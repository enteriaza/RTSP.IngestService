// One reconstructed picture of the encoder with the per-macroblock and per-4x4-block state later macroblocks, the
// deblocking filter and later pictures read: planes padded to whole macroblocks (as the decoder stores them, so
// motion compensation clamps at the same edges), motion vectors, reference indices and picture identities, CAVLC
// coefficient counts, Intra4x4 modes, macroblock kinds, QPs and slice membership. All arrays are pooled and sized
// once from the picture size; nothing is allocated per macroblock or per picture after construction.

using System.Buffers;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>How a macroblock was coded, as the neighbour and deblocking derivations need it.</summary>
internal enum H264MbKind : byte
{
    /// <summary>P_Skip.</summary>
    Skip,

    /// <summary>An inter macroblock with coded motion.</summary>
    Inter,

    /// <summary>Intra_16x16.</summary>
    Intra16x16,

    /// <summary>Intra_4x4.</summary>
    Intra4x4,

    /// <summary>I_PCM.</summary>
    Pcm,
}

/// <summary>A reconstructed encoder picture and its coding state.</summary>
internal sealed class H264EncoderPicture : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="H264EncoderPicture"/> class.</summary>
    /// <param name="widthInMbs">Width in macroblocks.</param>
    /// <param name="heightInMbs">Height in macroblocks.</param>
    public H264EncoderPicture(int widthInMbs, int heightInMbs)
    {
        WidthInMbs = widthInMbs;
        HeightInMbs = heightInMbs;
        var mbs = widthInMbs * heightInMbs;
        Luma = Rent<byte>(Width * Height);
        Cb = Rent<byte>(Width * Height / 4);
        Cr = Rent<byte>(Width * Height / 4);
        Mv = Rent<short>(mbs * 32);
        RefIdx = Rent<sbyte>(mbs * 16);
        RefId = Rent<long>(mbs * 16);
        TotalCoeff = Rent<byte>(mbs * 16);
        ChromaCoeff = Rent<byte>(mbs * 8);
        IntraModes = Rent<sbyte>(mbs * 16);
        Kind = Rent<H264MbKind>(mbs);
        Qp = Rent<byte>(mbs);
        Slice = Rent<short>(mbs);
    }

    /// <summary>Gets the width in macroblocks.</summary>
    public int WidthInMbs { get; }

    /// <summary>Gets the height in macroblocks.</summary>
    public int HeightInMbs { get; }

    /// <summary>Gets the padded luma width, the luma stride.</summary>
    public int Width => WidthInMbs * 16;

    /// <summary>Gets the padded luma height.</summary>
    public int Height => HeightInMbs * 16;

    /// <summary>Gets the 4x4 blocks per row.</summary>
    public int BlocksPerRow => WidthInMbs * 4;

    /// <summary>Gets the luma plane.</summary>
    public byte[] Luma { get; private set; }

    /// <summary>Gets the Cb plane, stride <see cref="Width"/> / 2.</summary>
    public byte[] Cb { get; private set; }

    /// <summary>Gets the Cr plane.</summary>
    public byte[] Cr { get; private set; }

    /// <summary>Gets the motion vectors: x and y per 4x4 block, by picture block raster index.</summary>
    public short[] Mv { get; private set; }

    /// <summary>Gets the reference index per 4x4 block in its slice's list 0, or -1 for intra.</summary>
    public sbyte[] RefIdx { get; private set; }

    /// <summary>Gets the identity of the referenced picture per 4x4 block, or -1 for intra.</summary>
    public long[] RefId { get; private set; }

    /// <summary>Gets TotalCoeff of each luma 4x4 block (AC only for Intra16x16), 16 for I_PCM.</summary>
    public byte[] TotalCoeff { get; private set; }

    /// <summary>Gets TotalCoeff of each chroma AC 4x4 block: 4 Cb then 4 Cr per macroblock.</summary>
    public byte[] ChromaCoeff { get; private set; }

    /// <summary>Gets Intra4x4PredMode per luma 4x4 block, or -1 when the macroblock is not Intra_4x4.</summary>
    public sbyte[] IntraModes { get; private set; }

    /// <summary>Gets the kind of each macroblock.</summary>
    public H264MbKind[] Kind { get; private set; }

    /// <summary>Gets QPY of each macroblock (0 for I_PCM).</summary>
    public byte[] Qp { get; private set; }

    /// <summary>Gets the slice index of each macroblock.</summary>
    public short[] Slice { get; private set; }

    /// <summary>Gets or sets a process-unique identity, renewed for every coded picture.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets frame_num.</summary>
    public int FrameNum { get; set; }

    /// <summary>Gets or sets whether the picture is marked as used for short-term reference.</summary>
    public bool ShortTerm { get; set; }

    /// <summary>Gets or sets whether the picture is marked as used for long-term reference.</summary>
    public bool LongTerm { get; set; }

    /// <summary>Gets or sets LongTermFrameIdx.</summary>
    public int LongTermFrameIdx { get; set; }

    /// <summary>Gets or sets whether this is a non-existing frame inferred for a frame_num gap; it has no samples to reference.</summary>
    public bool NonExisting { get; set; }

    /// <summary>Gets whether the picture is a reference.</summary>
    public bool IsReference => ShortTerm || LongTerm;

    /// <summary>Gets the bytes held by one picture of the given size.</summary>
    /// <param name="widthInMbs">Width in macroblocks.</param>
    /// <param name="heightInMbs">Height in macroblocks.</param>
    /// <returns>The byte count: 384 sample bytes and 256 state bytes per macroblock.</returns>
    public static long ByteCount(int widthInMbs, int heightInMbs) => (long)widthInMbs * heightInMbs * (384 + 64 + 16 + 128 + 16 + 8 + 16 + 1 + 1 + 2);

    /// <summary>Returns the picture block raster index of a 4x4 block.</summary>
    /// <param name="mbx">Macroblock column.</param>
    /// <param name="mby">Macroblock row.</param>
    /// <param name="bx">Block column in the macroblock, 0 to 3.</param>
    /// <param name="by">Block row in the macroblock, 0 to 3.</param>
    /// <returns>The index.</returns>
    public int Block(int mbx, int mby, int bx, int by) => (((mby * 4) + by) * BlocksPerRow) + (mbx * 4) + bx;

    /// <inheritdoc/>
    public void Dispose()
    {
        Return(Luma);
        Return(Cb);
        Return(Cr);
        Return(Mv);
        Return(RefIdx);
        Return(RefId);
        Return(TotalCoeff);
        Return(ChromaCoeff);
        Return(IntraModes);
        Return(Kind);
        Return(Qp);
        Return(Slice);
        Luma = Cb = Cr = TotalCoeff = ChromaCoeff = Qp = [];
        Mv = [];
        RefIdx = IntraModes = [];
        RefId = [];
        Kind = [];
        Slice = [];
    }

    /// <summary>Rents a cleared array.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="length">The length.</param>
    /// <returns>The array.</returns>
    private static T[] Rent<T>(int length)
    {
        var array = ArrayPool<T>.Shared.Rent(length);
        Array.Clear(array);
        return array;
    }

    /// <summary>Returns an array to the pool.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="array">The array.</param>
    private static void Return<T>(T[] array)
    {
        if (array.Length != 0)
        {
            ArrayPool<T>.Shared.Return(array);
        }
    }
}
