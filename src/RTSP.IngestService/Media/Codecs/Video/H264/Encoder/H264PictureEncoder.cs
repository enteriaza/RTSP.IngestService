// Codes one picture: slice headers (7.3.3) and, per macroblock, the mode decision, prediction, transform,
// quantisation, reconstruction and CAVLC syntax, then the deblocking filter over the finished reconstruction.
// Mode decision compares SATD plus lambda-weighted header bits of P_Skip, P_L0_16x16, P_8x8 (four 8x8 partitions),
// Intra_16x16 and Intra_4x4; a macroblock whose coded size would exceed 3200 bits is replaced by I_PCM so every
// macroblock and therefore every picture has a hard size bound. All scratch is per encoder; nothing is allocated
// per macroblock.

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>One memory_management_control_operation (7.3.3.3).</summary>
/// <param name="Operation">memory_management_control_operation, 1 to 6.</param>
/// <param name="Value">difference_of_pic_nums_minus1 (1, 3), long_term_pic_num (2) or max_long_term_frame_idx_plus1 (4).</param>
/// <param name="LongTermFrameIdx">long_term_frame_idx (3, 6).</param>
internal readonly record struct H264Mmco(int Operation, int Value = 0, int LongTermFrameIdx = 0);

/// <summary>One ref_pic_list_modification command (7.3.3.1).</summary>
/// <param name="Idc">modification_of_pic_nums_idc, 0 to 2.</param>
/// <param name="Value">abs_diff_pic_num_minus1 or long_term_pic_num.</param>
internal readonly record struct H264ListModification(int Idc, int Value);

/// <summary>The slice-header values shared by every slice of a picture.</summary>
internal sealed class H264PictureHeader
{
    /// <summary>Gets or sets whether the picture is an IDR picture.</summary>
    public bool IsIdr { get; set; }

    /// <summary>Gets or sets nal_ref_idc; 0 for a non-reference picture.</summary>
    public int NalRefIdc { get; set; }

    /// <summary>Gets or sets frame_num.</summary>
    public int FrameNum { get; set; }

    /// <summary>Gets or sets idr_pic_id.</summary>
    public int IdrPicId { get; set; }

    /// <summary>Gets or sets pic_order_cnt_lsb (POC type 0).</summary>
    public int PocLsb { get; set; }

    /// <summary>Gets or sets num_ref_idx_l0_active_minus1 + 1 (P pictures).</summary>
    public int ActiveReferences { get; set; }

    /// <summary>Gets or sets long_term_reference_flag (IDR pictures).</summary>
    public bool LongTermReference { get; set; }

    /// <summary>Gets the ref_pic_list_modification commands of list 0.</summary>
    public List<H264ListModification> ListModifications { get; } = [];

    /// <summary>Gets the memory management operations; empty selects the sliding window.</summary>
    public List<H264Mmco> Mmcos { get; } = [];

    /// <summary>Gets or sets the picture QP.</summary>
    public int Qp { get; set; }

    /// <summary>Gets or sets whether every macroblock with a reference is coded P_Skip (the VBV's last resort).</summary>
    public bool ForceSkip { get; set; }

    /// <summary>Gets or sets redundant_pic_cnt, written when the PPS signals it.</summary>
    public int RedundantPicCnt { get; set; }

    /// <summary>Gets or sets a predicate selecting macroblocks to code as I_PCM regardless of cost (conformance streams).</summary>
    public Func<int, bool>? ForcePcm { get; set; }

    /// <summary>Gets whether the picture is coded with I slices.</summary>
    public bool IsIntra => IsIdr || ActiveReferences == 0;
}

/// <summary>The outcome of coding one picture.</summary>
internal struct H264PictureCounters
{
    /// <summary>Intra macroblocks.</summary>
    public int Intra;

    /// <summary>P_Skip macroblocks.</summary>
    public int Skip;

    /// <summary>I_PCM macroblocks.</summary>
    public int Pcm;

    /// <summary>The summed mode-decision distortion (SATD) of the chosen modes, the rate controller's complexity.</summary>
    public long Satd;
}

/// <summary>Codes pictures into slice RBSPs.</summary>
internal sealed partial class H264PictureEncoder
{
    /// <summary>The macroblock size bound: 128 + RawMbBits for 8-bit 4:2:0 (A.3.1).</summary>
    public const int MaxMbBits = 3200;

    /// <summary>Raster 4x4 block index by luma4x4BlkIdx.</summary>
    private static readonly byte[] BlkRaster = [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    /// <summary>The sequence layout.</summary>
    private readonly H264SequenceLayout _layout;

    /// <summary>The integer motion search range.</summary>
    private readonly int _searchRange;

    /// <summary>Source luma of the macroblock.</summary>
    private readonly byte[] _srcY = new byte[256];

    /// <summary>Source chroma of the macroblock: 64 Cb then 64 Cr.</summary>
    private readonly byte[] _srcC = new byte[128];

    /// <summary>Inter luma prediction.</summary>
    private readonly byte[] _predY = new byte[256];

    /// <summary>Inter chroma prediction: 64 Cb then 64 Cr.</summary>
    private readonly byte[] _predC = new byte[128];

    /// <summary>Best inter luma prediction kept while other modes are tried.</summary>
    private readonly byte[] _bestPredY = new byte[256];

    /// <summary>Luma levels in scan order by raster block, 16 per block.</summary>
    private readonly int[] _luma = new int[256];

    /// <summary>Intra16x16 DC levels in scan order.</summary>
    private readonly int[] _lumaDc = new int[16];

    /// <summary>Chroma DC levels, raster, 4 per component.</summary>
    private readonly int[] _chromaDc = new int[8];

    /// <summary>Chroma AC levels in scan order (positions 1 to 15), 15 per block, 4 blocks per component.</summary>
    private readonly int[] _chromaAc = new int[120];

    /// <summary>Intra4x4 modes chosen for the macroblock by raster block.</summary>
    private readonly sbyte[] _modes = new sbyte[16];

    /// <summary>The partition motion of the macroblock: x, y and reference index per 8x8 partition.</summary>
    private readonly int[] _partition = new int[12];

    /// <summary>The motion vector predictor of each 8x8 partition (x, y), for the mvd syntax.</summary>
    private readonly int[] _partitionMvp = new int[8];

    /// <summary>The picture being coded.</summary>
    private H264EncoderPicture _pic = null!;

    /// <summary>The source frame.</summary>
    private VideoFrame _source = null!;

    /// <summary>The reference list of the picture; null entries are non-existing frames.</summary>
    private H264EncoderPicture?[] _refs = [];

    /// <summary>The picture header.</summary>
    private H264PictureHeader _header = null!;

    /// <summary>The macroblock column.</summary>
    private int _mbx;

    /// <summary>The macroblock row.</summary>
    private int _mby;

    /// <summary>The macroblock address.</summary>
    private int _mbAddr;

    /// <summary>The slice index.</summary>
    private int _slice;

    /// <summary>The picture QP.</summary>
    private int _qp;

    /// <summary>sqrt(lambda) * 256 at the picture QP.</summary>
    private int _lambda;

    /// <summary>The pending mb_skip_run.</summary>
    private int _skipRun;

    /// <summary>Raster blocks of the current macroblock whose motion is already decided.</summary>
    private int _doneBlocks;

    /// <summary>The counters of the picture.</summary>
    private H264PictureCounters _counters;

    /// <summary>Initializes a new instance of the <see cref="H264PictureEncoder"/> class.</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <param name="searchRange">The motion search range.</param>
    public H264PictureEncoder(H264SequenceLayout layout, int searchRange)
    {
        _layout = layout;
        _searchRange = searchRange;
        _halfSamples = new H264HalfSamplePlanes(layout.WidthInMbs * 16, layout.HeightInMbs * 16);
    }

    /// <summary>Codes a picture into one RBSP per slice and deblocks the reconstruction.</summary>
    /// <param name="source">The source frame, at the layout's size.</param>
    /// <param name="picture">Receives the reconstruction and coding state.</param>
    /// <param name="references">The list 0 references (P pictures); null for a non-existing frame.</param>
    /// <param name="header">The picture header.</param>
    /// <param name="slices">The macroblock rows at which each slice starts, then the row count.</param>
    /// <param name="writer">Receives the slice RBSPs back to back, each byte aligned.</param>
    /// <param name="sliceEnds">Receives the byte offset after each slice.</param>
    /// <param name="cancellationToken">Observed between macroblock rows.</param>
    /// <returns>The picture counters.</returns>
    public H264PictureCounters Encode(VideoFrame source, H264EncoderPicture picture, H264EncoderPicture?[] references, H264PictureHeader header, ReadOnlySpan<int> slices, H264BitWriter writer, List<int> sliceEnds, CancellationToken cancellationToken)
    {
        _source = source;
        _pic = picture;
        _refs = references;
        _header = header;
        _qp = header.Qp;
        _lambda = H264EncoderTables.Lambda256[_qp];
        _counters = default;
        for (_slice = 0; _slice < slices.Length - 1; _slice++)
        {
            var firstMb = slices[_slice] * _layout.WidthInMbs;
            var endMb = slices[_slice + 1] * _layout.WidthInMbs;
            WriteSliceHeader(writer, firstMb);
            _skipRun = 0;
            for (_mbAddr = firstMb; _mbAddr < endMb; _mbAddr++)
            {
                _mbx = _mbAddr % _layout.WidthInMbs;
                _mby = _mbAddr / _layout.WidthInMbs;
                if (_mbx == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                EncodeMacroblock(writer);
            }

            if (_skipRun > 0)
            {
                writer.WriteUe(_skipRun);
            }

            writer.WriteTrailingBits();
            sliceEnds.Add((int)(writer.Position >> 3));
        }

        H264EncoderDeblocking.Filter(picture);
        return _counters;
    }

    /// <summary>Writes slice_header() (7.3.3) for a slice starting at a macroblock.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="firstMb">first_mb_in_slice.</param>
    private void WriteSliceHeader(H264BitWriter w, int firstMb)
    {
        var h = _header;
        w.WriteUe(firstMb);
        w.WriteUe(h.IsIntra ? 7 : 5);
        w.WriteUe(0);
        w.Write((uint)h.FrameNum, _layout.Log2MaxFrameNum);
        if (h.IsIdr)
        {
            w.WriteUe(h.IdrPicId);
        }

        if (_layout.PocType == 0)
        {
            w.Write((uint)h.PocLsb, _layout.Log2MaxPocLsb);
        }
        else if (_layout.PocType == 1)
        {
            // 8.2.1.2: a non-reference picture's expected POC is 2 * frame_num - 3, before the previous reference
            // picture; delta_pic_order_cnt[0] moves it to 2 * frame_num - 1 so output order stays decoding order.
            w.WriteSe(h.NalRefIdc == 0 ? 1 - H264SequenceLayout.OffsetForNonRefPic : 0);
        }

        if (_layout.RedundantPicCntPresent)
        {
            w.WriteUe(h.RedundantPicCnt);
        }

        if (!h.IsIntra)
        {
            var overriding = h.ActiveReferences != _layout.MaxReferenceFrames;
            w.WriteBit(overriding);
            if (overriding)
            {
                w.WriteUe(h.ActiveReferences - 1);
            }

            w.WriteBit(h.ListModifications.Count != 0);
            if (h.ListModifications.Count != 0)
            {
                foreach (var m in h.ListModifications)
                {
                    w.WriteUe(m.Idc);
                    w.WriteUe(m.Value);
                }

                w.WriteUe(3);
            }
        }

        if (h.NalRefIdc != 0)
        {
            if (h.IsIdr)
            {
                w.WriteBit(false);
                w.WriteBit(h.LongTermReference);
            }
            else
            {
                w.WriteBit(h.Mmcos.Count != 0);
                if (h.Mmcos.Count != 0)
                {
                    foreach (var m in h.Mmcos)
                    {
                        w.WriteUe(m.Operation);
                        if (m.Operation is 1 or 2 or 3 or 4)
                        {
                            w.WriteUe(m.Value);
                        }

                        if (m.Operation is 3 or 6)
                        {
                            w.WriteUe(m.LongTermFrameIdx);
                        }
                    }

                    w.WriteUe(0);
                }
            }
        }

        w.WriteSe(_qp - 26);
        w.WriteUe(0);
        w.WriteSe(0);
        w.WriteSe(0);
    }

    /// <summary>Chooses, codes and writes one macroblock.</summary>
    /// <param name="w">The writer.</param>
    private void EncodeMacroblock(H264BitWriter w)
    {
        LoadSource();
        _pic.Slice[_mbAddr] = (short)_slice;
        _doneBlocks = 0;
        var forcePcm = _header.ForcePcm?.Invoke(_mbAddr) == true;
        var cost = 0;
        var mode = forcePcm ? MbChoice.Pcm : _header.IsIntra ? ChooseIntra(int.MaxValue, out cost) : ChooseInter(out cost);
        _counters.Satd += cost;
        if (mode == MbChoice.Skip)
        {
            _skipRun++;
            _counters.Skip++;
            return;
        }

        if (!_header.IsIntra)
        {
            w.WriteUe(_skipRun);
            _skipRun = 0;
        }

        var start = w.Position;
        if (mode != MbChoice.Pcm)
        {
            WriteMacroblock(w, mode);
            if (w.Position - start <= MaxMbBits)
            {
                if (mode != MbChoice.Inter)
                {
                    _counters.Intra++;
                }

                return;
            }

            w.Rewind(start);
        }

        WritePcm(w);
        _counters.Intra++;
        _counters.Pcm++;
    }

    /// <summary>Copies the macroblock's source samples, replicating the frame edge into the padding.</summary>
    private void LoadSource()
    {
        int width = _source.Width, height = _source.Height;
        var y = _source.GetPlane(0);
        for (var j = 0; j < 16; j++)
        {
            var row = Math.Min((_mby * 16) + j, height - 1) * width;
            for (var i = 0; i < 16; i++)
            {
                _srcY[(j * 16) + i] = y[row + Math.Min((_mbx * 16) + i, width - 1)];
            }
        }

        int cw = width / 2, ch = height / 2;
        for (var c = 0; c < 2; c++)
        {
            var plane = _source.GetPlane(c + 1);
            for (var j = 0; j < 8; j++)
            {
                var row = Math.Min((_mby * 8) + j, ch - 1) * cw;
                for (var i = 0; i < 8; i++)
                {
                    _srcC[(c * 64) + (j * 8) + i] = plane[row + Math.Min((_mbx * 8) + i, cw - 1)];
                }
            }
        }
    }

    /// <summary>Writes an I_PCM macroblock and makes its reconstruction the source samples.</summary>
    /// <param name="w">The writer.</param>
    private void WritePcm(H264BitWriter w)
    {
        w.WriteUe(_header.IsIntra ? 25 : 30);
        w.AlignZero();
        w.WriteBytes(_srcY);
        w.WriteBytes(_srcC);
        StoreSamples(_srcY, _srcC);
        SetIntraState(H264MbKind.Pcm);
        _pic.Qp[_mbAddr] = 0;
        _pic.TotalCoeff.AsSpan(_mbAddr * 16, 16).Fill(16);
        _pic.ChromaCoeff.AsSpan(_mbAddr * 8, 8).Fill(16);
    }

    /// <summary>Writes samples into the macroblock of the reconstruction.</summary>
    /// <param name="luma">256 luma samples.</param>
    /// <param name="chroma">64 Cb then 64 Cr samples.</param>
    private void StoreSamples(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> chroma)
    {
        var stride = _pic.Width;
        for (var j = 0; j < 16; j++)
        {
            luma.Slice(j * 16, 16).CopyTo(_pic.Luma.AsSpan((((_mby * 16) + j) * stride) + (_mbx * 16), 16));
        }

        var cs = stride / 2;
        for (var j = 0; j < 8; j++)
        {
            chroma.Slice(j * 8, 8).CopyTo(_pic.Cb.AsSpan((((_mby * 8) + j) * cs) + (_mbx * 8), 8));
            chroma.Slice(64 + (j * 8), 8).CopyTo(_pic.Cr.AsSpan((((_mby * 8) + j) * cs) + (_mbx * 8), 8));
        }
    }

    /// <summary>Records an intra macroblock's neighbour state: no motion, reference -1.</summary>
    /// <param name="kind">The kind.</param>
    private void SetIntraState(H264MbKind kind)
    {
        _pic.Kind[_mbAddr] = kind;
        for (var b = 0; b < 16; b++)
        {
            var index = _pic.Block(_mbx, _mby, b & 3, b >> 2);
            _pic.Mv[2 * index] = 0;
            _pic.Mv[(2 * index) + 1] = 0;
            _pic.RefIdx[index] = -1;
            _pic.RefId[index] = -1;
            _pic.IntraModes[(_mbAddr * 16) + b] = kind == H264MbKind.Intra4x4 ? _modes[b] : (sbyte)-1;
        }
    }

    /// <summary>Converts a lambda-weighted bit count into cost units.</summary>
    /// <param name="bits">The bits.</param>
    /// <returns>The cost.</returns>
    private int BitCost(int bits) => (int)(((long)_lambda * bits) + 128 >> 8);

    /// <summary>Returns whether a macroblock is available to the current one (6.4.8): inside the picture, already coded and in the same slice.</summary>
    /// <param name="mbx">Column.</param>
    /// <param name="mby">Row.</param>
    /// <returns>Whether it is available.</returns>
    private bool MbAvailable(int mbx, int mby)
    {
        if (mbx < 0 || mby < 0 || mbx >= _layout.WidthInMbs || mby >= _layout.HeightInMbs)
        {
            return false;
        }

        var addr = (mby * _layout.WidthInMbs) + mbx;
        return addr < _mbAddr && _pic.Slice[addr] == _slice;
    }

    /// <summary>What a macroblock is coded as.</summary>
    private enum MbChoice
    {
        /// <summary>P_Skip.</summary>
        Skip,

        /// <summary>P_L0_16x16 or P_8x8; the partition state holds the motion.</summary>
        Inter,

        /// <summary>Intra_16x16.</summary>
        Intra16x16,

        /// <summary>Intra_4x4.</summary>
        Intra4x4,

        /// <summary>I_PCM.</summary>
        Pcm,
    }
}
