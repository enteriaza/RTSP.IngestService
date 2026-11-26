// slice_header() (ITU-T H.264 7.3.3) with ref_pic_list_modification (7.3.3.1), pred_weight_table (7.3.3.2) and
// dec_ref_pic_marking (7.3.3.3), for progressive frames. One instance is reused for every slice; all arrays have the
// fixed sizes the syntax allows, so parsing allocates nothing.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>Slice types after the modulo-5 reduction.</summary>
internal enum H264SliceType
{
    /// <summary>P slice.</summary>
    P = 0,

    /// <summary>B slice.</summary>
    B = 1,

    /// <summary>I slice.</summary>
    I = 2,
}

/// <summary>A parsed slice header.</summary>
internal sealed class H264SliceHeader
{
    /// <summary>The most reference list modification or memory management operations kept per list.</summary>
    public const int MaxOperations = 66;

    /// <summary>Gets nal_unit_type.</summary>
    public H264NalUnitType NalType { get; private set; }

    /// <summary>Gets nal_ref_idc.</summary>
    public int NalRefIdc { get; private set; }

    /// <summary>Gets a value indicating whether the slice belongs to an IDR picture.</summary>
    public bool IsIdr => NalType == H264NalUnitType.IdrSlice;

    /// <summary>Gets first_mb_in_slice.</summary>
    public int FirstMb { get; private set; }

    /// <summary>Gets the slice type.</summary>
    public H264SliceType Type { get; private set; }

    /// <summary>Gets the PPS.</summary>
    public H264Pps Pps { get; private set; } = null!;

    /// <summary>Gets the SPS.</summary>
    public H264Sps Sps { get; private set; } = null!;

    /// <summary>Gets frame_num.</summary>
    public int FrameNum { get; private set; }

    /// <summary>Gets idr_pic_id.</summary>
    public int IdrPicId { get; private set; }

    /// <summary>Gets pic_order_cnt_lsb.</summary>
    public int PocLsb { get; private set; }

    /// <summary>Gets delta_pic_order_cnt_bottom.</summary>
    public int DeltaPocBottom { get; private set; }

    /// <summary>Gets delta_pic_order_cnt[0].</summary>
    public int DeltaPoc0 { get; private set; }

    /// <summary>Gets delta_pic_order_cnt[1].</summary>
    public int DeltaPoc1 { get; private set; }

    /// <summary>Gets redundant_pic_cnt.</summary>
    public int RedundantPicCnt { get; private set; }

    /// <summary>Gets direct_spatial_mv_pred_flag.</summary>
    public bool DirectSpatial { get; private set; }

    /// <summary>Gets num_ref_idx_lX_active_minus1 + 1 per list (0 for unused lists).</summary>
    public int[] NumRefIdxActive { get; } = new int[2];

    /// <summary>Gets the number of modification operations per list.</summary>
    public int[] ModificationCount { get; } = new int[2];

    /// <summary>Gets modification_of_pic_nums_idc per list and operation.</summary>
    public int[] ModificationIdc { get; } = new int[2 * MaxOperations];

    /// <summary>Gets abs_diff_pic_num_minus1 or long_term_pic_num per list and operation.</summary>
    public int[] ModificationValue { get; } = new int[2 * MaxOperations];

    /// <summary>Gets a value indicating whether the slice carries an explicit pred_weight_table.</summary>
    public bool HasWeightTable { get; private set; }

    /// <summary>Gets luma_log2_weight_denom.</summary>
    public int LumaLog2Denom { get; private set; }

    /// <summary>Gets chroma_log2_weight_denom.</summary>
    public int ChromaLog2Denom { get; private set; }

    /// <summary>Gets weights at <c>(list * 32 + refIdx) * 3 + component</c> (Y, Cb, Cr).</summary>
    public int[] Weights { get; } = new int[2 * 32 * 3];

    /// <summary>Gets offsets, laid out as <see cref="Weights"/>.</summary>
    public int[] Offsets { get; } = new int[2 * 32 * 3];

    /// <summary>Gets no_output_of_prior_pics_flag.</summary>
    public bool NoOutputOfPriorPics { get; private set; }

    /// <summary>Gets long_term_reference_flag.</summary>
    public bool LongTermReference { get; private set; }

    /// <summary>Gets adaptive_ref_pic_marking_mode_flag.</summary>
    public bool AdaptiveMarking { get; private set; }

    /// <summary>Gets the number of memory management control operations.</summary>
    public int MmcoCount { get; private set; }

    /// <summary>Gets memory_management_control_operation per operation.</summary>
    public int[] MmcoOp { get; } = new int[MaxOperations];

    /// <summary>Gets difference_of_pic_nums_minus1 or long_term_pic_num per operation.</summary>
    public int[] MmcoA { get; } = new int[MaxOperations];

    /// <summary>Gets long_term_frame_idx or max_long_term_frame_idx_plus1 per operation.</summary>
    public int[] MmcoB { get; } = new int[MaxOperations];

    /// <summary>Gets cabac_init_idc.</summary>
    public int CabacInitIdc { get; private set; }

    /// <summary>Gets SliceQPY.</summary>
    public int SliceQp { get; private set; }

    /// <summary>Gets disable_deblocking_filter_idc.</summary>
    public int DisableDeblocking { get; private set; }

    /// <summary>Gets FilterOffsetA (slice_alpha_c0_offset_div2 * 2).</summary>
    public int FilterOffsetA { get; private set; }

    /// <summary>Gets FilterOffsetB (slice_beta_offset_div2 * 2).</summary>
    public int FilterOffsetB { get; private set; }

    /// <summary>Gets a value indicating whether memory_management_control_operation 5 is present.</summary>
    public bool HasMmco5 { get; private set; }

    /// <summary>Parses a slice header; the reader is left at the start of slice_data().</summary>
    /// <param name="r">The reader over the slice RBSP.</param>
    /// <param name="nalType">nal_unit_type (1 or 5).</param>
    /// <param name="nalRefIdc">nal_ref_idc.</param>
    /// <param name="spsTable">Stored SPSs by id.</param>
    /// <param name="ppsTable">Stored PPSs by id.</param>
    /// <exception cref="CodecException">The header is invalid, references a missing parameter set, or is unsupported.</exception>
    public void Parse(ref BitstreamReader r, H264NalUnitType nalType, int nalRefIdc, H264Sps?[] spsTable, H264Pps?[] ppsTable)
    {
        NalType = nalType;
        NalRefIdc = nalRefIdc;
        FirstMb = H264Syntax.ReadUe(ref r, H264Sps.MaxDimensionInMbs * H264Sps.MaxDimensionInMbs, "first_mb_in_slice");
        var rawType = H264Syntax.ReadUe(ref r, 9, "slice_type") % 5;
        if (rawType >= 3)
        {
            H264Syntax.Unsupported("SP and SI slices are not supported.");
        }

        Type = (H264SliceType)rawType;
        var ppsId = H264Syntax.ReadUe(ref r, 255, "pic_parameter_set_id");
        var pps = ppsTable[ppsId] ?? throw new CodecException(CodecErrorKind.InvalidData, "A slice references a missing PPS.");
        if (pps.UnsupportedReason is { } ppsReason)
        {
            H264Syntax.Unsupported(ppsReason);
        }

        var sps = spsTable[pps.SpsId] ?? throw new CodecException(CodecErrorKind.InvalidData, "A slice references a missing SPS.");
        if (sps.UnsupportedReason is { } spsReason)
        {
            H264Syntax.Unsupported(spsReason);
        }

        Pps = pps;
        Sps = sps;
        if (FirstMb >= sps.MbCount)
        {
            CodecException.InvalidData("first_mb_in_slice is outside the picture.");
        }

        if (IsIdr && Type != H264SliceType.I)
        {
            CodecException.InvalidData("An IDR picture contains a P or B slice.");
        }

        FrameNum = (int)r.Read(sps.Log2MaxFrameNum);
        if (IsIdr && FrameNum != 0)
        {
            CodecException.InvalidData("An IDR picture has a non-zero frame_num.");
        }

        IdrPicId = IsIdr ? H264Syntax.ReadUe(ref r, 65535, "idr_pic_id") : 0;
        PocLsb = 0;
        DeltaPocBottom = 0;
        DeltaPoc0 = 0;
        DeltaPoc1 = 0;
        if (sps.PocType == 0)
        {
            PocLsb = (int)r.Read(sps.Log2MaxPocLsb);
            if (pps.BottomFieldPicOrderPresent)
            {
                DeltaPocBottom = H264Syntax.ReadSe(ref r, int.MinValue + 1, int.MaxValue, "delta_pic_order_cnt_bottom");
            }
        }
        else if (sps.PocType == 1 && !sps.DeltaPicOrderAlwaysZero)
        {
            DeltaPoc0 = H264Syntax.ReadSe(ref r, int.MinValue + 1, int.MaxValue, "delta_pic_order_cnt[0]");
            if (pps.BottomFieldPicOrderPresent)
            {
                DeltaPoc1 = H264Syntax.ReadSe(ref r, int.MinValue + 1, int.MaxValue, "delta_pic_order_cnt[1]");
            }
        }

        RedundantPicCnt = pps.RedundantPicCntPresent ? H264Syntax.ReadUe(ref r, 127, "redundant_pic_cnt") : 0;
        DirectSpatial = Type == H264SliceType.B && r.ReadBit();
        NumRefIdxActive[0] = Type == H264SliceType.I ? 0 : pps.NumRefIdxL0Default;
        NumRefIdxActive[1] = Type == H264SliceType.B ? pps.NumRefIdxL1Default : 0;
        if (Type != H264SliceType.I && r.ReadBit())
        {
            NumRefIdxActive[0] = H264Syntax.ReadUe(ref r, 31, "num_ref_idx_l0_active_minus1") + 1;
            if (Type == H264SliceType.B)
            {
                NumRefIdxActive[1] = H264Syntax.ReadUe(ref r, 31, "num_ref_idx_l1_active_minus1") + 1;
            }
        }

        ModificationCount[0] = 0;
        ModificationCount[1] = 0;
        for (var list = 0; list < (Type == H264SliceType.B ? 2 : Type == H264SliceType.P ? 1 : 0); list++)
        {
            if (!r.ReadBit())
            {
                continue;
            }

            var maxPicNum = 1 << sps.Log2MaxFrameNum;
            while (true)
            {
                var idc = H264Syntax.ReadUe(ref r, 3, "modification_of_pic_nums_idc");
                if (idc == 3)
                {
                    break;
                }

                var count = ModificationCount[list];
                if (count >= NumRefIdxActive[list])
                {
                    CodecException.InvalidData("A reference picture list has more modifications than entries.");
                }

                ModificationIdc[(list * MaxOperations) + count] = idc;
                ModificationValue[(list * MaxOperations) + count] = idc == 2
                    ? H264Syntax.ReadUe(ref r, 32, "long_term_pic_num")
                    : H264Syntax.ReadUe(ref r, maxPicNum - 1, "abs_diff_pic_num_minus1");
                ModificationCount[list] = count + 1;
            }
        }

        HasWeightTable = (pps.WeightedPred && Type == H264SliceType.P) || (pps.WeightedBipredIdc == 1 && Type == H264SliceType.B);
        if (HasWeightTable)
        {
            ParseWeights(ref r);
        }

        NoOutputOfPriorPics = false;
        LongTermReference = false;
        AdaptiveMarking = false;
        MmcoCount = 0;
        HasMmco5 = false;
        if (nalRefIdc != 0)
        {
            ParseMarking(ref r);
        }

        CabacInitIdc = pps.Cabac && Type != H264SliceType.I ? H264Syntax.ReadUe(ref r, 2, "cabac_init_idc") : 0;
        SliceQp = pps.PicInitQp + H264Syntax.ReadSe(ref r, -26 - pps.PicInitQp + 26, 51 - pps.PicInitQp, "slice_qp_delta");
        DisableDeblocking = 0;
        FilterOffsetA = 0;
        FilterOffsetB = 0;
        if (pps.DeblockingFilterControlPresent)
        {
            DisableDeblocking = H264Syntax.ReadUe(ref r, 2, "disable_deblocking_filter_idc");
            if (DisableDeblocking != 1)
            {
                FilterOffsetA = 2 * H264Syntax.ReadSe(ref r, -6, 6, "slice_alpha_c0_offset_div2");
                FilterOffsetB = 2 * H264Syntax.ReadSe(ref r, -6, 6, "slice_beta_offset_div2");
            }
        }
    }

    /// <summary>Parses pred_weight_table; absent entries take the default weight and zero offset.</summary>
    /// <param name="r">The reader.</param>
    private void ParseWeights(ref BitstreamReader r)
    {
        LumaLog2Denom = H264Syntax.ReadUe(ref r, 7, "luma_log2_weight_denom");
        ChromaLog2Denom = H264Syntax.ReadUe(ref r, 7, "chroma_log2_weight_denom");
        for (var list = 0; list < (Type == H264SliceType.B ? 2 : 1); list++)
        {
            for (var i = 0; i < NumRefIdxActive[list]; i++)
            {
                var index = ((list * 32) + i) * 3;
                Weights[index] = 1 << LumaLog2Denom;
                Offsets[index] = 0;
                if (r.ReadBit())
                {
                    Weights[index] = H264Syntax.ReadSe(ref r, -128, 127, "luma_weight");
                    Offsets[index] = H264Syntax.ReadSe(ref r, -128, 127, "luma_offset");
                }

                var chromaPresent = r.ReadBit();
                for (var c = 1; c <= 2; c++)
                {
                    Weights[index + c] = 1 << ChromaLog2Denom;
                    Offsets[index + c] = 0;
                    if (chromaPresent)
                    {
                        Weights[index + c] = H264Syntax.ReadSe(ref r, -128, 127, "chroma_weight");
                        Offsets[index + c] = H264Syntax.ReadSe(ref r, -128, 127, "chroma_offset");
                    }
                }
            }
        }
    }

    /// <summary>Parses dec_ref_pic_marking.</summary>
    /// <param name="r">The reader.</param>
    private void ParseMarking(ref BitstreamReader r)
    {
        if (IsIdr)
        {
            NoOutputOfPriorPics = r.ReadBit();
            LongTermReference = r.ReadBit();
            return;
        }

        AdaptiveMarking = r.ReadBit();
        if (!AdaptiveMarking)
        {
            return;
        }

        while (true)
        {
            var op = H264Syntax.ReadUe(ref r, 6, "memory_management_control_operation");
            if (op == 0)
            {
                break;
            }

            if (MmcoCount >= MaxOperations)
            {
                CodecException.InvalidData("A slice has too many memory management control operations.");
            }

            var maxPicNum = (1 << Sps.Log2MaxFrameNum) - 1;
            MmcoOp[MmcoCount] = op;
            MmcoA[MmcoCount] = op is 1 or 3 ? H264Syntax.ReadUe(ref r, maxPicNum, "difference_of_pic_nums_minus1")
                : op == 2 ? H264Syntax.ReadUe(ref r, 31, "long_term_pic_num") : 0;
            MmcoB[MmcoCount] = op is 3 or 6 ? H264Syntax.ReadUe(ref r, 15, "long_term_frame_idx")
                : op == 4 ? H264Syntax.ReadUe(ref r, 16, "max_long_term_frame_idx_plus1") : 0;
            HasMmco5 |= op == 5;
            MmcoCount++;
        }
    }
}
