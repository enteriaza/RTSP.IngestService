// A decoded frame in the decoded picture buffer: samples at the coded (macroblock-aligned) size, plus the motion data
// later pictures need for temporal direct prediction. Buffers are rented from the shared array pools and returned by
// Release; the decoder recycles pictures of the current size and releases them all on reset, size change and
// disposal. Samples never leave the decoder: output copies the cropped region into a VideoFrame the caller owns.

using System.Buffers;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Reference marking of a picture.</summary>
internal enum H264ReferenceMark
{
    /// <summary>Unused for reference.</summary>
    None,

    /// <summary>Used for short-term reference.</summary>
    ShortTerm,

    /// <summary>Used for long-term reference.</summary>
    LongTerm,
}

/// <summary>A frame in the decoded picture buffer.</summary>
internal sealed class H264Picture
{
    /// <summary>Initializes a new instance of the <see cref="H264Picture"/> class without buffers.</summary>
    /// <param name="widthInMbs">The width in macroblocks.</param>
    /// <param name="heightInMbs">The height in macroblocks.</param>
    public H264Picture(int widthInMbs, int heightInMbs)
    {
        WidthInMbs = widthInMbs;
        HeightInMbs = heightInMbs;
    }

    /// <summary>Gets the width in macroblocks.</summary>
    public int WidthInMbs { get; }

    /// <summary>Gets the height in macroblocks.</summary>
    public int HeightInMbs { get; }

    /// <summary>Gets the coded luma width.</summary>
    public int Width => WidthInMbs * 16;

    /// <summary>Gets the coded luma height.</summary>
    public int Height => HeightInMbs * 16;

    /// <summary>Gets the luma plane (stride <see cref="Width"/>), or an empty array before allocation.</summary>
    public byte[] Luma { get; private set; } = [];

    /// <summary>Gets the Cb plane (stride <see cref="Width"/> / 2).</summary>
    public byte[] Cb { get; private set; } = [];

    /// <summary>Gets the Cr plane (stride <see cref="Width"/> / 2).</summary>
    public byte[] Cr { get; private set; } = [];

    /// <summary>Gets motion vectors at <c>((list * blocks4x4) + block) * 2 + component</c>, blocks in picture raster order.</summary>
    public short[] Mv { get; private set; } = [];

    /// <summary>Gets reference indices at <c>list * blocks8x8 + block</c>; -1 when the list is not used.</summary>
    public sbyte[] RefIdx { get; private set; } = [];

    /// <summary>Gets the <see cref="Id"/> of the referenced picture, laid out as <see cref="RefIdx"/>.</summary>
    public long[] RefPicId { get; private set; } = [];

    /// <summary>Gets a flag per macroblock: 1 when intra coded.</summary>
    public byte[] IntraMb { get; private set; } = [];

    /// <summary>Gets a value indicating whether buffers are allocated.</summary>
    public bool IsAllocated => Luma.Length != 0;

    /// <summary>Gets or sets the decoder-unique identity used to compare references.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a "non-existing" frame inferred from a frame_num gap.</summary>
    public bool NonExisting { get; set; }

    /// <summary>Gets or sets a value indicating whether the picture is an IDR picture.</summary>
    public bool IsIdr { get; set; }

    /// <summary>Gets or sets a value indicating whether nal_ref_idc was non-zero.</summary>
    public bool IsReferencePicture { get; set; }

    /// <summary>Gets or sets frame_num.</summary>
    public int FrameNum { get; set; }

    /// <summary>Gets or sets FrameNumWrap for the picture being decoded.</summary>
    public int FrameNumWrap { get; set; }

    /// <summary>Gets or sets LongTermFrameIdx.</summary>
    public int LongTermFrameIdx { get; set; }

    /// <summary>Gets or sets the reference marking.</summary>
    public H264ReferenceMark Mark { get; set; }

    /// <summary>Gets or sets PicOrderCnt (the minimum of the field order counts).</summary>
    public int Poc { get; set; }

    /// <summary>Gets or sets TopFieldOrderCnt.</summary>
    public int TopPoc { get; set; }

    /// <summary>Gets or sets BottomFieldOrderCnt.</summary>
    public int BottomPoc { get; set; }

    /// <summary>Gets or sets a value indicating whether the picture awaits output.</summary>
    public bool NeededForOutput { get; set; }

    /// <summary>Gets or sets the presentation timestamp.</summary>
    public long Timestamp { get; set; }

    /// <summary>Gets or sets the timestamp time base.</summary>
    public MediaTimeBase TimeBase { get; set; }

    /// <summary>Gets or sets the SPS the picture was decoded with (crop and colour).</summary>
    public H264Sps? Sps { get; set; }

    /// <summary>Gets a value indicating whether the picture is marked as used for reference.</summary>
    public bool IsReference => Mark != H264ReferenceMark.None;

    /// <summary>Gets the number of bytes the buffers of a picture of this size occupy.</summary>
    /// <param name="widthInMbs">The width in macroblocks.</param>
    /// <param name="heightInMbs">The height in macroblocks.</param>
    /// <returns>The byte count.</returns>
    public static long ByteCount(int widthInMbs, int heightInMbs)
    {
        var mbs = (long)widthInMbs * heightInMbs;
        return (mbs * 384) + (mbs * 16 * 2 * 2 * sizeof(short)) + (mbs * 4 * 2 * (sizeof(sbyte) + sizeof(long))) + mbs;
    }

    /// <summary>Rents the buffers.</summary>
    public void Allocate()
    {
        var mbs = WidthInMbs * HeightInMbs;
        Luma = ArrayPool<byte>.Shared.Rent(mbs * 256);
        Cb = ArrayPool<byte>.Shared.Rent(mbs * 64);
        Cr = ArrayPool<byte>.Shared.Rent(mbs * 64);
        Mv = ArrayPool<short>.Shared.Rent(mbs * 16 * 2 * 2);
        RefIdx = ArrayPool<sbyte>.Shared.Rent(mbs * 4 * 2);
        RefPicId = ArrayPool<long>.Shared.Rent(mbs * 4 * 2);
        IntraMb = ArrayPool<byte>.Shared.Rent(mbs);
    }

    /// <summary>Returns the buffers; the picture must not be used for samples afterwards.</summary>
    public void Release()
    {
        if (!IsAllocated)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(Luma);
        ArrayPool<byte>.Shared.Return(Cb);
        ArrayPool<byte>.Shared.Return(Cr);
        ArrayPool<short>.Shared.Return(Mv);
        ArrayPool<sbyte>.Shared.Return(RefIdx);
        ArrayPool<long>.Shared.Return(RefPicId);
        ArrayPool<byte>.Shared.Return(IntraMb);
        Luma = [];
        Cb = [];
        Cr = [];
        Mv = [];
        RefIdx = [];
        RefPicId = [];
        IntraMb = [];
    }

    /// <summary>Clears the DPB state for reuse.</summary>
    public void ResetState()
    {
        NonExisting = false;
        IsIdr = false;
        IsReferencePicture = false;
        FrameNum = 0;
        FrameNumWrap = 0;
        LongTermFrameIdx = 0;
        Mark = H264ReferenceMark.None;
        Poc = 0;
        TopPoc = 0;
        BottomPoc = 0;
        NeededForOutput = false;
        Timestamp = 0;
        Sps = null;
    }
}
