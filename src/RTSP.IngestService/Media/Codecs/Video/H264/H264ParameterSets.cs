// Sequence and picture parameter sets (ITU-T H.264 7.3.2.1, 7.3.2.2, Annex E VUI). A parameter set that is valid
// syntax but uses a feature outside the decoder's scope is stored with an UnsupportedReason, so the failure is
// reported when a slice activates it, not when an unused parameter set is merely transmitted.

using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Media.Codecs.Video.H264;

/// <summary>A parsed sequence parameter set.</summary>
internal sealed class H264Sps
{
    /// <summary>The largest picture width or height in macroblocks the parser accepts.</summary>
    public const int MaxDimensionInMbs = VideoFrame.MaxDimension / 16;

    /// <summary>Gets the RBSP, which identifies the content for replacement checks.</summary>
    public required byte[] Rbsp { get; init; }

    /// <summary>Gets seq_parameter_set_id.</summary>
    public int Id { get; init; }

    /// <summary>Gets profile_idc.</summary>
    public int ProfileIdc { get; init; }

    /// <summary>Gets level_idc.</summary>
    public int LevelIdc { get; init; }

    /// <summary>Gets a value indicating whether constraint_set3_flag is set.</summary>
    public bool ConstraintSet3 { get; init; }

    /// <summary>Gets why the decoder cannot decode this sequence, or <see langword="null"/>.</summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>Gets the six 4x4 scaling lists (Intra Y, Cb, Cr, Inter Y, Cb, Cr), raster order, 16 each.</summary>
    public byte[] Scaling4x4 { get; init; } = [];

    /// <summary>Gets the two 8x8 scaling lists (Intra Y, Inter Y), raster order, 64 each.</summary>
    public byte[] Scaling8x8 { get; init; } = [];

    /// <summary>Gets a value indicating whether seq_scaling_matrix_present_flag is set.</summary>
    public bool ScalingMatrixPresent { get; init; }

    /// <summary>Gets log2_max_frame_num_minus4 + 4.</summary>
    public int Log2MaxFrameNum { get; init; }

    /// <summary>Gets pic_order_cnt_type.</summary>
    public int PocType { get; init; }

    /// <summary>Gets log2_max_pic_order_cnt_lsb_minus4 + 4.</summary>
    public int Log2MaxPocLsb { get; init; }

    /// <summary>Gets delta_pic_order_always_zero_flag.</summary>
    public bool DeltaPicOrderAlwaysZero { get; init; }

    /// <summary>Gets offset_for_non_ref_pic.</summary>
    public int OffsetForNonRefPic { get; init; }

    /// <summary>Gets offset_for_top_to_bottom_field.</summary>
    public int OffsetForTopToBottomField { get; init; }

    /// <summary>Gets offset_for_ref_frame, num_ref_frames_in_pic_order_cnt_cycle entries.</summary>
    public int[] OffsetForRefFrame { get; init; } = [];

    /// <summary>Gets max_num_ref_frames.</summary>
    public int MaxNumRefFrames { get; init; }

    /// <summary>Gets gaps_in_frame_num_value_allowed_flag.</summary>
    public bool GapsInFrameNumAllowed { get; init; }

    /// <summary>Gets the width in macroblocks.</summary>
    public int WidthInMbs { get; init; }

    /// <summary>Gets the height in macroblocks.</summary>
    public int HeightInMbs { get; init; }

    /// <summary>Gets direct_8x8_inference_flag.</summary>
    public bool Direct8x8Inference { get; init; }

    /// <summary>Gets the left crop in luma samples.</summary>
    public int CropLeft { get; init; }

    /// <summary>Gets the right crop in luma samples.</summary>
    public int CropRight { get; init; }

    /// <summary>Gets the top crop in luma samples.</summary>
    public int CropTop { get; init; }

    /// <summary>Gets the bottom crop in luma samples.</summary>
    public int CropBottom { get; init; }

    /// <summary>Gets the VUI colour description, or <see cref="VideoColorInfo.Unspecified"/>.</summary>
    public VideoColorInfo Color { get; init; } = VideoColorInfo.Unspecified;

    /// <summary>Gets max_num_reorder_frames, or -1 when the VUI has no bitstream restriction.</summary>
    public int MaxNumReorderFrames { get; init; } = -1;

    /// <summary>Gets max_dec_frame_buffering, or -1 when the VUI has no bitstream restriction.</summary>
    public int MaxDecFrameBuffering { get; init; } = -1;

    /// <summary>Gets the macroblock count of a frame.</summary>
    public int MbCount => WidthInMbs * HeightInMbs;

    /// <summary>Gets the cropped (display) width.</summary>
    public int DisplayWidth => (WidthInMbs * 16) - CropLeft - CropRight;

    /// <summary>Gets the cropped (display) height.</summary>
    public int DisplayHeight => (HeightInMbs * 16) - CropTop - CropBottom;

    /// <summary>Parses an SPS RBSP.</summary>
    /// <param name="rbsp">The RBSP after the NAL header.</param>
    /// <returns>The SPS.</returns>
    /// <exception cref="CodecException">The syntax is invalid or exceeds the parser's structural limits.</exception>
    public static H264Sps Parse(ReadOnlySpan<byte> rbsp)
    {
        var payloadBits = H264NalUnits.PayloadBits(rbsp);
        var r = new BitstreamReader(rbsp);
        var profile = (int)r.Read(8);
        var constraints = r.Read(8);
        var level = (int)r.Read(8);
        var id = H264Syntax.ReadUe(ref r, 31, "seq_parameter_set_id");
        var raw = rbsp.ToArray();
        string? unsupported = profile switch
        {
            66 or 77 or 100 => null,
            88 => "The Extended profile is not supported.",
            110 or 122 or 244 or 44 => "High 10, High 4:2:2 and High 4:4:4 profiles are not supported.",
            _ => $"profile_idc {profile} is not supported.",
        };
        if (unsupported is not null)
        {
            return new H264Sps { Rbsp = raw, Id = id, ProfileIdc = profile, LevelIdc = level, UnsupportedReason = unsupported };
        }

        var scaling4x4 = new byte[6 * 16];
        var scaling8x8 = new byte[2 * 64];
        var matrixPresent = false;
        if (profile == 100)
        {
            var chromaFormat = H264Syntax.ReadUe(ref r, 3, "chroma_format_idc");
            if (chromaFormat == 3)
            {
                r.Skip(1);
            }

            var bitDepthLuma = H264Syntax.ReadUe(ref r, 6, "bit_depth_luma_minus8") + 8;
            var bitDepthChroma = H264Syntax.ReadUe(ref r, 6, "bit_depth_chroma_minus8") + 8;
            var bypass = r.ReadBit();
            if (chromaFormat != 1 || bitDepthLuma != 8 || bitDepthChroma != 8 || bypass)
            {
                return new H264Sps
                {
                    Rbsp = raw,
                    Id = id,
                    ProfileIdc = profile,
                    LevelIdc = level,
                    UnsupportedReason = chromaFormat != 1 ? "Only 4:2:0 chroma is supported." : bypass ? "Lossless transform bypass is not supported." : "Only 8-bit video is supported.",
                };
            }

            matrixPresent = r.ReadBit();
            if (matrixPresent)
            {
                H264ScalingLists.Parse(ref r, 8, scaling4x4, scaling8x8, null);
            }
        }

        if (!matrixPresent)
        {
            H264ScalingLists.Flat(scaling4x4, scaling8x8);
        }

        var log2MaxFrameNum = H264Syntax.ReadUe(ref r, 12, "log2_max_frame_num_minus4") + 4;
        var pocType = H264Syntax.ReadUe(ref r, 2, "pic_order_cnt_type");
        var log2MaxPocLsb = 0;
        var alwaysZero = false;
        var offsetNonRef = 0;
        var offsetTopBottom = 0;
        int[] offsetRef = [];
        if (pocType == 0)
        {
            log2MaxPocLsb = H264Syntax.ReadUe(ref r, 12, "log2_max_pic_order_cnt_lsb_minus4") + 4;
        }
        else if (pocType == 1)
        {
            alwaysZero = r.ReadBit();
            offsetNonRef = H264Syntax.ReadSe(ref r, int.MinValue + 1, int.MaxValue, "offset_for_non_ref_pic");
            offsetTopBottom = H264Syntax.ReadSe(ref r, int.MinValue + 1, int.MaxValue, "offset_for_top_to_bottom_field");
            offsetRef = new int[H264Syntax.ReadUe(ref r, 255, "num_ref_frames_in_pic_order_cnt_cycle")];
            for (var i = 0; i < offsetRef.Length; i++)
            {
                offsetRef[i] = H264Syntax.ReadSe(ref r, int.MinValue + 1, int.MaxValue, "offset_for_ref_frame");
            }
        }

        var maxRefFrames = H264Syntax.ReadUe(ref r, 16, "max_num_ref_frames");
        var gaps = r.ReadBit();
        var widthInMbs = (long)r.ReadExpGolomb() + 1;
        var heightInMapUnits = (long)r.ReadExpGolomb() + 1;
        if (widthInMbs > MaxDimensionInMbs || heightInMapUnits > MaxDimensionInMbs)
        {
            throw new CodecException(CodecErrorKind.ResourceLimit, "The coded picture size exceeds the decoder's structural limit.");
        }

        var frameMbsOnly = r.ReadBit();
        if (!frameMbsOnly)
        {
            return new H264Sps { Rbsp = raw, Id = id, ProfileIdc = profile, LevelIdc = level, UnsupportedReason = "Interlaced (field or MBAFF) coding is not supported." };
        }

        var direct8x8 = r.ReadBit();
        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (r.ReadBit())
        {
            cropLeft = 2 * H264Syntax.ReadUe(ref r, MaxDimensionInMbs * 8, "frame_crop_left_offset");
            cropRight = 2 * H264Syntax.ReadUe(ref r, MaxDimensionInMbs * 8, "frame_crop_right_offset");
            cropTop = 2 * H264Syntax.ReadUe(ref r, MaxDimensionInMbs * 8, "frame_crop_top_offset");
            cropBottom = 2 * H264Syntax.ReadUe(ref r, MaxDimensionInMbs * 8, "frame_crop_bottom_offset");
            if (cropLeft + cropRight >= widthInMbs * 16 || cropTop + cropBottom >= heightInMapUnits * 16)
            {
                CodecException.InvalidData("The SPS frame cropping removes the whole picture.");
            }
        }

        var color = VideoColorInfo.Unspecified;
        var reorder = -1;
        var decBuffering = -1;
        if (r.ReadBit())
        {
            ParseVui(ref r, ref color, ref reorder, ref decBuffering);
        }

        if (r.Position != payloadBits)
        {
            CodecException.InvalidData("The SPS has trailing data or is truncated.");
        }

        return new H264Sps
        {
            Rbsp = raw,
            Id = id,
            ProfileIdc = profile,
            LevelIdc = level,
            ConstraintSet3 = (constraints & 0x10) != 0,
            Scaling4x4 = scaling4x4,
            Scaling8x8 = scaling8x8,
            ScalingMatrixPresent = matrixPresent,
            Log2MaxFrameNum = log2MaxFrameNum,
            PocType = pocType,
            Log2MaxPocLsb = log2MaxPocLsb,
            DeltaPicOrderAlwaysZero = alwaysZero,
            OffsetForNonRefPic = offsetNonRef,
            OffsetForTopToBottomField = offsetTopBottom,
            OffsetForRefFrame = offsetRef,
            MaxNumRefFrames = maxRefFrames,
            GapsInFrameNumAllowed = gaps,
            WidthInMbs = (int)widthInMbs,
            HeightInMbs = (int)heightInMapUnits,
            Direct8x8Inference = direct8x8,
            CropLeft = cropLeft,
            CropRight = cropRight,
            CropTop = cropTop,
            CropBottom = cropBottom,
            Color = color,
            MaxNumReorderFrames = reorder,
            MaxDecFrameBuffering = decBuffering,
        };
    }

    /// <summary>Parses vui_parameters (E.1.1), keeping the colour description and bitstream restriction.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="color">Receives the colour description.</param>
    /// <param name="reorder">Receives max_num_reorder_frames.</param>
    /// <param name="decBuffering">Receives max_dec_frame_buffering.</param>
    private static void ParseVui(ref BitstreamReader r, ref VideoColorInfo color, ref int reorder, ref int decBuffering)
    {
        if (r.ReadBit() && r.Read(8) == 255)
        {
            r.Skip(32);
        }

        if (r.ReadBit())
        {
            r.Skip(1);
        }

        if (r.ReadBit())
        {
            r.Skip(3);
            var fullRange = r.ReadBit();
            color = color with { FullRange = fullRange };
            if (r.ReadBit())
            {
                color = new VideoColorInfo((byte)r.Read(8), (byte)r.Read(8), (byte)r.Read(8), fullRange);
            }
        }

        if (r.ReadBit())
        {
            H264Syntax.ReadUe(ref r, 5, "chroma_sample_loc_type_top_field");
            H264Syntax.ReadUe(ref r, 5, "chroma_sample_loc_type_bottom_field");
        }

        if (r.ReadBit())
        {
            r.Skip(32);
            r.Skip(32);
            r.Skip(1);
        }

        var nalHrd = r.ReadBit();
        if (nalHrd)
        {
            ParseHrd(ref r);
        }

        var vclHrd = r.ReadBit();
        if (vclHrd)
        {
            ParseHrd(ref r);
        }

        if (nalHrd || vclHrd)
        {
            r.Skip(1);
        }

        r.Skip(1);
        if (r.ReadBit())
        {
            r.Skip(1);
            r.ReadExpGolomb();
            r.ReadExpGolomb();
            H264Syntax.ReadUe(ref r, 16, "log2_max_mv_length_horizontal");
            H264Syntax.ReadUe(ref r, 16, "log2_max_mv_length_vertical");
            reorder = H264Syntax.ReadUe(ref r, 16, "max_num_reorder_frames");
            decBuffering = H264Syntax.ReadUe(ref r, 16, "max_dec_frame_buffering");
            if (reorder > decBuffering)
            {
                CodecException.InvalidData("max_num_reorder_frames exceeds max_dec_frame_buffering.");
            }
        }
    }

    /// <summary>Skips hrd_parameters (E.1.2).</summary>
    /// <param name="r">The reader.</param>
    private static void ParseHrd(ref BitstreamReader r)
    {
        var count = H264Syntax.ReadUe(ref r, 31, "cpb_cnt_minus1") + 1;
        r.Skip(8);
        for (var i = 0; i < count; i++)
        {
            r.ReadExpGolomb();
            r.ReadExpGolomb();
            r.Skip(1);
        }

        r.Skip(20);
    }
}

/// <summary>A parsed picture parameter set.</summary>
internal sealed class H264Pps
{
    /// <summary>Gets the RBSP, which identifies the content for replacement checks.</summary>
    public required byte[] Rbsp { get; init; }

    /// <summary>Gets pic_parameter_set_id.</summary>
    public int Id { get; init; }

    /// <summary>Gets seq_parameter_set_id.</summary>
    public int SpsId { get; init; }

    /// <summary>Gets why the decoder cannot decode pictures using this PPS, or <see langword="null"/>.</summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>Gets a value indicating whether entropy_coding_mode_flag (CABAC) is set.</summary>
    public bool Cabac { get; init; }

    /// <summary>Gets bottom_field_pic_order_in_frame_present_flag.</summary>
    public bool BottomFieldPicOrderPresent { get; init; }

    /// <summary>Gets num_ref_idx_l0_default_active_minus1 + 1.</summary>
    public int NumRefIdxL0Default { get; init; }

    /// <summary>Gets num_ref_idx_l1_default_active_minus1 + 1.</summary>
    public int NumRefIdxL1Default { get; init; }

    /// <summary>Gets weighted_pred_flag.</summary>
    public bool WeightedPred { get; init; }

    /// <summary>Gets weighted_bipred_idc.</summary>
    public int WeightedBipredIdc { get; init; }

    /// <summary>Gets pic_init_qp_minus26 + 26.</summary>
    public int PicInitQp { get; init; }

    /// <summary>Gets chroma_qp_index_offset.</summary>
    public int ChromaQpIndexOffset { get; init; }

    /// <summary>Gets second_chroma_qp_index_offset.</summary>
    public int SecondChromaQpIndexOffset { get; init; }

    /// <summary>Gets deblocking_filter_control_present_flag.</summary>
    public bool DeblockingFilterControlPresent { get; init; }

    /// <summary>Gets constrained_intra_pred_flag.</summary>
    public bool ConstrainedIntraPred { get; init; }

    /// <summary>Gets redundant_pic_cnt_present_flag.</summary>
    public bool RedundantPicCntPresent { get; init; }

    /// <summary>Gets transform_8x8_mode_flag.</summary>
    public bool Transform8x8Mode { get; init; }

    /// <summary>Gets a value indicating whether pic_scaling_matrix_present_flag is set.</summary>
    public bool ScalingMatrixPresent { get; init; }

    /// <summary>Gets the transmitted lists; see <see cref="H264ScalingLists.Parse"/>.</summary>
    public H264ScalingLists.Transmitted? TransmittedLists { get; init; }

    /// <summary>Parses a PPS RBSP.</summary>
    /// <param name="rbsp">The RBSP after the NAL header.</param>
    /// <returns>The PPS.</returns>
    /// <exception cref="CodecException">The syntax is invalid.</exception>
    public static H264Pps Parse(ReadOnlySpan<byte> rbsp)
    {
        var payloadBits = H264NalUnits.PayloadBits(rbsp);
        var r = new BitstreamReader(rbsp);
        var raw = rbsp.ToArray();
        var id = H264Syntax.ReadUe(ref r, 255, "pic_parameter_set_id");
        var spsId = H264Syntax.ReadUe(ref r, 31, "seq_parameter_set_id");
        var cabac = r.ReadBit();
        var bottomPresent = r.ReadBit();
        var sliceGroups = H264Syntax.ReadUe(ref r, 7, "num_slice_groups_minus1") + 1;
        if (sliceGroups > 1)
        {
            return new H264Pps { Rbsp = raw, Id = id, SpsId = spsId, UnsupportedReason = "Flexible macroblock ordering (slice groups) is not supported." };
        }

        var l0 = H264Syntax.ReadUe(ref r, 31, "num_ref_idx_l0_default_active_minus1") + 1;
        var l1 = H264Syntax.ReadUe(ref r, 31, "num_ref_idx_l1_default_active_minus1") + 1;
        var weighted = r.ReadBit();
        var bipred = (int)r.Read(2);
        if (bipred == 3)
        {
            CodecException.InvalidData("weighted_bipred_idc is 3.");
        }

        var initQp = H264Syntax.ReadSe(ref r, -26, 25, "pic_init_qp_minus26") + 26;
        H264Syntax.ReadSe(ref r, -26, 25, "pic_init_qs_minus26");
        var chromaOffset = H264Syntax.ReadSe(ref r, -12, 12, "chroma_qp_index_offset");
        var deblockingControl = r.ReadBit();
        var constrainedIntra = r.ReadBit();
        var redundant = r.ReadBit();
        var transform8x8 = false;
        var matrixPresent = false;
        H264ScalingLists.Transmitted? lists = null;
        var secondOffset = chromaOffset;
        if (r.Position < payloadBits)
        {
            transform8x8 = r.ReadBit();
            matrixPresent = r.ReadBit();
            if (matrixPresent)
            {
                lists = new H264ScalingLists.Transmitted();
                H264ScalingLists.Parse(ref r, 6 + (transform8x8 ? 2 : 0), lists.List4x4, lists.List8x8, lists);
            }

            secondOffset = H264Syntax.ReadSe(ref r, -12, 12, "second_chroma_qp_index_offset");
        }

        if (r.Position != payloadBits)
        {
            CodecException.InvalidData("The PPS has trailing data or is truncated.");
        }

        return new H264Pps
        {
            Rbsp = raw,
            Id = id,
            SpsId = spsId,
            Cabac = cabac,
            BottomFieldPicOrderPresent = bottomPresent,
            NumRefIdxL0Default = l0,
            NumRefIdxL1Default = l1,
            WeightedPred = weighted,
            WeightedBipredIdc = bipred,
            PicInitQp = initQp,
            ChromaQpIndexOffset = chromaOffset,
            SecondChromaQpIndexOffset = secondOffset,
            DeblockingFilterControlPresent = deblockingControl,
            ConstrainedIntraPred = constrainedIntra,
            RedundantPicCntPresent = redundant,
            Transform8x8Mode = transform8x8,
            ScalingMatrixPresent = matrixPresent,
            TransmittedLists = lists,
        };
    }
}

/// <summary>Scaling list syntax (7.3.2.1.1.1) and the fall-back rules (Table 7-2).</summary>
internal static class H264ScalingLists
{
    /// <summary>Parses scaling lists. With <paramref name="transmitted"/> null (SPS) fall-back rule A is applied.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="count">The number of lists in the syntax: 8 for the SPS (4:2:0), 6 or 8 for the PPS.</param>
    /// <param name="lists4x4">Receives six 4x4 lists, raster order.</param>
    /// <param name="lists8x8">Receives two 8x8 lists, raster order.</param>
    /// <param name="transmitted">For a PPS, receives which lists were present and which use the default.</param>
    public static void Parse(ref BitstreamReader r, int count, byte[] lists4x4, byte[] lists8x8, Transmitted? transmitted)
    {
        for (var i = 0; i < count; i++)
        {
            var present = r.ReadBit();
            var size = i < 6 ? 16 : 64;
            var target = i < 6 ? lists4x4.AsSpan(i * 16, 16) : lists8x8.AsSpan((i - 6) * 64, 64);
            var useDefault = false;
            if (present)
            {
                useDefault = ReadList(ref r, target, size);
            }

            if (transmitted is not null)
            {
                transmitted.Present[i] = present;
                transmitted.UseDefault[i] = useDefault;
                continue;
            }

            if (!present || useDefault)
            {
                Fallback(i, present, lists4x4, lists8x8, null, null);
            }
        }
    }

    /// <summary>Fills flat (all 16) lists.</summary>
    /// <param name="lists4x4">Six 4x4 lists.</param>
    /// <param name="lists8x8">Two 8x8 lists.</param>
    public static void Flat(byte[] lists4x4, byte[] lists8x8)
    {
        lists4x4.AsSpan().Fill(16);
        lists8x8.AsSpan().Fill(16);
    }

    /// <summary>Resolves the lists in force for a picture.</summary>
    /// <param name="sps">The active SPS.</param>
    /// <param name="pps">The active PPS.</param>
    /// <param name="lists4x4">Receives six 4x4 lists.</param>
    /// <param name="lists8x8">Receives two 8x8 lists.</param>
    public static void Resolve(H264Sps sps, H264Pps pps, byte[] lists4x4, byte[] lists8x8)
    {
        if (!pps.ScalingMatrixPresent || pps.TransmittedLists is not { } t)
        {
            sps.Scaling4x4.CopyTo(lists4x4, 0);
            sps.Scaling8x8.CopyTo(lists8x8, 0);
            return;
        }

        var ruleB = sps.ScalingMatrixPresent;
        for (var i = 0; i < 8; i++)
        {
            if (t.Present[i] && !t.UseDefault[i])
            {
                if (i < 6)
                {
                    t.List4x4.AsSpan(i * 16, 16).CopyTo(lists4x4.AsSpan(i * 16));
                }
                else
                {
                    t.List8x8.AsSpan((i - 6) * 64, 64).CopyTo(lists8x8.AsSpan((i - 6) * 64));
                }
            }
            else
            {
                Fallback(i, t.Present[i], lists4x4, lists8x8, ruleB ? sps.Scaling4x4 : null, ruleB ? sps.Scaling8x8 : null);
            }
        }
    }

    /// <summary>Reads one scaling_list into raster order.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="target">The list.</param>
    /// <param name="size">16 or 64.</param>
    /// <returns>useDefaultScalingMatrixFlag.</returns>
    private static bool ReadList(ref BitstreamReader r, Span<byte> target, int size)
    {
        var scan = size == 16 ? H264Tables.Zigzag4x4 : H264Tables.Zigzag8x8;
        var last = 8;
        var next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
            {
                var delta = H264Syntax.ReadSe(ref r, -128, 127, "delta_scale");
                next = (last + delta + 256) % 256;
                if (j == 0 && next == 0)
                {
                    return true;
                }
            }

            var value = next == 0 ? last : next;
            target[scan[j]] = (byte)value;
            last = value;
        }

        return false;
    }

    /// <summary>Applies the fall-back rule for an absent list, or the default for a list that selects it.</summary>
    /// <param name="i">The list index.</param>
    /// <param name="present">Whether the list was transmitted (then it selected the default).</param>
    /// <param name="lists4x4">The 4x4 lists being built.</param>
    /// <param name="lists8x8">The 8x8 lists being built.</param>
    /// <param name="sequence4x4">For rule B, the SPS 4x4 lists; for rule A, null.</param>
    /// <param name="sequence8x8">For rule B, the SPS 8x8 lists; for rule A, null.</param>
    private static void Fallback(int i, bool present, byte[] lists4x4, byte[] lists8x8, byte[]? sequence4x4, byte[]? sequence8x8)
    {
        ReadOnlySpan<byte> source;
        if (present || i is 0 or 3 or 6 or 7)
        {
            source = (present || sequence4x4 is null) ? i switch
            {
                < 3 => H264Tables.Default4x4Intra,
                < 6 => H264Tables.Default4x4Inter,
                6 => H264Tables.Default8x8Intra,
                _ => H264Tables.Default8x8Inter,
            }
            : i < 6 ? sequence4x4.AsSpan(i * 16, 16) : sequence8x8.AsSpan((i - 6) * 64, 64);
        }
        else
        {
            source = lists4x4.AsSpan((i - 1) * 16, 16);
        }

        source.CopyTo(i < 6 ? lists4x4.AsSpan(i * 16, 16) : lists8x8.AsSpan((i - 6) * 64, 64));
    }

    /// <summary>The lists a PPS transmitted, resolved against the SPS at activation.</summary>
    internal sealed class Transmitted
    {
        /// <summary>Gets the six 4x4 lists as transmitted.</summary>
        public byte[] List4x4 { get; } = new byte[6 * 16];

        /// <summary>Gets the two 8x8 lists as transmitted.</summary>
        public byte[] List8x8 { get; } = new byte[2 * 64];

        /// <summary>Gets pic_scaling_list_present_flag per list.</summary>
        public bool[] Present { get; } = new bool[8];

        /// <summary>Gets useDefaultScalingMatrixFlag per list.</summary>
        public bool[] UseDefault { get; } = new bool[8];
    }
}
