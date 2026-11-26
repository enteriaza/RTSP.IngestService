// Slice data decoding (ITU-T H.264 7.3.4, 7.3.5) for progressive frames. One instance per coded picture size holds
// the per-macroblock state of the picture being decoded: what neighbouring macroblocks contribute to entropy
// contexts, prediction and deblocking. Grids are in picture raster order: macroblocks (W x H), 4x4 luma blocks
// (4W x 4H), 8x8 blocks (2W x 2H) and 4x4 chroma blocks (2W x 2H per component). A macroblock is "available" to
// another exactly when it belongs to the same slice and has been decoded (6.4.1); within the current macroblock,
// a 4x4 block's motion is available once its partition has been derived.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Macroblock kinds.</summary>
internal enum H264MbKind : byte
{
    /// <summary>Not decoded.</summary>
    None,

    /// <summary>I_NxN with 4x4 transform.</summary>
    I4x4,

    /// <summary>I_NxN with 8x8 transform.</summary>
    I8x8,

    /// <summary>Intra_16x16.</summary>
    I16x16,

    /// <summary>I_PCM.</summary>
    IPcm,

    /// <summary>P_Skip.</summary>
    PSkip,

    /// <summary>Coded P macroblock.</summary>
    PInter,

    /// <summary>B_Skip.</summary>
    BSkip,

    /// <summary>B_Direct_16x16.</summary>
    BDirect,

    /// <summary>Other coded B macroblock.</summary>
    BInter,
}

/// <summary>Decodes the slices of one picture.</summary>
internal sealed partial class H264SliceDecoder
{
    /// <summary>The largest |coefficient level| or scaled coefficient a conforming 8-bit stream contains (7.4.5.3).</summary>
    internal const int MaxCoefficient = 1 << 15;

    /// <summary>The most set-or-clear bits tolerated between the CABAC stop bit and the last set bit of a slice RBSP.</summary>
    internal const int MaxCabacTrailingBits = 16;

    /// <summary>Width in macroblocks.</summary>
    internal readonly int W;

    /// <summary>Height in macroblocks.</summary>
    internal readonly int H;

    /// <summary>Macroblocks per picture.</summary>
    internal readonly int MbCount;

    /// <summary>Width of the 4x4 grid.</summary>
    internal readonly int W4;

    /// <summary>Width of the 8x8 and chroma 4x4 grids.</summary>
    internal readonly int W8;

    /// <summary>Slice number per macroblock; -1 before it is decoded.</summary>
    internal readonly int[] MbSlice;

    /// <summary>Kind per macroblock.</summary>
    internal readonly H264MbKind[] MbKindOf;

    /// <summary>coded_block_pattern per macroblock.</summary>
    internal readonly byte[] MbCbp;

    /// <summary>QPY per macroblock as used by deblocking (0 for I_PCM).</summary>
    internal readonly byte[] MbQp;

    /// <summary>transform_size_8x8_flag per macroblock.</summary>
    internal readonly bool[] MbTransform8x8;

    /// <summary>intra_chroma_pred_mode per macroblock.</summary>
    internal readonly byte[] MbChromaMode;

    /// <summary>coded_block_flag of DC blocks per macroblock: bit 0 luma, bits 1 and 2 Cb and Cr.</summary>
    internal readonly byte[] MbDcCoded;

    /// <summary>TotalCoeff per 4x4 luma block (CAVLC nC).</summary>
    internal readonly byte[] TotalCoeff;

    /// <summary>1 per 4x4 luma block whose transform block has non-zero coefficients (8x8 blocks mark all four).</summary>
    internal readonly byte[] Coded;

    /// <summary>Intra4x4/8x8 prediction mode per 4x4 luma block.</summary>
    internal readonly sbyte[] IntraMode;

    /// <summary>TotalCoeff per chroma 4x4 AC block, Cb grid then Cr grid.</summary>
    internal readonly byte[] ChromaTotalCoeff;

    /// <summary>|mvd| per list, 4x4 block and component, capped (CABAC contexts).</summary>
    internal readonly byte[] MvdAbs;

    /// <summary>1 per 8x8 block predicted in direct mode.</summary>
    internal readonly byte[] Direct8x8;

    /// <summary>Deblocking parameters per slice of the picture.</summary>
    private readonly List<SliceFilter> _slices = [];

    /// <summary>CABAC context states.</summary>
    private readonly byte[] _cabacStates = new byte[H264CabacTables.ContextCount];

    /// <summary>Scratch for one residual block's levels.</summary>
    private readonly int[] _levels = new int[64];

    /// <summary>Scratch for I_PCM samples.</summary>
    private readonly byte[] _pcm = new byte[384];

    /// <summary>Initializes a new instance of the <see cref="H264SliceDecoder"/> class.</summary>
    /// <param name="widthInMbs">The width in macroblocks.</param>
    /// <param name="heightInMbs">The height in macroblocks.</param>
    public H264SliceDecoder(int widthInMbs, int heightInMbs)
    {
        W = widthInMbs;
        H = heightInMbs;
        MbCount = W * H;
        W4 = W * 4;
        W8 = W * 2;
        MbSlice = new int[MbCount];
        MbKindOf = new H264MbKind[MbCount];
        MbCbp = new byte[MbCount];
        MbQp = new byte[MbCount];
        MbTransform8x8 = new bool[MbCount];
        MbChromaMode = new byte[MbCount];
        MbDcCoded = new byte[MbCount];
        TotalCoeff = new byte[MbCount * 16];
        Coded = new byte[MbCount * 16];
        IntraMode = new sbyte[MbCount * 16];
        ChromaTotalCoeff = new byte[MbCount * 8];
        MvdAbs = new byte[MbCount * 16 * 2 * 2];
        Direct8x8 = new byte[MbCount * 4];
        InitializeScratch();
    }

    /// <summary>Gets the number of macroblocks of the current picture decoded so far.</summary>
    public int DecodedMbCount { get; private set; }

    /// <summary>Gets the 4x4 block column within a macroblock by luma4x4BlkIdx.</summary>
    internal static ReadOnlySpan<byte> BlkX => [0, 1, 0, 1, 2, 3, 2, 3, 0, 1, 0, 1, 2, 3, 2, 3];

    /// <summary>Gets the 4x4 block row within a macroblock by luma4x4BlkIdx.</summary>
    internal static ReadOnlySpan<byte> BlkY => [0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 3, 3, 2, 2, 3, 3];

    /// <summary>Gets the current macroblock address.</summary>
    internal int MbAddr { get; private set; }

    /// <summary>Gets the current macroblock column.</summary>
    internal int MbX { get; private set; }

    /// <summary>Gets the current macroblock row.</summary>
    internal int MbY { get; private set; }

    /// <summary>Gets the kind of the current macroblock.</summary>
    internal H264MbKind Kind { get; private set; }

    /// <summary>Gets the current slice type.</summary>
    internal H264SliceType SliceType { get; private set; }

    /// <summary>Gets a value indicating whether the previous macroblock of the slice had a non-zero mb_qp_delta.</summary>
    internal bool PrevQpDeltaNonZero { get; private set; }

    /// <summary>Gets num_ref_idx_active per list.</summary>
    internal int[] NumRefIdxActive { get; } = new int[2];

    /// <summary>Gets a value indicating whether the current macroblock is intra coded.</summary>
    internal bool CurrentIsIntra => IsIntra(Kind);

    /// <summary>Gets a value indicating whether the current macroblock is the intra I_NxN type (CAVLC me(v) mapping).</summary>
    internal bool CurrentIsIntraNxN => Kind is H264MbKind.I4x4 or H264MbKind.I8x8;

    /// <summary>Returns whether a kind is intra.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for intra kinds.</returns>
    internal static bool IsIntra(H264MbKind kind) => kind is H264MbKind.I4x4 or H264MbKind.I8x8 or H264MbKind.I16x16 or H264MbKind.IPcm;

    /// <summary>Returns whether a kind is a skipped macroblock.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> for P_Skip and B_Skip.</returns>
    internal static bool IsSkip(H264MbKind kind) => kind is H264MbKind.PSkip or H264MbKind.BSkip;

    /// <summary>Prepares for a new picture.</summary>
    /// <param name="picture">The picture to decode into.</param>
    public void BeginPicture(H264Picture picture)
    {
        _pic = picture;
        Array.Fill(MbSlice, -1);
        _slices.Clear();
        DecodedMbCount = 0;
    }

    /// <summary>Decodes one slice of the current picture.</summary>
    /// <param name="rbsp">The slice RBSP.</param>
    /// <param name="payloadBits">The position of the rbsp_stop_one_bit.</param>
    /// <param name="dataStart">The bit position of slice_data().</param>
    /// <param name="header">The parsed slice header.</param>
    /// <param name="list0">RefPicList0, <c>header.NumRefIdxActive[0]</c> entries; null entries are "no reference picture".</param>
    /// <param name="list1">RefPicList1.</param>
    /// <exception cref="CodecException">The slice data is invalid.</exception>
    public void DecodeSlice(ReadOnlySpan<byte> rbsp, int payloadBits, int dataStart, H264SliceHeader header, H264Picture?[] list0, H264Picture?[] list1)
    {
        if (_slices.Count >= MbCount)
        {
            CodecException.InvalidData("A picture has more slices than macroblocks.");
        }

        BeginSlice(header, list0, list1);
        var reader = new BitstreamReader(rbsp);
        reader.Skip(dataStart);
        if (header.Pps.Cabac)
        {
            while ((reader.Position & 7) != 0)
            {
                if (!reader.ReadBit())
                {
                    CodecException.InvalidData("cabac_alignment_one_bit is not one.");
                }
            }

            H264CabacReader.InitializeContexts(_cabacStates, SliceType == H264SliceType.I ? 0 : header.CabacInitIdc + 1, header.SliceQp);
            var syntax = new H264CabacSyntax(new H264CabacReader(reader, _cabacStates));
            CabacLoop(ref syntax, rbsp, payloadBits);
        }
        else
        {
            var syntax = new H264CavlcSyntax(reader);
            CavlcLoop(ref syntax, payloadBits);
        }
    }

    /// <summary>Returns whether the macroblock at (mbx, mby) is available to the current macroblock.</summary>
    /// <param name="mbx">The column.</param>
    /// <param name="mby">The row.</param>
    /// <returns><see langword="true"/> when inside the picture, in the current slice, and decoded.</returns>
    internal bool MbAvailable(int mbx, int mby) => mbx >= 0 && mby >= 0 && mbx < W && MbSlice[(mby * W) + mbx] == _sliceNum;

    /// <summary>Returns the macroblock address containing a 4x4 block, or -1 when outside or unavailable.</summary>
    /// <param name="bx">The absolute 4x4 column.</param>
    /// <param name="by">The absolute 4x4 row.</param>
    /// <returns>The address or -1.</returns>
    internal int BlockMb(int bx, int by) => bx >= 0 && by >= 0 && bx < W4 && MbAvailable(bx >> 2, by >> 2) ? ((by >> 2) * W) + (bx >> 2) : -1;

    /// <summary>Returns nC for a luma 4x4 block (9.2.1).</summary>
    /// <param name="blk">luma4x4BlkIdx.</param>
    /// <returns>nC.</returns>
    internal int LumaNc(int blk)
    {
        var bx = (MbX * 4) + BlkX[blk];
        var by = (MbY * 4) + BlkY[blk];
        var a = BlockMb(bx - 1, by);
        var b = BlockMb(bx, by - 1);
        return Average(a >= 0, a >= 0 ? TotalCoeff[(by * W4) + bx - 1] : 0, b >= 0, b >= 0 ? TotalCoeff[((by - 1) * W4) + bx] : 0);
    }

    /// <summary>Returns nC for a chroma AC block (9.2.1).</summary>
    /// <param name="component">0 Cb, 1 Cr.</param>
    /// <param name="blk">chroma4x4BlkIdx.</param>
    /// <returns>nC.</returns>
    internal int ChromaNc(int component, int blk)
    {
        var cx = (MbX * 2) + (blk & 1);
        var cy = (MbY * 2) + (blk >> 1);
        var plane = component * MbCount * 4;
        var a = cx > 0 && MbAvailable((cx - 1) >> 1, cy >> 1);
        var b = cy > 0 && MbAvailable(cx >> 1, (cy - 1) >> 1);
        return Average(a, a ? ChromaTotalCoeff[plane + (cy * W8) + cx - 1] : 0, b, b ? ChromaTotalCoeff[plane + ((cy - 1) * W8) + cx] : 0);
    }

    /// <summary>Returns the CABAC coded_block_flag ctxIdxInc (9.3.3.1.1.9).</summary>
    /// <param name="block">The block.</param>
    /// <returns>condTermFlagA + 2 * condTermFlagB.</returns>
    internal int CodedBlockFlagInc(in H264ResidualBlock block)
    {
        int a, b;
        switch (block.Category)
        {
            case H264BlockCategory.LumaDc:
                a = DcCondition(MbX - 1, MbY, 1);
                b = DcCondition(MbX, MbY - 1, 1);
                break;
            case H264BlockCategory.ChromaDc:
                a = DcCondition(MbX - 1, MbY, 2 << block.Component);
                b = DcCondition(MbX, MbY - 1, 2 << block.Component);
                break;
            case H264BlockCategory.ChromaAc:
                {
                    var cx = (MbX * 2) + (block.Index & 1);
                    var cy = (MbY * 2) + (block.Index >> 1);
                    a = ChromaAcCondition(cx - 1, cy, block.Component);
                    b = ChromaAcCondition(cx, cy - 1, block.Component);
                    break;
                }

            default:
                {
                    var bx = (MbX * 4) + BlkX[block.Index];
                    var by = (MbY * 4) + BlkY[block.Index];
                    a = LumaCondition(bx - 1, by);
                    b = LumaCondition(bx, by - 1);
                    break;
                }
        }

        return a + (2 * b);
    }

    /// <summary>Averages neighbouring counts per 9.2.1.</summary>
    /// <param name="hasA">Whether A is available.</param>
    /// <param name="na">nA.</param>
    /// <param name="hasB">Whether B is available.</param>
    /// <param name="nb">nB.</param>
    /// <returns>nC.</returns>
    private static int Average(bool hasA, int na, bool hasB, int nb) => hasA && hasB ? (na + nb + 1) >> 1 : hasA ? na : hasB ? nb : 0;

    /// <summary>condTermFlagN for a DC block.</summary>
    /// <param name="mbx">Neighbour column.</param>
    /// <param name="mby">Neighbour row.</param>
    /// <param name="bit">The MbDcCoded bit.</param>
    /// <returns>0 or 1.</returns>
    private int DcCondition(int mbx, int mby, int bit)
    {
        if (!MbAvailable(mbx, mby))
        {
            return CurrentIsIntra ? 1 : 0;
        }

        var addr = (mby * W) + mbx;
        var kind = MbKindOf[addr];
        if (kind == H264MbKind.IPcm)
        {
            return 1;
        }

        var present = bit == 1 ? kind == H264MbKind.I16x16 : !IsSkip(kind) && (MbCbp[addr] >> 4) != 0;
        return present && (MbDcCoded[addr] & bit) != 0 ? 1 : 0;
    }

    /// <summary>condTermFlagN for a luma 4x4 or AC block.</summary>
    /// <param name="bx">Neighbour 4x4 column.</param>
    /// <param name="by">Neighbour 4x4 row.</param>
    /// <returns>0 or 1.</returns>
    private int LumaCondition(int bx, int by)
    {
        var addr = BlockMb(bx, by);
        if (addr < 0)
        {
            return CurrentIsIntra ? 1 : 0;
        }

        var kind = MbKindOf[addr];
        if (kind == H264MbKind.IPcm)
        {
            return 1;
        }

        var b8 = ((by & 3) >> 1 << 1) | ((bx & 3) >> 1);
        return !IsSkip(kind) && ((MbCbp[addr] >> b8) & 1) != 0 ? Coded[(by * W4) + bx] : 0;
    }

    /// <summary>condTermFlagN for a chroma AC block.</summary>
    /// <param name="cx">Neighbour chroma 4x4 column.</param>
    /// <param name="cy">Neighbour chroma 4x4 row.</param>
    /// <param name="component">0 Cb, 1 Cr.</param>
    /// <returns>0 or 1.</returns>
    private int ChromaAcCondition(int cx, int cy, int component)
    {
        if (cx < 0 || cy < 0 || !MbAvailable(cx >> 1, cy >> 1))
        {
            return CurrentIsIntra ? 1 : 0;
        }

        var addr = ((cy >> 1) * W) + (cx >> 1);
        var kind = MbKindOf[addr];
        if (kind == H264MbKind.IPcm)
        {
            return 1;
        }

        return !IsSkip(kind) && (MbCbp[addr] >> 4) == 2 && ChromaTotalCoeff[(component * MbCount * 4) + (cy * W8) + cx] != 0 ? 1 : 0;
    }

    /// <summary>Decodes the macroblocks of a CAVLC slice (7.3.4).</summary>
    /// <param name="s">The syntax reader.</param>
    /// <param name="payloadBits">The stop bit position.</param>
    private void CavlcLoop(ref H264CavlcSyntax s, int payloadBits)
    {
        var addr = _firstMb;
        while (true)
        {
            if (SliceType != H264SliceType.I)
            {
                var run = H264Syntax.ReadUe(ref s.Reader, MbCount - addr, "mb_skip_run");
                for (var i = 0; i < run; i++)
                {
                    StartMacroblock(addr++);
                    DecodeSkip();
                }

                if (run > 0 && s.Reader.Position >= payloadBits)
                {
                    break;
                }
            }

            if (addr >= MbCount)
            {
                CodecException.InvalidData("Slice data continues past the last macroblock.");
            }

            StartMacroblock(addr++);
            DecodeMacroblock(ref s);
            if (s.Reader.Position >= payloadBits)
            {
                break;
            }
        }

        if (s.Reader.Position != payloadBits)
        {
            CodecException.InvalidData("CAVLC slice data overruns the RBSP trailing bits.");
        }
    }

    /// <summary>Decodes the macroblocks of a CABAC slice (7.3.4).</summary>
    /// <param name="s">The syntax reader.</param>
    /// <param name="rbsp">The RBSP.</param>
    /// <param name="payloadBits">The position of the last set bit of the RBSP.</param>
    private void CabacLoop(ref H264CabacSyntax s, ReadOnlySpan<byte> rbsp, int payloadBits)
    {
        var addr = _firstMb;
        while (true)
        {
            if (addr >= MbCount)
            {
                CodecException.InvalidData("Slice data continues past the last macroblock.");
            }

            StartMacroblock(addr++);
            if (SliceType != H264SliceType.I && s.SkipFlag(this))
            {
                DecodeSkip();
            }
            else
            {
                DecodeMacroblock(ref s);
            }

            if (s.Engine.Terminate() != 0)
            {
                break;
            }
        }

        // The last bit read by the engine is the rbsp_stop_one_bit (9.3.3.2.2.3). x264's flush may set a padding bit after it, so a short tail is tolerated.
        var end = s.Engine.Position;
        if (end > payloadBits + 1 || payloadBits + 1 - end > MaxCabacTrailingBits || (rbsp[(end - 1) >> 3] & (0x80 >> ((end - 1) & 7))) == 0)
        {
            CodecException.InvalidData("CABAC slice data does not end at the rbsp_stop_one_bit.");
        }
    }

    /// <summary>Begins a macroblock: claims it for the slice and clears its per-block state.</summary>
    /// <param name="addr">The address.</param>
    private void StartMacroblock(int addr)
    {
        if (MbSlice[addr] != -1)
        {
            CodecException.InvalidData("Two slices contain the same macroblock.");
        }

        MbSlice[addr] = _sliceNum;
        MbAddr = addr;
        MbX = addr % W;
        MbY = addr / W;
        Kind = H264MbKind.None;
        DecodedMbCount++;
        MbCbp[addr] = 0;
        MbTransform8x8[addr] = false;
        MbChromaMode[addr] = 0;
        MbDcCoded[addr] = 0;
        MbKindOf[addr] = H264MbKind.None;
        for (var row = 0; row < 4; row++)
        {
            var g = (((MbY * 4) + row) * W4) + (MbX * 4);
            TotalCoeff.AsSpan(g, 4).Clear();
            Coded.AsSpan(g, 4).Clear();
            IntraMode.AsSpan(g, 4).Fill(-1);
            for (var list = 0; list < 2; list++)
            {
                MvdAbs.AsSpan((((list * MbCount * 16) + g) * 2), 8).Clear();
            }
        }

        for (var c = 0; c < 2; c++)
        {
            for (var row = 0; row < 2; row++)
            {
                ChromaTotalCoeff.AsSpan((c * MbCount * 4) + ((((MbY * 2) + row) * W8) + (MbX * 2)), 2).Clear();
            }
        }

        for (var row = 0; row < 2; row++)
        {
            Direct8x8.AsSpan((((MbY * 2) + row) * W8) + (MbX * 2), 2).Clear();
            for (var list = 0; list < 2; list++)
            {
                var g8 = (list * MbCount * 4) + (((MbY * 2) + row) * W8) + (MbX * 2);
                _pic.RefIdx.AsSpan(g8, 2).Fill(-1);
                _pic.RefPicId.AsSpan(g8, 2).Fill(-1);
            }
        }

        _pic.IntraMb[addr] = 0;
        _derivedMask = 0;
        _lumaCodedMask = 0;
        _chromaCodedMask = 0;
        _lumaDcCoded = false;
    }

    /// <summary>Sets the kind of the current macroblock.</summary>
    /// <param name="kind">The kind.</param>
    private void SetKind(H264MbKind kind)
    {
        Kind = kind;
        MbKindOf[MbAddr] = kind;
        _pic.IntraMb[MbAddr] = IsIntra(kind) ? (byte)1 : (byte)0;
    }

    /// <summary>Decodes a P_Skip or B_Skip macroblock.</summary>
    private void DecodeSkip()
    {
        SetKind(SliceType == H264SliceType.P ? H264MbKind.PSkip : H264MbKind.BSkip);
        MbQp[MbAddr] = (byte)_qp;
        PrevQpDeltaNonZero = false;
        DeriveMotion();
        PredictInter();
    }

    /// <summary>Decodes a coded macroblock (7.3.5).</summary>
    /// <typeparam name="T">The entropy reader.</typeparam>
    /// <param name="s">The reader.</param>
    private void DecodeMacroblock<T>(ref T s)
        where T : IH264EntropyReader, allows ref struct
    {
        var mbType = s.MbType(this);
        var intraType = SliceType switch
        {
            H264SliceType.I => mbType,
            H264SliceType.P => mbType >= 5 ? mbType - 5 : -1,
            _ => mbType >= 23 ? mbType - 23 : -1,
        };
        if (intraType > 25)
        {
            CodecException.InvalidData("mb_type is out of range.");
        }

        var cbp = 0;
        var transform8x8 = false;
        if (intraType == 25)
        {
            SetKind(H264MbKind.IPcm);
            s.Pcm(_pcm);
            DecodePcm();
            return;
        }

        if (intraType >= 0)
        {
            if (intraType == 0)
            {
                transform8x8 = _transform8x8Mode && s.Transform8x8(this);
                SetKind(transform8x8 ? H264MbKind.I8x8 : H264MbKind.I4x4);
                MbTransform8x8[MbAddr] = transform8x8;
                ReadIntraModes(ref s, transform8x8);
            }
            else
            {
                SetKind(H264MbKind.I16x16);
                _intra16x16Mode = (intraType - 1) % 4;
                cbp = ((((intraType - 1) / 4) % 3) << 4) | (intraType >= 13 ? 15 : 0);
            }

            var chroma = s.IntraChromaPredMode(this);
            if (chroma > 3)
            {
                CodecException.InvalidData("intra_chroma_pred_mode is out of range.");
            }

            MbChromaMode[MbAddr] = (byte)chroma;
        }
        else
        {
            ReadInterPrediction(ref s, mbType);
        }

        if (Kind != H264MbKind.I16x16)
        {
            cbp = s.CodedBlockPattern(this);
            MbCbp[MbAddr] = (byte)cbp;
            if ((cbp & 15) != 0 && _transform8x8Mode && !IsIntra(Kind) && _noSubPartitionBelow8x8
                && (Kind != H264MbKind.BDirect || _direct8x8Inference))
            {
                transform8x8 = s.Transform8x8(this);
                MbTransform8x8[MbAddr] = transform8x8;
            }
        }

        MbCbp[MbAddr] = (byte)cbp;
        if (cbp != 0 || Kind == H264MbKind.I16x16)
        {
            var delta = s.MbQpDelta(this);
            if (delta is < -26 or > 25)
            {
                CodecException.InvalidData("mb_qp_delta is out of range.");
            }

            _qp = (_qp + delta + 52) % 52;
            PrevQpDeltaNonZero = delta != 0;
            ReadResidual(ref s, cbp, transform8x8);
        }
        else
        {
            PrevQpDeltaNonZero = false;
        }

        MbQp[MbAddr] = (byte)_qp;
        if (IsIntra(Kind))
        {
            ReconstructIntra();
        }
        else
        {
            DeriveMotion();
            PredictInter();
            ReconstructInterResidual(cbp, transform8x8);
        }
    }

    /// <summary>Reads Intra4x4 or Intra8x8 prediction modes and derives them (8.3.1.1, 8.3.2.1).</summary>
    /// <typeparam name="T">The entropy reader.</typeparam>
    /// <param name="s">The reader.</param>
    /// <param name="eight">Whether the macroblock uses 8x8 blocks.</param>
    private void ReadIntraModes<T>(ref T s, bool eight)
        where T : IH264EntropyReader, allows ref struct
    {
        var count = eight ? 4 : 16;
        for (var i = 0; i < count; i++)
        {
            var blk = eight ? i * 4 : i;
            var bx = (MbX * 4) + BlkX[blk];
            var by = (MbY * 4) + BlkY[blk];
            var rem = s.IntraPredMode();
            var predicted = PredictedIntraMode(bx, by);
            var mode = rem < 0 ? predicted : rem < predicted ? rem : rem + 1;
            if (eight)
            {
                IntraMode[(by * W4) + bx] = (sbyte)mode;
                IntraMode[(by * W4) + bx + 1] = (sbyte)mode;
                IntraMode[((by + 1) * W4) + bx] = (sbyte)mode;
                IntraMode[((by + 1) * W4) + bx + 1] = (sbyte)mode;
                _intraModes[i] = mode;
            }
            else
            {
                IntraMode[(by * W4) + bx] = (sbyte)mode;
                _intraModes[blk] = mode;
            }
        }
    }

    /// <summary>Returns predIntra4x4PredMode / predIntra8x8PredMode for a block.</summary>
    /// <param name="bx">The block's absolute 4x4 column.</param>
    /// <param name="by">The block's absolute 4x4 row.</param>
    /// <returns>The predicted mode.</returns>
    private int PredictedIntraMode(int bx, int by)
    {
        var a = BlockMb(bx - 1, by);
        var b = BlockMb(bx, by - 1);
        if (a < 0 || b < 0 || (_constrainedIntra && (!IsIntra(MbKindOf[a]) || !IsIntra(MbKindOf[b]))))
        {
            return 2;
        }

        var modeA = MbKindOf[a] is H264MbKind.I4x4 or H264MbKind.I8x8 ? IntraMode[(by * W4) + bx - 1] : 2;
        var modeB = MbKindOf[b] is H264MbKind.I4x4 or H264MbKind.I8x8 ? IntraMode[((by - 1) * W4) + bx] : 2;
        return Math.Min(modeA, modeB);
    }

    /// <summary>Reads residual() (7.3.5.3) into the coefficient scratch and records coded-block state.</summary>
    /// <typeparam name="T">The entropy reader.</typeparam>
    /// <param name="s">The reader.</param>
    /// <param name="cbp">coded_block_pattern.</param>
    /// <param name="transform8x8">transform_size_8x8_flag.</param>
    private void ReadResidual<T>(ref T s, int cbp, bool transform8x8)
        where T : IH264EntropyReader, allows ref struct
    {
        var levels = _levels.AsSpan();
        var zigzag = H264Tables.Zigzag4x4;
        if (Kind == H264MbKind.I16x16)
        {
            levels[..16].Clear();
            var n = s.ResidualBlock(this, new H264ResidualBlock(H264BlockCategory.LumaDc, 0, 0), 0, 15, 16, levels);
            for (var k = 0; k < 16; k++)
            {
                _lumaDc[zigzag[k]] = levels[k];
            }

            _lumaDcCoded = n > 0;
            if (n > 0)
            {
                MbDcCoded[MbAddr] |= 1;
            }
        }

        _lumaCodedMask = 0;
        for (var b8 = 0; b8 < 4; b8++)
        {
            if (((cbp >> b8) & 1) == 0)
            {
                continue;
            }

            if (transform8x8 && s.IsCabac)
            {
                levels.Clear();
                var n = s.ResidualBlock(this, new H264ResidualBlock(H264BlockCategory.Luma8x8, b8, 0), 0, 63, 64, levels);
                var target = _luma8x8.AsSpan(b8 * 64, 64);
                var scan8 = H264Tables.Zigzag8x8;
                for (var k = 0; k < 64; k++)
                {
                    target[scan8[k]] = levels[k];
                }

                MarkLuma8x8(b8, n, n);
                continue;
            }

            var any = 0;
            for (var i4 = 0; i4 < 4; i4++)
            {
                var blk = (b8 * 4) + i4;
                levels[..16].Clear();
                int n;
                if (Kind == H264MbKind.I16x16)
                {
                    n = s.ResidualBlock(this, new H264ResidualBlock(H264BlockCategory.LumaAc, blk, 0), 0, 14, 15, levels);
                    var target = _luma4x4.AsSpan(blk * 16, 16);
                    for (var k = 0; k < 15; k++)
                    {
                        target[zigzag[k + 1]] = levels[k];
                    }
                }
                else
                {
                    n = s.ResidualBlock(this, new H264ResidualBlock(H264BlockCategory.Luma4x4, blk, 0), 0, 15, 16, levels);
                    if (transform8x8)
                    {
                        var target = _luma8x8.AsSpan(b8 * 64, 64);
                        var scan8 = H264Tables.Zigzag8x8;
                        for (var k = 0; k < 16; k++)
                        {
                            target[scan8[(4 * k) + i4]] = levels[k];
                        }
                    }
                    else
                    {
                        var target = _luma4x4.AsSpan(blk * 16, 16);
                        for (var k = 0; k < 16; k++)
                        {
                            target[zigzag[k]] = levels[k];
                        }
                    }
                }

                var g = (((MbY * 4) + BlkY[blk]) * W4) + (MbX * 4) + BlkX[blk];
                TotalCoeff[g] = (byte)n;
                any += n;
                if (!transform8x8)
                {
                    Coded[g] = n > 0 ? (byte)1 : (byte)0;
                    if (n > 0)
                    {
                        _lumaCodedMask |= 1 << blk;
                    }
                }
            }

            if (transform8x8)
            {
                MarkLuma8x8(b8, any, -1);
            }
        }

        _chromaCodedMask = 0;
        if ((cbp >> 4) != 0)
        {
            for (var c = 0; c < 2; c++)
            {
                levels[..4].Clear();
                var n = s.ResidualBlock(this, new H264ResidualBlock(H264BlockCategory.ChromaDc, 0, c), 0, 3, 4, levels);
                levels[..4].CopyTo(_chromaDc.AsSpan(c * 4, 4));
                if (n > 0)
                {
                    MbDcCoded[MbAddr] |= (byte)(2 << c);
                    _chromaCodedMask |= 0x0F << (c * 4);
                }
            }
        }

        if ((cbp >> 4) == 2)
        {
            for (var c = 0; c < 2; c++)
            {
                for (var blk = 0; blk < 4; blk++)
                {
                    levels[..15].Clear();
                    var n = s.ResidualBlock(this, new H264ResidualBlock(H264BlockCategory.ChromaAc, blk, c), 0, 14, 15, levels);
                    var target = _chromaAc.AsSpan(((c * 4) + blk) * 16, 16);
                    for (var k = 0; k < 15; k++)
                    {
                        target[zigzag[k + 1]] = levels[k];
                    }

                    ChromaTotalCoeff[(c * MbCount * 4) + ((((MbY * 2) + (blk >> 1)) * W8) + (MbX * 2) + (blk & 1))] = (byte)n;
                    if (n > 0)
                    {
                        _chromaCodedMask |= 1 << ((c * 4) + blk);
                    }
                }
            }
        }
    }

    /// <summary>Records an 8x8 transform block's coded state on its four 4x4 blocks.</summary>
    /// <param name="b8">luma8x8BlkIdx.</param>
    /// <param name="nonZero">The number of non-zero coefficients.</param>
    /// <param name="totalCoeff">TotalCoeff to store on all four blocks, or -1 to keep the per-4x4 CAVLC values.</param>
    private void MarkLuma8x8(int b8, int nonZero, int totalCoeff)
    {
        for (var i4 = 0; i4 < 4; i4++)
        {
            var blk = (b8 * 4) + i4;
            var g = (((MbY * 4) + BlkY[blk]) * W4) + (MbX * 4) + BlkX[blk];
            Coded[g] = nonZero > 0 ? (byte)1 : (byte)0;
            if (totalCoeff >= 0)
            {
                TotalCoeff[g] = (byte)Math.Min(totalCoeff, 16);
            }
        }

        if (nonZero > 0)
        {
            _lumaCodedMask |= 0xF << (b8 * 4);
        }
    }

    /// <summary>Writes I_PCM samples and the state neighbours see (7.3.5, 8.3.5).</summary>
    private void DecodePcm()
    {
        var stride = _pic.Width;
        var luma = _pic.Luma;
        for (var y = 0; y < 16; y++)
        {
            _pcm.AsSpan(y * 16, 16).CopyTo(luma.AsSpan((((MbY * 16) + y) * stride) + (MbX * 16)));
        }

        var cstride = stride / 2;
        for (var y = 0; y < 8; y++)
        {
            _pcm.AsSpan(256 + (y * 8), 8).CopyTo(_pic.Cb.AsSpan((((MbY * 8) + y) * cstride) + (MbX * 8)));
            _pcm.AsSpan(320 + (y * 8), 8).CopyTo(_pic.Cr.AsSpan((((MbY * 8) + y) * cstride) + (MbX * 8)));
        }

        MbCbp[MbAddr] = 0x2F;
        MbQp[MbAddr] = 0;
        MbDcCoded[MbAddr] = 7;
        PrevQpDeltaNonZero = false;
        for (var row = 0; row < 4; row++)
        {
            var g = (((MbY * 4) + row) * W4) + (MbX * 4);
            TotalCoeff.AsSpan(g, 4).Fill(16);
            Coded.AsSpan(g, 4).Fill(1);
        }

        for (var c = 0; c < 2; c++)
        {
            for (var row = 0; row < 2; row++)
            {
                ChromaTotalCoeff.AsSpan((c * MbCount * 4) + ((((MbY * 2) + row) * W8) + (MbX * 2)), 2).Fill(16);
            }
        }

        ClearMotion();
    }

    /// <summary>Deblocking parameters of one slice.</summary>
    /// <param name="DisableIdc">disable_deblocking_filter_idc.</param>
    /// <param name="OffsetA">FilterOffsetA.</param>
    /// <param name="OffsetB">FilterOffsetB.</param>
    /// <param name="CbOffset">chroma_qp_index_offset.</param>
    /// <param name="CrOffset">second_chroma_qp_index_offset.</param>
    private readonly record struct SliceFilter(int DisableIdc, int OffsetA, int OffsetB, int CbOffset, int CrOffset);
}
