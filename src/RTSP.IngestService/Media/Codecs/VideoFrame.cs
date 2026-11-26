// Uncompressed video: planar 8-bit YUV 4:2:0 in one buffer rented from the shared array pool, luma plane first, then
// Cb and Cr at half width and half height. It is the output of the managed H.264 decoder and the intended input of a
// managed encoder. The receiver of a frame owns it and must dispose it exactly once, which returns the buffer; spans
// obtained from it must not be used afterwards.

using System.Buffers;

namespace RTSP.IngestService.Media.Codecs;

/// <summary>The sample layout of a <see cref="VideoFrame"/>.</summary>
internal enum VideoPixelFormat
{
    /// <summary>Planar Y, Cb, Cr; 8 bits per sample; chroma subsampled by two in both directions.</summary>
    Yuv420P8,
}

/// <summary>
/// The colour description of a frame, as ITU-T H.273 code points (the values of the H.264 VUI). 2 means unspecified.
/// </summary>
/// <param name="Primaries">colour_primaries; 1 is BT.709.</param>
/// <param name="Transfer">transfer_characteristics; 1 is BT.709.</param>
/// <param name="Matrix">matrix_coefficients; 1 is BT.709.</param>
/// <param name="FullRange">video_full_range_flag; <see langword="false"/> is limited (16-235) range.</param>
internal readonly record struct VideoColorInfo(byte Primaries, byte Transfer, byte Matrix, bool FullRange)
{
    /// <summary>No colour description: unspecified primaries, transfer and matrix, limited range.</summary>
    public static readonly VideoColorInfo Unspecified = new(2, 2, 2, false);

    /// <summary>BT.709 SDR, limited range: the canonical output colour.</summary>
    public static readonly VideoColorInfo Bt709 = new(1, 1, 1, false);
}

/// <summary>Counts the frames a producer has handed out and not yet seen disposed, and bounds that number.</summary>
internal sealed class VideoFrameBudget
{
    /// <summary>Frames currently alive.</summary>
    private int _outstanding;

    /// <summary>Initializes a new instance of the <see cref="VideoFrameBudget"/> class.</summary>
    /// <param name="maxOutstanding">The most frames that may be alive at once.</param>
    public VideoFrameBudget(int maxOutstanding)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutstanding);
        MaxOutstanding = maxOutstanding;
    }

    /// <summary>Gets the most frames that may be alive at once.</summary>
    public int MaxOutstanding { get; }

    /// <summary>Gets the number of frames alive.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>Reserves one frame.</summary>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.ResourceLimit"/>: the budget is exhausted.</exception>
    public void Acquire()
    {
        if (Interlocked.Increment(ref _outstanding) > MaxOutstanding)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new CodecException(CodecErrorKind.ResourceLimit, "Too many decoded video frames are outstanding.");
        }
    }

    /// <summary>Releases one frame.</summary>
    public void Release() => Interlocked.Decrement(ref _outstanding);
}

/// <summary>A planar 8-bit 4:2:0 picture that owns a pooled buffer.</summary>
internal sealed class VideoFrame : IDisposable
{
    /// <summary>The largest width or height accepted.</summary>
    public const int MaxDimension = 16384;

    /// <summary>The budget charged for this frame, if any.</summary>
    private readonly VideoFrameBudget? _budget;

    /// <summary>The rented buffer: Y, then Cb, then Cr.</summary>
    private byte[]? _buffer;

    /// <summary>Initializes a new instance of the <see cref="VideoFrame"/> class with a rented, uninitialised buffer.</summary>
    /// <param name="width">The width in luma samples; even, 2 to <see cref="MaxDimension"/>.</param>
    /// <param name="height">The height in luma samples; even, 2 to <see cref="MaxDimension"/>.</param>
    /// <param name="timestamp">The presentation timestamp in <paramref name="timeBase"/> ticks.</param>
    /// <param name="timeBase">The timestamp time base.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="budget">The budget to charge until the frame is disposed, or <see langword="null"/>.</param>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.ResourceLimit"/>: <paramref name="budget"/> is exhausted.</exception>
    public VideoFrame(int width, int height, long timestamp, MediaTimeBase timeBase, VideoColorInfo color, VideoFrameBudget? budget = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxDimension);
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("4:2:0 frames have even dimensions.", nameof(width));
        }

        Width = width;
        Height = height;
        Timestamp = timestamp;
        TimeBase = timeBase;
        Color = color;
        budget?.Acquire();
        _budget = budget;
        _buffer = ArrayPool<byte>.Shared.Rent(ByteCount);
    }

    /// <summary>Gets the sample layout.</summary>
    public VideoPixelFormat Format { get; } = VideoPixelFormat.Yuv420P8;

    /// <summary>Gets the bits per sample.</summary>
    public int BitDepth { get; } = 8;

    /// <summary>Gets the width in luma samples.</summary>
    public int Width { get; }

    /// <summary>Gets the height in luma samples.</summary>
    public int Height { get; }

    /// <summary>Gets the width of the chroma planes.</summary>
    public int ChromaWidth => Width / 2;

    /// <summary>Gets the height of the chroma planes.</summary>
    public int ChromaHeight => Height / 2;

    /// <summary>Gets the presentation timestamp.</summary>
    public long Timestamp { get; }

    /// <summary>Gets the timestamp time base.</summary>
    public MediaTimeBase TimeBase { get; }

    /// <summary>Gets the colour description.</summary>
    public VideoColorInfo Color { get; }

    /// <summary>Gets a value indicating whether the picture is a random access point (an H.264 IDR picture).</summary>
    public bool IsKeyframe { get; init; }

    /// <summary>Gets a value indicating whether the picture was used as a reference by the stream.</summary>
    public bool IsReference { get; init; }

    /// <summary>Gets the picture's position in presentation order, counted from zero per decoder instance.</summary>
    public long PresentationIndex { get; init; }

    /// <summary>Gets the total buffer size in bytes.</summary>
    public int ByteCount => (Width * Height) + (2 * ChromaWidth * ChromaHeight);

    /// <summary>Gets the distance in bytes between rows of a plane.</summary>
    /// <param name="plane">0 for Y, 1 for Cb, 2 for Cr.</param>
    /// <returns>The stride.</returns>
    public int GetStride(int plane) => PlaneIndex(plane) == 0 ? Width : ChromaWidth;

    /// <summary>Gets one plane's samples, row by row at <see cref="GetStride"/> bytes apart.</summary>
    /// <param name="plane">0 for Y, 1 for Cb, 2 for Cr.</param>
    /// <returns>The samples, valid until the frame is disposed.</returns>
    /// <exception cref="ObjectDisposedException">The frame was disposed.</exception>
    public Span<byte> GetPlane(int plane)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        var lumaSize = Width * Height;
        var chromaSize = ChromaWidth * ChromaHeight;
        return PlaneIndex(plane) switch
        {
            0 => _buffer.AsSpan(0, lumaSize),
            1 => _buffer.AsSpan(lumaSize, chromaSize),
            _ => _buffer.AsSpan(lumaSize + chromaSize, chromaSize),
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _budget?.Release();
        }
    }

    /// <summary>Validates a plane index.</summary>
    /// <param name="plane">The index.</param>
    /// <returns>The index.</returns>
    private static int PlaneIndex(int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plane, 2);
        return plane;
    }
}
