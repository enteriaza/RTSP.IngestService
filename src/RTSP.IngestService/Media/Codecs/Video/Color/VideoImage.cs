// A read-only description of an uncompressed picture in any layout the colour converter accepts: Y'CbCr 4:2:0,
// 4:2:2 or 4:4:4, or R'G'B', at 8 to 16 bits per sample, with its ITU-T H.273 colour description and, for HDR
// sources, the content peak luminance when known. It borrows the caller's planes or frame (no copy) and is valid
// only while they are. VideoFrame stays the 8-bit 4:2:0 decoder contract; this type exists so wider inputs need
// not widen it. Samples above the bit depth's maximum are read as the maximum.

namespace RTSP.IngestService.Media.Codecs.Video.Color;

/// <summary>The plane layout of a <see cref="VideoImage"/>.</summary>
internal enum VideoImageLayout
{
    /// <summary>Y', Cb, Cr; chroma subsampled by two in both directions.</summary>
    Yuv420,

    /// <summary>Y', Cb, Cr; chroma subsampled by two horizontally.</summary>
    Yuv422,

    /// <summary>Y', Cb, Cr at full resolution.</summary>
    Yuv444,

    /// <summary>R', G', B' at full resolution, in that plane order; the matrix must be identity (H.273 code 0).</summary>
    Rgb,
}

/// <summary>A borrowed uncompressed picture with its colour description.</summary>
internal sealed class VideoImage
{
    /// <summary>The 8-bit planes, or null.</summary>
    private readonly ReadOnlyMemory<byte>[]? _bytes;

    /// <summary>The 9- to 16-bit planes, or null.</summary>
    private readonly ReadOnlyMemory<ushort>[]? _words;

    /// <summary>The borrowed frame, or null.</summary>
    private readonly VideoFrame? _frame;

    /// <summary>The row strides in samples.</summary>
    private readonly int[] _strides;

    /// <summary>Initializes a new instance of the <see cref="VideoImage"/> class over planes.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="bitDepth">The bit depth.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="peakNits">The content peak luminance, or null.</param>
    /// <param name="bytes">8-bit planes.</param>
    /// <param name="words">Wider planes.</param>
    /// <param name="strides">The strides.</param>
    private VideoImage(int width, int height, VideoImageLayout layout, int bitDepth, VideoColorInfo color, double? peakNits, ReadOnlyMemory<byte>[]? bytes, ReadOnlyMemory<ushort>[]? words, int[] strides)
    {
        if (width is < 1 or > VideoFrame.MaxDimension || height is < 1 or > VideoFrame.MaxDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The image size must be 1 to 16384 in each direction.");
        }

        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        if (peakNits is { } peak && !(peak >= 1 && peak <= 10000))
        {
            throw new ArgumentOutOfRangeException(nameof(peakNits), "The peak luminance must be 1 to 10000 cd/m2.");
        }

        Width = width;
        Height = height;
        Layout = layout;
        BitDepth = bitDepth;
        Color = color;
        PeakNits = peakNits;
        _bytes = bytes;
        _words = words;
        _strides = strides;
        var count = bytes?.Length ?? words!.Length;
        if (count != 3 || strides.Length != 3)
        {
            throw new ArgumentException("An image has exactly three planes and three strides.", nameof(strides));
        }

        for (var plane = 0; plane < 3; plane++)
        {
            int planeWidth = PlaneWidth(plane), planeHeight = PlaneHeight(plane);
            var length = bytes?[plane].Length ?? words![plane].Length;
            if (strides[plane] < planeWidth || ((long)strides[plane] * (planeHeight - 1)) + planeWidth > length)
            {
                throw new ArgumentException($"Plane {plane} is smaller than its stride and size require.", nameof(strides));
            }
        }
    }

    /// <summary>Initializes a new instance of the <see cref="VideoImage"/> class over a frame.</summary>
    /// <param name="frame">The frame.</param>
    private VideoImage(VideoFrame frame)
    {
        Width = frame.Width;
        Height = frame.Height;
        Layout = VideoImageLayout.Yuv420;
        BitDepth = 8;
        Color = frame.Color;
        _frame = frame;
        _strides = [frame.Width, frame.ChromaWidth, frame.ChromaWidth];
    }

    /// <summary>Gets the width in samples.</summary>
    public int Width { get; }

    /// <summary>Gets the height in samples.</summary>
    public int Height { get; }

    /// <summary>Gets the layout.</summary>
    public VideoImageLayout Layout { get; }

    /// <summary>Gets the bits per sample, 8 to 16.</summary>
    public int BitDepth { get; }

    /// <summary>Gets the colour description.</summary>
    public VideoColorInfo Color { get; }

    /// <summary>Gets the content peak luminance in cd/m2 (for example MaxCLL or the mastering display maximum), or null when unknown.</summary>
    public double? PeakNits { get; }

    /// <summary>Gets the horizontal chroma subsampling shift.</summary>
    internal int ChromaShiftX => Layout is VideoImageLayout.Yuv420 or VideoImageLayout.Yuv422 ? 1 : 0;

    /// <summary>Gets the vertical chroma subsampling shift.</summary>
    internal int ChromaShiftY => Layout == VideoImageLayout.Yuv420 ? 1 : 0;

    /// <summary>Gets a value indicating whether the planes are bytes.</summary>
    internal bool IsBytes => _bytes is not null || _frame is not null;

    /// <summary>Describes 8-bit planes.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="planes">Three planes.</param>
    /// <param name="strides">Three strides in samples.</param>
    /// <param name="peakNits">The content peak luminance, or null.</param>
    /// <returns>The image.</returns>
    public static VideoImage FromBytes(int width, int height, VideoImageLayout layout, VideoColorInfo color, ReadOnlyMemory<byte>[] planes, int[] strides, double? peakNits = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        return new VideoImage(width, height, layout, 8, color, peakNits, [.. planes], null, [.. strides]);
    }

    /// <summary>Describes 9- to 16-bit planes, one sample per 16-bit word, least significant bits aligned.</summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="bitDepth">The bit depth, 9 to 16.</param>
    /// <param name="color">The colour description.</param>
    /// <param name="planes">Three planes.</param>
    /// <param name="strides">Three strides in samples.</param>
    /// <param name="peakNits">The content peak luminance, or null.</param>
    /// <returns>The image.</returns>
    public static VideoImage FromWords(int width, int height, VideoImageLayout layout, int bitDepth, VideoColorInfo color, ReadOnlyMemory<ushort>[] planes, int[] strides, double? peakNits = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        ArgumentOutOfRangeException.ThrowIfLessThan(bitDepth, 9);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitDepth, 16);
        return new VideoImage(width, height, layout, bitDepth, color, peakNits, null, [.. planes], [.. strides]);
    }

    /// <summary>Describes a frame's planes without copying; valid until the frame is disposed.</summary>
    /// <param name="frame">The frame.</param>
    /// <returns>The image.</returns>
    public static VideoImage FromFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _ = frame.GetPlane(0);
        return new VideoImage(frame);
    }

    /// <summary>Gets the width of a plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The width in samples.</returns>
    internal int PlaneWidth(int plane) => plane == 0 ? Width : (Width + (1 << ChromaShiftX) - 1) >> ChromaShiftX;

    /// <summary>Gets the height of a plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The height in samples.</returns>
    internal int PlaneHeight(int plane) => plane == 0 ? Height : (Height + (1 << ChromaShiftY) - 1) >> ChromaShiftY;

    /// <summary>Gets an 8-bit plane row.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="y">The row.</param>
    /// <returns>The row.</returns>
    internal ReadOnlySpan<byte> ByteRow(int plane, int y) =>
        (_frame is not null ? (ReadOnlySpan<byte>)_frame.GetPlane(plane) : _bytes![plane].Span).Slice(y * _strides[plane], PlaneWidth(plane));

    /// <summary>Reads a plane row as integers, clamped to the bit depth.</summary>
    /// <param name="plane">The plane.</param>
    /// <param name="y">The row.</param>
    /// <param name="destination">Receives <see cref="PlaneWidth"/> samples.</param>
    internal void ReadRow(int plane, int y, Span<int> destination)
    {
        var width = PlaneWidth(plane);
        if (IsBytes)
        {
            var row = ByteRow(plane, y);
            for (var x = 0; x < width; x++)
            {
                destination[x] = row[x];
            }

            return;
        }

        var max = (1 << BitDepth) - 1;
        var words = _words![plane].Span.Slice(y * _strides[plane], width);
        for (var x = 0; x < width; x++)
        {
            destination[x] = Math.Min(words[x], max);
        }
    }
}
