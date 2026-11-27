// The complete managed audio path from any supported AAC-LC stream to the canonical output: managed AAC decode,
// stereo downmix, resampling to 48 kHz, clipping and AAC-LC encode, as one synchronous call. The ingest transcoding
// session uses the same codecs through ManagedAudioStage; this type backs the codec smoke command and the end-to-end
// tests.

using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Audio.Processing;

namespace RTSP.IngestService.Media.Codecs.Audio;

/// <summary>Transcodes one AAC-LC stream to canonical 48 kHz stereo AAC-LC. Not thread-safe.</summary>
internal sealed class ManagedAudioTranscoder : IDisposable
{
    /// <summary>The decoder.</summary>
    private readonly AacDecoder _decoder;

    /// <summary>The canonical converter.</summary>
    private readonly CanonicalAudioConverter _converter;

    /// <summary>The encoder.</summary>
    private readonly AacEncoder _encoder;

    /// <summary>The sum of squared canonical samples, per channel.</summary>
    private readonly double[] _canonicalEnergy = new double[2];

    /// <summary>Initializes a new instance of the <see cref="ManagedAudioTranscoder"/> class.</summary>
    /// <param name="audioSpecificConfig">The source AudioSpecificConfig.</param>
    /// <param name="bitrate">The output bitrate in bits per second.</param>
    public ManagedAudioTranscoder(ReadOnlySpan<byte> audioSpecificConfig, int bitrate = AacEncoder.DefaultBitrate)
    {
        _decoder = AacDecoder.Create(audioSpecificConfig);
        _converter = new CanonicalAudioConverter(_decoder.Layout.Span, _decoder.SampleRate);
        _encoder = new AacEncoder(bitrate);
    }

    /// <summary>Gets the source sample rate.</summary>
    public int SourceSampleRate => _decoder.SampleRate;

    /// <summary>Gets the source channel count.</summary>
    public int SourceChannels => _decoder.Layout.Length;

    /// <summary>Gets the decoded source samples per channel.</summary>
    public long DecodedSamples { get; private set; }

    /// <summary>Gets the canonical samples per channel passed to the encoder.</summary>
    public long CanonicalSamples { get; private set; }

    /// <summary>Gets the samples clipped by the canonical converter.</summary>
    public long ClippedSamples => _converter.ClippedSamples;

    /// <summary>Gets the encoder statistics.</summary>
    public AacEncoderStatistics EncoderStatistics => _encoder.Statistics;

    /// <summary>The RMS of the canonical samples passed to the encoder.</summary>
    /// <param name="channel">0 (left) or 1 (right).</param>
    /// <returns>The RMS, 0 before any samples.</returns>
    public double CanonicalRms(int channel) => CanonicalSamples == 0 ? 0 : Math.Sqrt(_canonicalEnergy[channel] / CanonicalSamples);

    /// <summary>Transcodes one source access unit.</summary>
    /// <param name="packet">The source access unit.</param>
    /// <param name="output">Receives completed canonical access units.</param>
    public void Transcode(in EncodedPacket packet, ICollection<AacAccessUnit> output)
    {
        using var decoded = _decoder.Decode(packet);
        DecodedSamples += decoded.SampleCount;
        Encode(_converter.Process(decoded), output);
    }

    /// <summary>Ends the stream and emits the remaining access units.</summary>
    /// <param name="output">Receives the remaining access units.</param>
    public void Flush(ICollection<AacAccessUnit> output)
    {
        Encode(_converter.Flush(), output);
        _encoder.Flush(output);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _decoder.Dispose();
        _encoder.Dispose();
    }

    /// <summary>Encodes and disposes a canonical frame.</summary>
    /// <param name="frame">The frame, or <see langword="null"/>.</param>
    /// <param name="output">Receives access units.</param>
    private void Encode(AudioFrame? frame, ICollection<AacAccessUnit> output)
    {
        if (frame is null)
        {
            return;
        }

        using (frame)
        {
            CanonicalSamples += frame.SampleCount;
            for (var c = 0; c < 2; c++)
            {
                foreach (var sample in frame.GetChannel(c))
                {
                    _canonicalEnergy[c] += sample * (double)sample;
                }
            }

            _encoder.Encode(frame, output);
        }
    }
}
