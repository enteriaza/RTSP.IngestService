// MPEG-4 AAC-LC encoder for the canonical format, 48 kHz stereo (ISO/IEC 14496-3). Output is raw access units, one
// channel_pair_element plus ID_END per frame, described by the AudioSpecificConfig 0x11 0x90 (object type 2,
// frequency index 3, channel configuration 2, 1024-sample frames).
//  * Framing: input is buffered in 1024-sample blocks. Frame k is the MDCT of blocks k-1 and k (block -1 is the
//    silent priming block), so it decodes to input samples (k-1)*1024 .. k*1024-1. It is emitted once block k+1 is
//    complete, because block switching must know whether frame k+1 is short. Flush pads with silence and emits
//    frames up to ceil(N / 1024), so N input samples produce ceil(N / 1024) + 1 access units.
//  * Timestamps: access unit k carries anchor + (k - 1) * 1024 samples, so decoded output lines up with the input; the
//    first access unit starts 1024 samples (21.33 ms) before the first input sample and decodes to priming silence.
//  * Block switching: AacTransientDetector decides per frame; window sequences follow ONLY_LONG, LONG_START,
//    EIGHT_SHORT, LONG_STOP with the constraints of 4.6.11.3. Both channels share one sequence (common_window). The
//    window shape is always sine.
//  * Stereo: per band, mid/side (M = (L + R) / 2, S = (L - R) / 2) is chosen when its perceptual-entropy estimate
//    sum log2(1 + E / T) is lower than left/right's; M and S then share half the smaller L/R allowed noise.
//  * Rate control: a constrained average bitrate. Each frame earns bitrate * 1024 / 48000 bits; the frame budget is
//    that plus a quarter (long) or three quarters (short) of the bit reservoir, which holds unused bits up to
//    12288 - mean. A binary search finds the smallest global scalefactor offset (from -36, 54 dB finer than the
//    psychoacoustic target, upwards) whose exact coded size fits the budget. The long-term average never exceeds the
//    target, and no access unit exceeds 12288 bits (6144 per channel).
//  * Bandwidth: bands starting at or above a bitrate-dependent cutoff (12 kHz at 64 kbit/s to 20 kHz above
//    192 kbit/s) are not coded.
//  * TNS: a channel's frame may use it when the first-difference energy of its 2048 input samples, in sixteen
//    128-sample blocks, has an arithmetic-to-geometric mean ratio of at least 1 dB (a temporal envelope to shape:
//    voice onsets, pitch pulses, decays; a stationary tone is predictable across frequency too, but has none), and
//    the frame is ONLY_LONG, or LONG_STOP from 48 kbit/s (starved decays after an attack clip when the excess noise
//    is shaped into the attack). LONG_START frames never use it: their window overlaps the coming attack, and shaping
//    noise toward it is pre-echo. Nor do frames that include Flush's silent padding: the edge it creates is not
//    programme material, and shaped noise at a tone cut off there overshoots by up to 5 dB at 64 kbit/s.
//    AacChannelCoder then decides by prediction gain.
// Not used: intensity stereo, PNS, pulse data, KBD windows and short-window grouping.

using RTSP.IngestService.Media.Codecs.Audio.Processing;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;
using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;

/// <summary>One encoded AAC access unit.</summary>
/// <param name="Data">The raw access unit, owned by the receiver.</param>
/// <param name="Timestamp">The presentation timestamp of its decoded output.</param>
/// <param name="Duration">The duration, 1024 samples.</param>
/// <param name="TimeBase">The timestamp time base.</param>
internal readonly record struct AacAccessUnit(ReadOnlyMemory<byte> Data, long Timestamp, long Duration, MediaTimeBase TimeBase);

/// <summary>Counters describing what an encoder produced.</summary>
internal sealed class AacEncoderStatistics
{
    /// <summary>Gets the access units emitted.</summary>
    public long Frames { get; internal set; }

    /// <summary>Gets the frames coded with eight short windows.</summary>
    public long ShortFrames { get; internal set; }

    /// <summary>Gets the LONG_START and LONG_STOP transition frames.</summary>
    public long TransitionFrames { get; internal set; }

    /// <summary>Gets the channel frames coded with a TNS filter.</summary>
    public long TnsChannels { get; internal set; }

    /// <summary>Gets the bands coded as mid/side.</summary>
    public long MidSideBands { get; internal set; }

    /// <summary>Gets the total access-unit bits.</summary>
    public long Bits { get; internal set; }
}

/// <summary>An AAC-LC 48 kHz stereo encoder. Not thread-safe; independent instances may run concurrently.</summary>
internal sealed class AacEncoder : IDisposable
{
    /// <summary>The sample rate.</summary>
    public const int SampleRate = 48000;

    /// <summary>The samples per frame.</summary>
    public const int FrameLength = 1024;

    /// <summary>The encoder delay in samples: the priming frame.</summary>
    public const int Delay = 1024;

    /// <summary>The default bitrate in bits per second, matching <c>Transcoding:Audio:BitrateKilobits</c>.</summary>
    public const int DefaultBitrate = 160000;

    /// <summary>The lowest supported bitrate.</summary>
    public const int MinBitrate = 32000;

    /// <summary>The highest supported bitrate.</summary>
    public const int MaxBitrate = 512000;

    /// <summary>The largest access unit in bits: 6144 per channel.</summary>
    public const int MaxFrameBits = 6144 * 2;

    /// <summary>The finest global scalefactor offset the rate loop tries.</summary>
    private const int FinestOffset = -36;

    /// <summary>An offset at which every band quantises to zero.</summary>
    private const int SilentOffset = 300;

    /// <summary>
    /// The smallest mean frame size, in bits, at which LONG_STOP frames use TNS (48 kbit/s): below it the decay after
    /// an attack is starved, and shaping the excess noise into the attack clips.
    /// </summary>
    private const int LongStopTnsMinimumBits = 1024;

    /// <summary>
    /// The smallest temporal-envelope unevenness (<see cref="EnvelopeFlatnessDb"/>) for which a frame may use TNS: a
    /// stationary sine's spectrum is predictable across frequency too, but has no envelope to shape.
    /// </summary>
    private const double TnsMinimumEnvelopeDb = 1;

    /// <summary>Samples held: blocks k-1, k and k+1.</summary>
    private const int BufferLength = 3 * FrameLength;

    /// <summary>The 2048-point MDCT.</summary>
    private readonly ForwardMdct _long = new(2048, 2.0);

    /// <summary>The 256-point MDCT.</summary>
    private readonly ForwardMdct _short = new(256, 2.0);

    /// <summary>Input samples per channel in 16-bit scale.</summary>
    private readonly float[][] _buffer = [new float[BufferLength], new float[BufferLength]];

    /// <summary>The channel coders.</summary>
    private readonly AacChannelCoder[] _coders = [new(), new()];

    /// <summary>Windowed-input scratch.</summary>
    private readonly float[] _windowed = new float[2048];

    /// <summary>Mid/side flags per window and band.</summary>
    private readonly bool[] _midSide = new bool[8 * AacChannelCoder.BandStride];

    /// <summary>Bitstream scratch, large enough for any trial quantisation.</summary>
    private readonly byte[] _scratch = new byte[32768];

    /// <summary>The bits each frame earns.</summary>
    private readonly double _meanBits;

    /// <summary>Coded long-window bands.</summary>
    private readonly int _longBands;

    /// <summary>Coded short-window bands.</summary>
    private readonly int _shortBands;

    /// <summary>Samples in <see cref="_buffer"/>.</summary>
    private int _fill;

    /// <summary>The previous frame's window sequence.</summary>
    private AacWindowSequence _previous;

    /// <summary>Unused bits carried forward.</summary>
    private double _reservoir;

    /// <summary>Access units emitted in this stream.</summary>
    private long _frameIndex;

    /// <summary>Input samples received in this stream.</summary>
    private long _inputSamples;

    /// <summary>Whether a stream is in progress.</summary>
    private bool _started;

    /// <summary>The first input timestamp.</summary>
    private long _anchor;

    /// <summary>The stream time base.</summary>
    private MediaTimeBase _timeBase;

    /// <summary>Whether a failure left the state undefined.</summary>
    private bool _faulted;

    /// <summary>Whether the encoder was disposed.</summary>
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="AacEncoder"/> class.</summary>
    /// <param name="bitrate">The target bitrate in bits per second, <see cref="MinBitrate"/> to <see cref="MaxBitrate"/>.</param>
    public AacEncoder(int bitrate = DefaultBitrate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitrate, MinBitrate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitrate, MaxBitrate);
        Bitrate = bitrate;
        Bandwidth = bitrate switch
        {
            <= 32000 => 7000,
            <= 48000 => 10000,
            <= 64000 => 12000,
            <= 96000 => 14000,
            <= 128000 => 16000,
            <= 160000 => 17000,
            <= 192000 => 18000,
            _ => 20000,
        };
        _meanBits = bitrate * (double)FrameLength / SampleRate;
        _longBands = CountBands(AacTables.LongBandOffsets[3], AacTables.LongWindowLength);
        _shortBands = CountBands(AacTables.ShortBandOffsets[3], AacTables.ShortWindowLength);
    }

    /// <summary>Gets the AudioSpecificConfig: AAC-LC, 48 kHz, two channels.</summary>
    public static ReadOnlySpan<byte> AudioSpecificConfig => [0x11, 0x90];

    /// <summary>Gets the target bitrate in bits per second.</summary>
    public int Bitrate { get; }

    /// <summary>Gets the coded audio bandwidth in hertz.</summary>
    public int Bandwidth { get; }

    /// <summary>Gets what the encoder has produced.</summary>
    public AacEncoderStatistics Statistics { get; } = new();

    /// <summary>Encodes canonical audio. The first frame after construction or <see cref="Flush"/> anchors the timeline.</summary>
    /// <param name="frame">48 kHz stereo (front left, front right) float samples; the caller keeps ownership.</param>
    /// <param name="output">Receives the completed access units, in order.</param>
    public void Encode(AudioFrame frame, ICollection<AacAccessUnit> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfUnusable();
        if (frame.SampleRate != SampleRate || !frame.Layout.Span.SequenceEqual(StereoDownmixer.StereoLayout.Span))
        {
            throw new ArgumentException("The AAC encoder accepts 48 kHz stereo (front left, front right) only.", nameof(frame));
        }

        try
        {
            if (!_started)
            {
                _started = true;
                _anchor = frame.Timestamp;
                _timeBase = frame.TimeBase;
                _buffer[0].AsSpan(0, FrameLength).Clear();
                _buffer[1].AsSpan(0, FrameLength).Clear();
                _fill = FrameLength;
            }

            var left = frame.GetChannel(0);
            var right = frame.GetChannel(1);
            var offset = 0;
            while (offset < frame.SampleCount)
            {
                var count = Math.Min(frame.SampleCount - offset, BufferLength - _fill);
                Scale(left.Slice(offset, count), _buffer[0].AsSpan(_fill, count));
                Scale(right.Slice(offset, count), _buffer[1].AsSpan(_fill, count));
                _fill += count;
                offset += count;
                _inputSamples += count;
                if (_fill == BufferLength)
                {
                    EmitFrame(output);
                }
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Ends the stream: pads with silence and emits the remaining access units, then resets for a new stream.</summary>
    /// <param name="output">Receives the remaining access units, in order.</param>
    public void Flush(ICollection<AacAccessUnit> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfUnusable();
        try
        {
            if (_started)
            {
                var frames = ((_inputSamples + FrameLength - 1) / FrameLength) + 1;
                while (_frameIndex < frames)
                {
                    _buffer[0].AsSpan(_fill).Clear();
                    _buffer[1].AsSpan(_fill).Clear();
                    _fill = BufferLength;
                    EmitFrame(output);
                }
            }

            _started = false;
            _frameIndex = 0;
            _inputSamples = 0;
            _reservoir = 0;
            _previous = AacWindowSequence.OnlyLong;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true;

    /// <summary>Counts the bands that start below the bandwidth.</summary>
    /// <param name="offsets">The band offsets.</param>
    /// <param name="windowLength">Coefficients per window.</param>
    /// <returns>The band count.</returns>
    private int CountBands(int[] offsets, int windowLength)
    {
        var bands = 0;
        while (bands < offsets.Length - 1 && offsets[bands] * (SampleRate / 2.0) / windowLength < Bandwidth)
        {
            bands++;
        }

        return bands;
    }

    /// <summary>Converts nominal float samples to the 16-bit scale the AAC filterbank is defined in.</summary>
    /// <param name="input">The samples.</param>
    /// <param name="output">The scaled samples.</param>
    private static void Scale(ReadOnlySpan<float> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[i] * AudioSampleFormat.Int16Scale;
        }
    }

    /// <summary>Chooses frame k's window sequence (4.6.11.3: START precedes and STOP follows short frames).</summary>
    /// <param name="previous">The previous frame's sequence.</param>
    /// <param name="attack">Whether frame k has an attack.</param>
    /// <param name="nextAttack">Whether frame k+1 has an attack.</param>
    /// <returns>The sequence.</returns>
    private static AacWindowSequence ChooseSequence(AacWindowSequence previous, bool attack, bool nextAttack)
    {
        var afterShort = previous is AacWindowSequence.LongStart or AacWindowSequence.EightShort;
        if (attack || (afterShort && nextAttack))
        {
            return AacWindowSequence.EightShort;
        }

        return afterShort ? AacWindowSequence.LongStop : nextAttack ? AacWindowSequence.LongStart : AacWindowSequence.OnlyLong;
    }

    /// <summary>Throws if the encoder was disposed or faulted.</summary>
    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("The AAC encoder failed earlier and must be replaced.");
        }
    }

    /// <summary>Codes frame k from blocks k-1 and k, then drops block k-1.</summary>
    /// <param name="output">Receives the access unit.</param>
    private void EmitFrame(ICollection<AacAccessUnit> output)
    {
        var attack = HasAttack(AacTransientDetector.RegionStart);
        var nextAttack = HasAttack(AacTransientDetector.RegionStart + FrameLength);
        var sequence = ChooseSequence(_previous, attack, nextAttack);
        var layout = sequence == AacWindowSequence.EightShort
            ? new AacFrameLayout(8, AacTables.ShortWindowLength, AacTables.ShortBandOffsets[3], _shortBands)
            : new AacFrameLayout(1, AacTables.LongWindowLength, AacTables.LongBandOffsets[3], _longBands);

        for (var c = 0; c < 2; c++)
        {
            var coder = _coders[c];
            Analyse(_buffer[c], sequence, coder.Spectrum);
            for (var w = 0; w < layout.Windows; w++)
            {
                AacPsychoacousticModel.Analyse(
                    coder.Spectrum.AsSpan(w * layout.WindowLength, layout.WindowLength),
                    layout.Offsets,
                    layout.MaxSfb,
                    coder.Energy.AsSpan(w * AacChannelCoder.BandStride, AacChannelCoder.BandStride),
                    coder.Threshold.AsSpan(w * AacChannelCoder.BandStride, AacChannelCoder.BandStride));
            }

            var padded = (_frameIndex + 1) * FrameLength > _inputSamples;
            var tns = !padded && (sequence == AacWindowSequence.OnlyLong || (sequence == AacWindowSequence.LongStop && _meanBits >= LongStopTnsMinimumBits));
            coder.ApplyTns(layout, tns && EnvelopeFlatnessDb(_buffer[c]) >= TnsMinimumEnvelopeDb);
            Statistics.TnsChannels += coder.HasTns ? 1 : 0;
        }

        var midSide = ChooseStereo(layout);
        _coders[0].Prepare(layout);
        _coders[1].Prepare(layout);

        var share = sequence == AacWindowSequence.EightShort ? 0.75 : 0.25;
        var budget = (int)Math.Min(MaxFrameBits, _meanBits + (_reservoir * share));
        var best = FinestOffset;
        if (Measure(best, sequence, layout, midSide) > budget)
        {
            var fits = SilentOffset;
            while (fits - best > 1)
            {
                var middle = (best + fits) / 2;
                if (Measure(middle, sequence, layout, midSide) <= budget)
                {
                    fits = middle;
                }
                else
                {
                    best = middle;
                }
            }

            best = fits;
        }

        var bytes = Measure(best, sequence, layout, midSide) / 8;
        if (bytes * 8 > budget)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "An AAC frame does not fit the bit budget.");
        }

        var timestamp = TimestampOf(_frameIndex);
        output.Add(new AacAccessUnit(_scratch.AsSpan(0, bytes).ToArray(), timestamp, TimestampOf(_frameIndex + 1) - timestamp, _timeBase));

        _reservoir = Math.Min(_reservoir + _meanBits - (bytes * 8), MaxFrameBits - _meanBits);
        _previous = sequence;
        _frameIndex++;
        Statistics.Frames++;
        Statistics.Bits += bytes * 8;
        Statistics.ShortFrames += sequence == AacWindowSequence.EightShort ? 1 : 0;
        Statistics.TransitionFrames += sequence is AacWindowSequence.LongStart or AacWindowSequence.LongStop ? 1 : 0;

        for (var c = 0; c < 2; c++)
        {
            _buffer[c].AsSpan(FrameLength).CopyTo(_buffer[c]);
        }

        _fill -= FrameLength;
    }

    /// <summary>The timestamp of access unit <paramref name="index"/>: anchor + (index - 1) * 1024 samples.</summary>
    /// <param name="index">The access unit index.</param>
    /// <returns>The timestamp.</returns>
    private long TimestampOf(long index) => _anchor + new MediaTimeBase(1, SampleRate).Rescale((index - 1) * FrameLength, _timeBase);

    /// <summary>
    /// The temporal-envelope unevenness of frame k's 2048 input samples: 10 log10 of the arithmetic over the geometric
    /// mean of the first-difference energy in sixteen 128-sample blocks. 0 dB for a stationary signal.
    /// </summary>
    /// <param name="samples">The channel buffer, blocks k-1 and k first.</param>
    /// <returns>The unevenness in decibels.</returns>
    private static double EnvelopeFlatnessDb(float[] samples)
    {
        const int blocks = 16;
        const int blockLength = 2 * FrameLength / blocks;
        double arithmetic = 0, logarithmic = 0;
        for (var b = 0; b < blocks; b++)
        {
            var energy = 1.0;
            for (var n = Math.Max(1, b * blockLength); n < (b + 1) * blockLength; n++)
            {
                var difference = samples[n] - samples[n - 1];
                energy += difference * difference;
            }

            arithmetic += energy / blocks;
            logarithmic += Math.Log(energy) / blocks;
        }

        return 10 * Math.Log10(arithmetic / Math.Exp(logarithmic));
    }

    /// <summary>Whether either channel has an attack in the region starting at a buffer offset.</summary>
    /// <param name="start">The buffer offset.</param>
    /// <returns><see langword="true"/> on an attack.</returns>
    private bool HasAttack(int start) =>
        AacTransientDetector.HasAttack(_buffer[0].AsSpan(start, AacTransientDetector.RegionLength))
        || AacTransientDetector.HasAttack(_buffer[1].AsSpan(start, AacTransientDetector.RegionLength));

    /// <summary>Windows blocks k-1 and k with the analysis window of <paramref name="sequence"/> and transforms them.</summary>
    /// <param name="samples">The channel buffer.</param>
    /// <param name="sequence">The window sequence.</param>
    /// <param name="spectrum">1024 coefficients, window-major for short windows.</param>
    private void Analyse(float[] samples, AacWindowSequence sequence, float[] spectrum)
    {
        var rising = AacWindows.Long[0];
        var shortRising = AacWindows.Short[0];
        var windowed = _windowed.AsSpan();
        if (sequence == AacWindowSequence.EightShort)
        {
            for (var w = 0; w < 8; w++)
            {
                var segment = samples.AsSpan(448 + (w * 128), 256);
                for (var n = 0; n < 128; n++)
                {
                    windowed[n] = segment[n] * shortRising[n];
                    windowed[128 + n] = segment[128 + n] * shortRising[127 - n];
                }

                _short.Transform(windowed[..256], spectrum.AsSpan(w * 128, 128));
            }

            return;
        }

        for (var n = 0; n < 1024; n++)
        {
            windowed[n] = sequence != AacWindowSequence.LongStop
                ? samples[n] * rising[n]
                : n < 448 ? 0 : n < 576 ? samples[n] * shortRising[n - 448] : samples[n];
        }

        for (var n = 1024; n < 2048; n++)
        {
            windowed[n] = sequence != AacWindowSequence.LongStart
                ? samples[n] * rising[2047 - n]
                : n < 1472 ? samples[n] : n < 1600 ? samples[n] * shortRising[1599 - n] : 0;
        }

        _long.Transform(windowed, spectrum);
    }

    /// <summary>Chooses mid/side per band and converts those bands' spectra, energies and allowed noise.</summary>
    /// <param name="layout">The frame layout.</param>
    /// <returns>Whether any band uses mid/side.</returns>
    private bool ChooseStereo(AacFrameLayout layout)
    {
        var left = _coders[0];
        var right = _coders[1];
        var any = false;
        for (var w = 0; w < layout.Windows; w++)
        {
            for (var b = 0; b < layout.MaxSfb; b++)
            {
                var slot = (w * AacChannelCoder.BandStride) + b;
                var start = (w * layout.WindowLength) + layout.Offsets[b];
                var end = (w * layout.WindowLength) + layout.Offsets[b + 1];
                var mid = 0f;
                var side = 0f;
                for (var i = start; i < end; i++)
                {
                    var m = (left.Spectrum[i] + right.Spectrum[i]) / 2;
                    var s = (left.Spectrum[i] - right.Spectrum[i]) / 2;
                    mid += m * m;
                    side += s * s;
                }

                var shared = Math.Min(left.Threshold[slot], right.Threshold[slot]) / 2;
                var leftRight = Entropy(left.Energy[slot], left.Threshold[slot]) + Entropy(right.Energy[slot], right.Threshold[slot]);
                var midSide = Entropy(mid, shared) + Entropy(side, shared);
                _midSide[slot] = midSide < leftRight;
                if (!_midSide[slot])
                {
                    continue;
                }

                any = true;
                Statistics.MidSideBands++;
                for (var i = start; i < end; i++)
                {
                    var l = left.Spectrum[i];
                    var r = right.Spectrum[i];
                    left.Spectrum[i] = (l + r) / 2;
                    right.Spectrum[i] = (l - r) / 2;
                }

                left.Energy[slot] = mid;
                right.Energy[slot] = side;
                left.Threshold[slot] = shared;
                right.Threshold[slot] = shared;
            }
        }

        return any;
    }

    /// <summary>A perceptual-entropy estimate log2(1 + E / T).</summary>
    /// <param name="energy">The band energy.</param>
    /// <param name="threshold">The allowed noise.</param>
    /// <returns>The estimate.</returns>
    private static double Entropy(float energy, float threshold) => Math.Log2(1 + (energy / threshold));

    /// <summary>Quantises both channels with a global offset and writes the access unit into the scratch buffer.</summary>
    /// <param name="offset">The global scalefactor offset.</param>
    /// <param name="sequence">The window sequence.</param>
    /// <param name="layout">The frame layout.</param>
    /// <param name="midSide">Whether any band uses mid/side.</param>
    /// <returns>The access unit size in bits, a multiple of 8.</returns>
    private int Measure(int offset, AacWindowSequence sequence, AacFrameLayout layout, bool midSide)
    {
        _coders[0].Quantise(offset);
        _coders[1].Quantise(offset);
        var writer = new BitstreamWriter(_scratch);
        writer.Write(1, 3);
        writer.Write(0, 4);
        writer.WriteBit(true);
        writer.WriteBit(false);
        writer.Write((uint)sequence, 2);
        writer.WriteBit(false);
        if (layout.Windows == 8)
        {
            writer.Write((uint)layout.MaxSfb, 4);
            writer.Write(0, 7);
        }
        else
        {
            writer.Write((uint)layout.MaxSfb, 6);
            writer.WriteBit(false);
        }

        writer.Write(midSide ? 1u : 0u, 2);
        if (midSide)
        {
            for (var w = 0; w < layout.Windows; w++)
            {
                for (var b = 0; b < layout.MaxSfb; b++)
                {
                    writer.WriteBit(_midSide[(w * AacChannelCoder.BandStride) + b]);
                }
            }
        }

        _coders[0].Write(ref writer);
        _coders[1].Write(ref writer);
        writer.Write(7, 3);
        writer.AlignToByte();
        return writer.BytesWritten * 8;
    }
}
