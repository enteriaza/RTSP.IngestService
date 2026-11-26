// Decoded PCM. Samples are 32-bit float, nominal range -1 to 1 (not clipped), stored planar in one buffer rented from
// the shared array pool. The receiver of a frame owns it and must dispose it exactly once, which returns the buffer;
// spans obtained from it must not be used afterwards.

using System.Buffers;

namespace RTSP.IngestService.Media.Codecs;

/// <summary>Loudspeaker positions of decoded channels.</summary>
internal enum AudioChannel
{
    /// <summary>Front centre.</summary>
    FrontCenter,

    /// <summary>Front left.</summary>
    FrontLeft,

    /// <summary>Front right.</summary>
    FrontRight,

    /// <summary>Outside (wide) front left.</summary>
    FrontLeftWide,

    /// <summary>Outside (wide) front right.</summary>
    FrontRightWide,

    /// <summary>Left surround.</summary>
    SurroundLeft,

    /// <summary>Right surround.</summary>
    SurroundRight,

    /// <summary>Rear centre.</summary>
    BackCenter,

    /// <summary>Low-frequency effects.</summary>
    LowFrequency,
}

/// <summary>A block of decoded planar PCM that owns a pooled buffer.</summary>
internal sealed class AudioFrame : IDisposable
{
    /// <summary>The rented buffer: <see cref="ChannelCount"/> planes of <see cref="SampleCount"/> samples.</summary>
    private float[]? _samples;

    /// <summary>Initializes a new instance of the <see cref="AudioFrame"/> class with a rented, zeroed buffer.</summary>
    /// <param name="layout">The channel positions, one per plane.</param>
    /// <param name="sampleCount">Samples per channel.</param>
    /// <param name="sampleRate">The sample rate in hertz.</param>
    /// <param name="timestamp">The timestamp of the first sample in <paramref name="timeBase"/> ticks.</param>
    /// <param name="timeBase">The timestamp time base.</param>
    public AudioFrame(ReadOnlyMemory<AudioChannel> layout, int sampleCount, int sampleRate, long timestamp, MediaTimeBase timeBase)
    {
        ArgumentOutOfRangeException.ThrowIfZero(layout.Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        Layout = layout;
        SampleCount = sampleCount;
        SampleRate = sampleRate;
        Timestamp = timestamp;
        TimeBase = timeBase;
        _samples = ArrayPool<float>.Shared.Rent(layout.Length * sampleCount);
        _samples.AsSpan(0, layout.Length * sampleCount).Clear();
    }

    /// <summary>Gets the channel positions, one per plane.</summary>
    public ReadOnlyMemory<AudioChannel> Layout { get; }

    /// <summary>Gets the number of channels.</summary>
    public int ChannelCount => Layout.Length;

    /// <summary>Gets the samples per channel.</summary>
    public int SampleCount { get; }

    /// <summary>Gets the sample rate in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the timestamp of the first sample.</summary>
    public long Timestamp { get; }

    /// <summary>Gets the timestamp time base.</summary>
    public MediaTimeBase TimeBase { get; }

    /// <summary>Gets the frame duration in <see cref="TimeBase"/> ticks, rounded to nearest.</summary>
    public long Duration => new MediaTimeBase(1, SampleRate).Rescale(SampleCount, TimeBase);

    /// <summary>Gets one channel's samples.</summary>
    /// <param name="channel">The plane index.</param>
    /// <returns>The samples, valid until the frame is disposed.</returns>
    /// <exception cref="ObjectDisposedException">The frame was disposed.</exception>
    public Span<float> GetChannel(int channel)
    {
        ObjectDisposedException.ThrowIf(_samples is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);
        return _samples.AsSpan(channel * SampleCount, SampleCount);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _samples, null) is { } samples)
        {
            ArrayPool<float>.Shared.Return(samples);
        }
    }
}
