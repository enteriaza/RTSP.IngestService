// Managed AAC-LC decoder (ISO/IEC 14496-3 subpart 4, Low Complexity object type 2, 1024-sample frames) for the raw
// access units carried by FLV/RTMP and MP4, configured by an AudioSpecificConfig. Supported: channel configurations
// 1 to 7 (SCE/CPE/LFE), all 13 indexed sampling frequencies, long, start, stop and eight-short window sequences, sine
// and KBD windows, M/S and intensity stereo, perceptual noise substitution, TNS, pulse data, DSE, FIL and PCE skipping.
// Rejected as unsupported: other object types (Main, SSR, LTP, ER, LD), 960-sample frames, channel configuration 0,
// explicit sampling frequencies, coupling channel elements, and SBR/PS (HE-AAC) whether signalled explicitly or found
// in a fill element. Each access unit decodes to exactly one 1024-sample frame; there is no decoder delay, so a flush
// produces nothing and only resets the overlap state. After any CodecException the decoder is faulted.

using System.Diagnostics;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;

/// <summary>Counts of coding tools seen, for diagnostics and tests.</summary>
internal sealed class AacToolUsage
{
    /// <summary>Gets or sets the frames decoded.</summary>
    public long Frames { get; set; }

    /// <summary>Gets or sets the channel frames with eight short windows.</summary>
    public long ShortWindowChannels { get; set; }

    /// <summary>Gets or sets the channel frames with a start or stop window.</summary>
    public long TransitionWindowChannels { get; set; }

    /// <summary>Gets or sets the channel frames using the KBD window.</summary>
    public long KbdWindowChannels { get; set; }

    /// <summary>Gets or sets the channel frames with TNS.</summary>
    public long TnsChannels { get; set; }

    /// <summary>Gets or sets the channel frames with pulse data.</summary>
    public long PulseChannels { get; set; }

    /// <summary>Gets or sets the noise-substituted bands.</summary>
    public long NoiseBands { get; set; }

    /// <summary>Gets or sets the mid/side bands.</summary>
    public long MidSideBands { get; set; }

    /// <summary>Gets or sets the intensity bands.</summary>
    public long IntensityBands { get; set; }
}

/// <summary>An AAC-LC decoder for one stream. Not thread-safe; one instance per publisher track.</summary>
internal sealed class AacDecoder : IDisposable
{
    /// <summary>Samples per frame.</summary>
    public const int FrameLength = 1024;

    /// <summary>Largest accepted access unit: 6144 bits per channel (4.5.3.1) for 8 channels.</summary>
    public const int MaxAccessUnitSize = 6144 / 8 * 8;

    /// <summary>Element types of <c>raw_data_block()</c>.</summary>
    private const int SingleChannelElement = 0;

    /// <summary>Channel pair element.</summary>
    private const int ChannelPairElement = 1;

    /// <summary>Coupling channel element.</summary>
    private const int CouplingChannelElement = 2;

    /// <summary>LFE channel element.</summary>
    private const int LfeChannelElement = 3;

    /// <summary>Data stream element.</summary>
    private const int DataStreamElement = 4;

    /// <summary>Program config element.</summary>
    private const int ProgramConfigElement = 5;

    /// <summary>Fill element.</summary>
    private const int FillElement = 6;

    /// <summary>End of the block.</summary>
    private const int EndElement = 7;

    /// <summary>Element sequence per channel configuration.</summary>
    private static readonly int[][] ElementsByConfiguration =
    [
        [],
        [SingleChannelElement],
        [ChannelPairElement],
        [SingleChannelElement, ChannelPairElement],
        [SingleChannelElement, ChannelPairElement, SingleChannelElement],
        [SingleChannelElement, ChannelPairElement, ChannelPairElement],
        [SingleChannelElement, ChannelPairElement, ChannelPairElement, LfeChannelElement],
        [SingleChannelElement, ChannelPairElement, ChannelPairElement, ChannelPairElement, LfeChannelElement],
    ];

    /// <summary>Channel positions per channel configuration, in bitstream order (Table 1.19).</summary>
    private static readonly AudioChannel[][] LayoutsByConfiguration =
    [
        [],
        [AudioChannel.FrontCenter],
        [AudioChannel.FrontLeft, AudioChannel.FrontRight],
        [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight],
        [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.BackCenter],
        [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.SurroundLeft, AudioChannel.SurroundRight],
        [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.SurroundLeft, AudioChannel.SurroundRight, AudioChannel.LowFrequency],
        [AudioChannel.FrontCenter, AudioChannel.FrontLeft, AudioChannel.FrontRight, AudioChannel.FrontLeftWide, AudioChannel.FrontRightWide, AudioChannel.SurroundLeft, AudioChannel.SurroundRight, AudioChannel.LowFrequency],
    ];

    /// <summary>The expected element sequence.</summary>
    private readonly int[] _elements;

    /// <summary>Per-channel spectra.</summary>
    private readonly AacChannelStream[] _streams;

    /// <summary>Per-channel overlap from the previous frame.</summary>
    private readonly float[][] _overlap;

    /// <summary>Per-channel previous window shape.</summary>
    private readonly int[] _previousShape;

    /// <summary>The synthesis filterbank.</summary>
    private readonly AacFilterbank _filterbank = new();

    /// <summary>M/S flags of the current pair, per group and band.</summary>
    private readonly bool[] _midSide = new bool[8 * AacChannelStream.MaxBands];

    /// <summary>The channel layout.</summary>
    private readonly AudioChannel[] _layout;

    /// <summary>Noise generator state.</summary>
    private uint _random = 0x1F2E3D4C;

    /// <summary>Whether a failure left the state undefined.</summary>
    private bool _faulted;

    /// <summary>Whether the decoder was disposed.</summary>
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="AacDecoder"/> class.</summary>
    /// <param name="frequencyIndex">The sampling frequency index.</param>
    /// <param name="channelConfiguration">The channel configuration, 1 to 7.</param>
    private AacDecoder(int frequencyIndex, int channelConfiguration)
    {
        SampleRate = AacTables.SampleRates[frequencyIndex];
        _elements = ElementsByConfiguration[channelConfiguration];
        _layout = LayoutsByConfiguration[channelConfiguration];
        _streams = new AacChannelStream[_layout.Length];
        _overlap = new float[_layout.Length][];
        _previousShape = new int[_layout.Length];
        for (var i = 0; i < _layout.Length; i++)
        {
            _streams[i] = new AacChannelStream(frequencyIndex);
            _overlap[i] = new float[FrameLength];
        }
    }

    /// <summary>Gets the output sample rate in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the output channel positions, in bitstream order.</summary>
    public ReadOnlyMemory<AudioChannel> Layout => _layout;

    /// <summary>Gets the coding tools seen so far.</summary>
    public AacToolUsage Usage { get; } = new();

    /// <summary>Creates a decoder for an AudioSpecificConfig.</summary>
    /// <param name="audioSpecificConfig">The AudioSpecificConfig (FLV AAC sequence header body).</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="CodecException">The configuration is invalid or not supported.</exception>
    public static AacDecoder Create(ReadOnlySpan<byte> audioSpecificConfig)
    {
        if (!AacAudioConfiguration.TryParse(audioSpecificConfig, out var configuration, out var error))
        {
            CodecException.InvalidData(error);
        }

        if (configuration.ExtensionAudioObjectType is { } extension)
        {
            CodecException.Unsupported($"HE-AAC (audio object type {extension}, SBR{(extension == 29 ? " and PS" : string.Empty)}) is not supported; only AAC-LC is decoded.");
        }

        if (configuration.AudioObjectType != 2)
        {
            CodecException.Unsupported($"AAC audio object type {configuration.AudioObjectType} is not supported; only AAC-LC (2) is decoded.");
        }

        if (configuration.SamplingFrequencyIndex == 15)
        {
            CodecException.Unsupported("An explicit AAC sampling frequency is not supported; an indexed frequency is required.");
        }

        if (configuration.ChannelConfiguration is < 1 or > 7)
        {
            CodecException.Unsupported($"AAC channel configuration {configuration.ChannelConfiguration} is not supported; 1 to 7 are.");
        }

        var reader = new BitstreamReader(audioSpecificConfig);
        reader.Skip(5 + 4 + 4);
        if (reader.Remaining < 3)
        {
            CodecException.InvalidData("The AAC-LC AudioSpecificConfig ends before its GASpecificConfig.");
        }

        if (reader.ReadBit())
        {
            CodecException.Unsupported("960-sample AAC frames are not supported.");
        }

        if (reader.ReadBit())
        {
            CodecException.Unsupported("AAC configured with dependsOnCoreCoder is not supported.");
        }

        if (reader.ReadBit())
        {
            CodecException.InvalidData("The GASpecificConfig extensionFlag is set, which AAC-LC does not allow.");
        }

        return new AacDecoder(configuration.SamplingFrequencyIndex, configuration.ChannelConfiguration);
    }

    /// <summary>Decodes one access unit (<c>raw_data_block()</c>) into 1024 samples per channel.</summary>
    /// <param name="packet">The access unit and its timestamp.</param>
    /// <returns>The frame, owned by the caller, timestamped with the packet's timestamp.</returns>
    /// <exception cref="CodecException">Invalid or unsupported data; the decoder is then faulted.</exception>
    /// <exception cref="InvalidOperationException">The decoder is faulted.</exception>
    public AudioFrame Decode(in EncodedPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("The AAC decoder failed earlier and must be replaced.");
        }

        if (packet.Data.Length > MaxAccessUnitSize)
        {
            _faulted = true;
            throw new CodecException(CodecErrorKind.ResourceLimit, $"The AAC access unit is {packet.Data.Length} bytes; at most {MaxAccessUnitSize} are accepted.");
        }

        var frame = new AudioFrame(_layout, FrameLength, SampleRate, packet.Timestamp, packet.TimeBase);
        try
        {
            DecodeBlock(packet.Data.Span, frame);
            Usage.Frames++;
            return frame;
        }
        catch
        {
            _faulted = true;
            frame.Dispose();
            throw;
        }
    }

    /// <summary>Discards the overlap state, as after a discontinuity. Nothing is buffered, so no frame is produced.</summary>
    /// <exception cref="InvalidOperationException">The decoder is faulted.</exception>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("The AAC decoder failed earlier and must be replaced.");
        }

        foreach (var overlap in _overlap)
        {
            Array.Clear(overlap);
        }

        Array.Clear(_previousShape);
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true;

    /// <summary>Decodes a raw data block into <paramref name="frame"/>.</summary>
    /// <param name="data">The access unit.</param>
    /// <param name="frame">The output frame.</param>
    private void DecodeBlock(ReadOnlySpan<byte> data, AudioFrame frame)
    {
        var reader = new BitstreamReader(data);
        var element = 0;
        var channel = 0;
        while (true)
        {
            var type = (int)reader.Read(3);
            switch (type)
            {
                case SingleChannelElement or LfeChannelElement or ChannelPairElement:
                    if (element >= _elements.Length || _elements[element] != type)
                    {
                        CodecException.InvalidData($"The AAC access unit has an unexpected element of type {type} for its channel configuration.");
                    }

                    reader.Skip(4);
                    if (type == ChannelPairElement)
                    {
                        DecodePair(ref reader, channel);
                        channel += 2;
                    }
                    else
                    {
                        var stream = _streams[channel];
                        stream.Read(ref reader, commonWindow: false, allowIntensity: false);
                        stream.Dequantise();
                        Usage.NoiseBands += stream.FillNoise(ref _random, null, default);
                        channel++;
                    }

                    element++;
                    break;
                case CouplingChannelElement:
                    CodecException.Unsupported("AAC coupling channel elements are not supported.");
                    break;
                case DataStreamElement:
                    SkipDataStream(ref reader);
                    break;
                case ProgramConfigElement:
                    SkipProgramConfig(ref reader);
                    break;
                case FillElement:
                    SkipFill(ref reader);
                    break;
                default:
                    Debug.Assert(type == EndElement, "Element types are three bits.");
                    if (element != _elements.Length)
                    {
                        CodecException.InvalidData($"The AAC access unit has {element} channel elements; its configuration requires {_elements.Length}.");
                    }

                    Synthesize(frame);
                    return;
            }
        }
    }

    /// <summary>Decodes a <c>channel_pair_element()</c> after its tag, including M/S and intensity stereo.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="channel">The first channel.</param>
    private void DecodePair(ref BitstreamReader reader, int channel)
    {
        var left = _streams[channel];
        var right = _streams[channel + 1];
        var commonWindow = reader.ReadBit();
        var maskPresent = 0;
        Array.Clear(_midSide);
        if (commonWindow)
        {
            left.ReadInfo(ref reader);
            right.CopyInfo(left);
            maskPresent = (int)reader.Read(2);
            if (maskPresent == 3)
            {
                CodecException.InvalidData("ms_mask_present 3 is reserved.");
            }

            for (var g = 0; g < left.GroupCount; g++)
            {
                for (var band = 0; band < left.MaxSfb; band++)
                {
                    _midSide[(g * AacChannelStream.MaxBands) + band] = maskPresent == 2 || (maskPresent == 1 && reader.ReadBit());
                }
            }
        }

        left.Read(ref reader, commonWindow, allowIntensity: false);
        right.Read(ref reader, commonWindow, allowIntensity: true);
        if (!commonWindow)
        {
            CheckNoIntensity(right);
        }

        left.Dequantise();
        right.Dequantise();
        Usage.NoiseBands += left.FillNoise(ref _random, null, default);
        Span<bool> correlated = stackalloc bool[8 * AacChannelStream.MaxBands];
        if (commonWindow)
        {
            for (var i = 0; i < correlated.Length; i++)
            {
                correlated[i] = _midSide[i] && left.BandType[i] == AacChannelStream.NoiseBand && right.BandType[i] == AacChannelStream.NoiseBand;
            }
        }

        Usage.NoiseBands += right.FillNoise(ref _random, commonWindow ? left : null, correlated);
        if (commonWindow)
        {
            ApplyStereo(left, right, maskPresent);
        }
    }

    /// <summary>Rejects intensity bands in a pair without a common window, where they are meaningless.</summary>
    /// <param name="right">The second channel.</param>
    private static void CheckNoIntensity(AacChannelStream right)
    {
        for (var g = 0; g < right.GroupCount; g++)
        {
            for (var band = 0; band < right.MaxSfb; band++)
            {
                if (right.BandType[(g * AacChannelStream.MaxBands) + band] >= AacChannelStream.IntensityBand2)
                {
                    CodecException.InvalidData("Intensity stereo requires a common window.");
                }
            }
        }
    }

    /// <summary>Applies M/S (4.6.8.1) then intensity stereo (4.6.8.2.3) to a common-window pair.</summary>
    /// <param name="left">The first channel.</param>
    /// <param name="right">The second channel.</param>
    /// <param name="maskPresent">The <c>ms_mask_present</c> value.</param>
    private void ApplyStereo(AacChannelStream left, AacChannelStream right, int maskPresent)
    {
        var windowLength = left.WindowLength;
        var window = 0;
        for (var g = 0; g < left.GroupCount; g++)
        {
            for (var band = 0; band < left.MaxSfb; band++)
            {
                var index = (g * AacChannelStream.MaxBands) + band;
                var rightType = right.BandType[index];
                var intensity = rightType >= AacChannelStream.IntensityBand2;
                var midSide = _midSide[index] && !intensity && left.BandType[index] != AacChannelStream.NoiseBand && rightType != AacChannelStream.NoiseBand;
                if (!intensity && !midSide)
                {
                    continue;
                }

                var scale = 0f;
                if (intensity)
                {
                    var sign = rightType == AacChannelStream.IntensityBand ? 1 : -1;
                    if (maskPresent == 1 && _midSide[index])
                    {
                        sign = -sign;
                    }

                    scale = (float)(sign * Math.Pow(2, -0.25 * right.ScaleFactor[index]));
                    Usage.IntensityBands++;
                }
                else
                {
                    Usage.MidSideBands++;
                }

                for (var w = 0; w < left.GroupLength[g]; w++)
                {
                    var start = ((window + w) * windowLength) + left.BandOffsets[band];
                    var end = ((window + w) * windowLength) + left.BandOffsets[band + 1];
                    for (var k = start; k < end; k++)
                    {
                        if (intensity)
                        {
                            right.Spectrum[k] = left.Spectrum[k] * scale;
                        }
                        else
                        {
                            var mid = left.Spectrum[k];
                            var side = right.Spectrum[k];
                            left.Spectrum[k] = mid + side;
                            right.Spectrum[k] = mid - side;
                        }
                    }
                }
            }

            window += left.GroupLength[g];
        }
    }

    /// <summary>Runs TNS and the filterbank for every channel into the frame.</summary>
    /// <param name="frame">The frame.</param>
    private void Synthesize(AudioFrame frame)
    {
        for (var channel = 0; channel < _streams.Length; channel++)
        {
            var stream = _streams[channel];
            stream.ApplyTns();
            _filterbank.Synthesize(stream.Spectrum, stream.WindowSequence, stream.WindowShape, _previousShape[channel], _overlap[channel], frame.GetChannel(channel));
            _previousShape[channel] = stream.WindowShape;
            Usage.ShortWindowChannels += stream.WindowSequence == AacWindowSequence.EightShort ? 1 : 0;
            Usage.TransitionWindowChannels += stream.WindowSequence is AacWindowSequence.LongStart or AacWindowSequence.LongStop ? 1 : 0;
            Usage.KbdWindowChannels += stream.WindowShape;
            Usage.TnsChannels += stream.HasTns ? 1 : 0;
            Usage.PulseChannels += stream.HasPulses ? 1 : 0;
        }
    }

    /// <summary>Skips a <c>data_stream_element()</c> after its type.</summary>
    /// <param name="reader">The reader.</param>
    private static void SkipDataStream(ref BitstreamReader reader)
    {
        reader.Skip(4);
        var align = reader.ReadBit();
        var count = (int)reader.Read(8);
        if (count == 255)
        {
            count += (int)reader.Read(8);
        }

        if (align)
        {
            reader.AlignToByte();
        }

        reader.Skip(count * 8);
    }

    /// <summary>Skips a <c>fill_element()</c>, rejecting SBR extension payloads (implicitly signalled HE-AAC).</summary>
    /// <param name="reader">The reader.</param>
    private static void SkipFill(ref BitstreamReader reader)
    {
        var count = (int)reader.Read(4);
        if (count == 15)
        {
            count += (int)reader.Read(8) - 1;
        }

        if (count > 0 && reader.Peek(4) is 13 or 14)
        {
            CodecException.Unsupported("The AAC stream carries SBR data (HE-AAC), which is not supported; only AAC-LC is decoded.");
        }

        reader.Skip(count * 8);
    }

    /// <summary>Skips a <c>program_config_element()</c> after its type (4.4.1.1).</summary>
    /// <param name="reader">The reader.</param>
    private static void SkipProgramConfig(ref BitstreamReader reader)
    {
        reader.Skip(4 + 2 + 4);
        var front = (int)reader.Read(4);
        var side = (int)reader.Read(4);
        var back = (int)reader.Read(4);
        var lfe = (int)reader.Read(2);
        var data = (int)reader.Read(3);
        var coupling = (int)reader.Read(4);
        if (reader.ReadBit())
        {
            reader.Skip(4);
        }

        if (reader.ReadBit())
        {
            reader.Skip(4);
        }

        if (reader.ReadBit())
        {
            reader.Skip(3);
        }

        reader.Skip(((front + side + back) * 5) + (lfe * 4) + (data * 4) + (coupling * 5));
        reader.AlignToByte();
        reader.Skip((int)reader.Read(8) * 8);
    }
}
