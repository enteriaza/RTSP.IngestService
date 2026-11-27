// One encoded access unit in a pooled buffer. The receiver owns it and disposes it exactly once, which returns the
// buffer and releases the encoder's outstanding-unit budget; Data must not be used afterwards.

using System.Buffers;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>An encoded picture: every NAL unit of one access unit, framed as configured.</summary>
internal sealed class H264EncodedAccessUnit : IDisposable
{
    /// <summary>The rented buffer.</summary>
    private byte[]? _buffer;

    /// <summary>The release callback of the owning encoder's budget.</summary>
    private Action? _release;

    /// <summary>Initializes a new instance of the <see cref="H264EncodedAccessUnit"/> class.</summary>
    /// <param name="buffer">The rented buffer; ownership passes to the unit.</param>
    /// <param name="length">The bytes used.</param>
    /// <param name="release">Called once on disposal.</param>
    internal H264EncodedAccessUnit(byte[] buffer, int length, Action release)
    {
        _buffer = buffer;
        Length = length;
        _release = release;
    }

    /// <summary>Gets the payload bytes.</summary>
    /// <exception cref="ObjectDisposedException">The unit was disposed.</exception>
    public ReadOnlyMemory<byte> Data => _buffer is { } buffer ? buffer.AsMemory(0, Length) : throw new ObjectDisposedException(nameof(H264EncodedAccessUnit));

    /// <summary>Gets the payload size.</summary>
    public int Length { get; }

    /// <summary>Gets the presentation timestamp, which equals the decode timestamp, in <see cref="H264Encoder.TimeBase"/> (1/90000) ticks.</summary>
    public required long Timestamp { get; init; }

    /// <summary>Gets whether the access unit is an IDR picture, a sync point.</summary>
    public required bool IsKeyframe { get; init; }

    /// <summary>Gets the framing of <see cref="Data"/>.</summary>
    public required H264OutputFormat Format { get; init; }

    /// <summary>Gets the parameter-set version the unit was coded with (<see cref="H264Encoder.ConfigurationVersion"/>).</summary>
    public required int ConfigurationVersion { get; init; }

    /// <summary>Gets the rate-control diagnostics of the picture.</summary>
    public required H264FrameStatistics Statistics { get; init; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
