// Converts decoded audio of one layout and rate to the canonical encoder input: 48 kHz stereo planar float, clipped to
// [-1, 1]. Stages run in the order downmix, resample, clip: downmixing first halves or better the resampling work,
// and clipping last catches resampler overshoot (Gibbs ringing) on full-scale input. Each stage is a separate class
// that can be used and tested on its own.

namespace RTSP.IngestService.Media.Codecs.Audio.Processing;

/// <summary>Converts one stream to canonical 48 kHz stereo float. Not thread-safe.</summary>
internal sealed class CanonicalAudioConverter
{
    /// <summary>The canonical sample rate.</summary>
    public const int SampleRate = 48000;

    /// <summary>The downmixer, or <see langword="null"/> when the input is already stereo.</summary>
    private readonly StereoDownmixer? _downmixer;

    /// <summary>The resampler.</summary>
    private readonly AudioResampler _resampler;

    /// <summary>Initializes a new instance of the <see cref="CanonicalAudioConverter"/> class.</summary>
    /// <param name="layout">The input channel positions.</param>
    /// <param name="sampleRate">The input sample rate in hertz.</param>
    public CanonicalAudioConverter(ReadOnlySpan<AudioChannel> layout, int sampleRate)
    {
        var stereo = layout.SequenceEqual(StereoDownmixer.StereoLayout.Span);
        _downmixer = stereo ? null : new StereoDownmixer(layout);
        _resampler = new AudioResampler(sampleRate, SampleRate, 2);
    }

    /// <summary>Gets the samples clipped since construction.</summary>
    public long ClippedSamples { get; private set; }

    /// <summary>Converts one frame.</summary>
    /// <param name="input">The decoded frame; the caller keeps ownership.</param>
    /// <returns>A canonical frame owned by the caller, or <see langword="null"/> while the resampler fills its look-ahead.</returns>
    public AudioFrame? Process(AudioFrame input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_downmixer is null)
        {
            return Clip(_resampler.Process(input));
        }

        using var stereo = _downmixer.Process(input);
        return Clip(_resampler.Process(stereo));
    }

    /// <summary>Ends the stream and returns the resampler's remaining output.</summary>
    /// <returns>The remaining canonical frame, or <see langword="null"/>.</returns>
    public AudioFrame? Flush() => Clip(_resampler.Flush());

    /// <summary>Discards resampler state, for example after a timestamp discontinuity.</summary>
    public void Reset() => _resampler.Reset();

    /// <summary>Clips a frame in place.</summary>
    /// <param name="frame">The frame, or <see langword="null"/>.</param>
    /// <returns>The same frame.</returns>
    private AudioFrame? Clip(AudioFrame? frame)
    {
        if (frame is not null)
        {
            ClippedSamples += AudioSampleFormat.Clip(frame.GetChannel(0)) + AudioSampleFormat.Clip(frame.GetChannel(1));
        }

        return frame;
    }
}
