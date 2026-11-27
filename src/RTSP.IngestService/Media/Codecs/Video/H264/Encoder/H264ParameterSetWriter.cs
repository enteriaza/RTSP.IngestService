// Sequence and picture parameter sets of the encoder (ITU-T H.264 7.3.2.1, 7.3.2.2, E.1.1), level selection
// (Table A-1) and NAL unit packaging with emulation prevention (7.4.1). The SPS signals High profile, 4:2:0, 8-bit,
// progressive frames, cropping to the exact output size and a VUI with BT.709 limited-range colour and a bitstream
// restriction of zero reordered frames, so decoders output every picture as soon as it is decoded.

using System.Buffers.Binary;

namespace RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

/// <summary>The fixed sequence-level syntax choices of one encoder configuration.</summary>
/// <param name="ProfileIdc">profile_idc.</param>
/// <param name="LevelIdc">level_idc.</param>
/// <param name="WidthInMbs">Picture width in macroblocks.</param>
/// <param name="HeightInMbs">Picture height in macroblocks.</param>
/// <param name="Width">Output width.</param>
/// <param name="Height">Output height.</param>
/// <param name="MaxReferenceFrames">max_num_ref_frames.</param>
/// <param name="PocType">pic_order_cnt_type: 0, 1 or 2.</param>
/// <param name="Log2MaxFrameNum">log2_max_frame_num.</param>
/// <param name="Log2MaxPocLsb">log2_max_pic_order_cnt_lsb for POC type 0.</param>
/// <param name="GapsAllowed">gaps_in_frame_num_value_allowed_flag.</param>
/// <param name="RedundantPicCntPresent">redundant_pic_cnt_present_flag.</param>
/// <param name="WeightedBipredIdc">weighted_bipred_idc.</param>
/// <param name="MaxNumReorderFrames">max_num_reorder_frames of the bitstream restriction.</param>
/// <param name="MaxVerticalMv">The largest vertical motion vector magnitude the level allows, in quarter samples.</param>
internal sealed record H264SequenceLayout(
    int ProfileIdc,
    int LevelIdc,
    int WidthInMbs,
    int HeightInMbs,
    int Width,
    int Height,
    int MaxReferenceFrames,
    int PocType,
    int Log2MaxFrameNum,
    int Log2MaxPocLsb,
    bool GapsAllowed,
    bool RedundantPicCntPresent,
    int WeightedBipredIdc,
    int MaxNumReorderFrames,
    int MaxVerticalMv)
{
    /// <summary>POC type 1: offset_for_non_ref_pic.</summary>
    public const int OffsetForNonRefPic = -1;

    /// <summary>POC type 1: offset_for_ref_frame[0], the only entry of the cycle.</summary>
    public const int OffsetForRefFrame = 2;

    /// <summary>Gets the number of macroblocks in a picture.</summary>
    public int Macroblocks => WidthInMbs * HeightInMbs;
}

/// <summary>Writes parameter sets and NAL units.</summary>
internal static class H264ParameterSetWriter
{
    /// <summary>The 4-byte Annex B start code.</summary>
    public static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    /// <summary>The H.264 levels: level_idc, MaxMBPS, MaxFS, MaxDpbMbs, MaxBR (1000 bit/s), MaxCPB (1000 bit), max vertical MV (quarter samples).</summary>
    private static readonly (int Idc, int Mbps, int Fs, int DpbMbs, int Br, int Cpb, int Mv)[] Levels =
    [
        (10, 1485, 99, 396, 64, 175, 256),
        (11, 3000, 396, 900, 192, 500, 512),
        (12, 6000, 396, 2376, 384, 1000, 512),
        (13, 11880, 396, 2376, 768, 2000, 512),
        (20, 11880, 396, 2376, 2000, 2000, 512),
        (21, 19800, 792, 4752, 4000, 4000, 1024),
        (22, 20250, 1620, 8100, 4000, 4000, 1024),
        (30, 40500, 1620, 8100, 10000, 10000, 1024),
        (31, 108000, 3600, 18000, 14000, 14000, 2048),
        (32, 216000, 5120, 20480, 20000, 20000, 2048),
        (40, 245760, 8192, 32768, 20000, 25000, 2048),
        (41, 245760, 8192, 32768, 50000, 62500, 2048),
        (42, 522240, 8704, 34816, 50000, 62500, 2048),
        (50, 589824, 22080, 110400, 135000, 135000, 2048),
        (51, 983040, 36864, 184320, 240000, 240000, 2048),
        (52, 2073600, 36864, 184320, 240000, 240000, 2048),
    ];

    /// <summary>Chooses the lowest level that admits the configuration (Table A-1, High profile cpbBrVclFactor 1250).</summary>
    /// <param name="widthInMbs">Width in macroblocks.</param>
    /// <param name="heightInMbs">Height in macroblocks.</param>
    /// <param name="frameRate">The highest frame rate.</param>
    /// <param name="references">max_num_ref_frames.</param>
    /// <param name="bitrate">The VBV peak rate in bit/s.</param>
    /// <param name="bufferBits">The VBV buffer in bits.</param>
    /// <returns>level_idc and the vertical motion vector limit.</returns>
    /// <exception cref="CodecException"><see cref="CodecErrorKind.Unsupported"/>: no level admits the configuration.</exception>
    public static (int LevelIdc, int MaxVerticalMv) SelectLevel(int widthInMbs, int heightInMbs, int frameRate, int references, long bitrate, long bufferBits)
    {
        var fs = widthInMbs * heightInMbs;
        foreach (var level in Levels)
        {
            var side = Math.Sqrt(level.Fs * 8.0);
            if (fs <= level.Fs && widthInMbs <= side && heightInMbs <= side && (long)fs * frameRate <= level.Mbps
                && (long)fs * references <= level.DpbMbs && bitrate <= level.Br * 1250L && bufferBits <= level.Cpb * 1250L)
            {
                return (level.Idc, level.Mv);
            }
        }

        throw new CodecException(CodecErrorKind.Unsupported, "No H.264 level admits the picture size, frame rate, reference count and bitrate.");
    }

    /// <summary>Writes seq_parameter_set_rbsp().</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <returns>The RBSP.</returns>
    public static byte[] WriteSps(H264SequenceLayout layout)
    {
        using var w = new H264BitWriter(64, 256);
        w.Write((uint)layout.ProfileIdc, 8);
        w.Write(0, 8);
        w.Write((uint)layout.LevelIdc, 8);
        w.WriteUe(0);
        if (layout.ProfileIdc is 100 or 110 or 122 or 244)
        {
            w.WriteUe(1);
            w.WriteUe(0);
            w.WriteUe(0);
            w.WriteBit(false);
            w.WriteBit(false);
        }

        w.WriteUe(layout.Log2MaxFrameNum - 4);
        w.WriteUe(layout.PocType);
        if (layout.PocType == 0)
        {
            w.WriteUe(layout.Log2MaxPocLsb - 4);
        }
        else if (layout.PocType == 1)
        {
            w.WriteBit(false);
            w.WriteSe(H264SequenceLayout.OffsetForNonRefPic);
            w.WriteSe(0);
            w.WriteUe(1);
            w.WriteSe(H264SequenceLayout.OffsetForRefFrame);
        }

        w.WriteUe(layout.MaxReferenceFrames);
        w.WriteBit(layout.GapsAllowed);
        w.WriteUe(layout.WidthInMbs - 1);
        w.WriteUe(layout.HeightInMbs - 1);
        w.WriteBit(true);
        w.WriteBit(true);
        var cropRight = ((layout.WidthInMbs * 16) - layout.Width) / 2;
        var cropBottom = ((layout.HeightInMbs * 16) - layout.Height) / 2;
        w.WriteBit(cropRight != 0 || cropBottom != 0);
        if (cropRight != 0 || cropBottom != 0)
        {
            w.WriteUe(0);
            w.WriteUe(cropRight);
            w.WriteUe(0);
            w.WriteUe(cropBottom);
        }

        w.WriteBit(true);
        WriteVui(w, layout);
        w.WriteTrailingBits();
        return w.Written.ToArray();
    }

    /// <summary>Writes pic_parameter_set_rbsp().</summary>
    /// <param name="layout">The sequence layout.</param>
    /// <returns>The RBSP.</returns>
    public static byte[] WritePps(H264SequenceLayout layout)
    {
        using var w = new H264BitWriter(32, 64);
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteBit(false);
        w.WriteBit(false);
        w.WriteUe(0);
        w.WriteUe(layout.MaxReferenceFrames - 1);
        w.WriteUe(0);
        w.WriteBit(false);
        w.Write((uint)layout.WeightedBipredIdc, 2);
        w.WriteSe(0);
        w.WriteSe(0);
        w.WriteSe(0);
        w.WriteBit(true);
        w.WriteBit(false);
        w.WriteBit(layout.RedundantPicCntPresent);
        w.WriteTrailingBits();
        return w.Written.ToArray();
    }

    /// <summary>Builds the AVCDecoderConfigurationRecord (ISO/IEC 14496-15 5.3.3.1) with 4-byte NAL lengths.</summary>
    /// <param name="sps">The SPS NAL unit, header included.</param>
    /// <param name="pps">The PPS NAL unit, header included.</param>
    /// <returns>The record.</returns>
    public static byte[] BuildAvcC(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        var record = new byte[11 + sps.Length + pps.Length];
        record[0] = 1;
        record[1] = sps[1];
        record[2] = sps[2];
        record[3] = sps[3];
        record[4] = 0xFF;
        record[5] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(6), (ushort)sps.Length);
        sps.CopyTo(record.AsSpan(8));
        var p = 8 + sps.Length;
        record[p] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(p + 1), (ushort)pps.Length);
        pps.CopyTo(record.AsSpan(p + 3));
        if (sps[1] is 100 or 110 or 122 or 244)
        {
            // High profiles append chroma_format, bit depths and the SPS extension count (5.3.3.1.2).
            Array.Resize(ref record, record.Length + 4);
            record[^4] = 0xFC | 1;
            record[^3] = 0xF8;
            record[^2] = 0xF8;
            record[^1] = 0;
        }

        return record;
    }

    /// <summary>Returns the size of a NAL unit after emulation prevention, header included.</summary>
    /// <param name="rbsp">The RBSP.</param>
    /// <returns>The size in bytes.</returns>
    public static int EscapedSize(ReadOnlySpan<byte> rbsp)
    {
        var size = 1 + rbsp.Length;
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                size++;
                zeros = 0;
            }

            zeros = b == 0 ? zeros + 1 : 0;
        }

        return size;
    }

    /// <summary>Writes a NAL unit: the header byte and the RBSP with emulation prevention bytes.</summary>
    /// <param name="nalRefIdc">nal_ref_idc.</param>
    /// <param name="type">nal_unit_type.</param>
    /// <param name="rbsp">The RBSP.</param>
    /// <param name="destination">At least <see cref="EscapedSize"/> bytes.</param>
    /// <returns>The bytes written.</returns>
    public static int WriteNal(int nalRefIdc, int type, ReadOnlySpan<byte> rbsp, Span<byte> destination)
    {
        destination[0] = (byte)((nalRefIdc << 5) | type);
        var n = 1;
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                destination[n++] = 3;
                zeros = 0;
            }

            destination[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return n;
    }

    /// <summary>Returns a NAL unit as a new array.</summary>
    /// <param name="nalRefIdc">nal_ref_idc.</param>
    /// <param name="type">nal_unit_type.</param>
    /// <param name="rbsp">The RBSP.</param>
    /// <returns>The NAL unit.</returns>
    public static byte[] Nal(int nalRefIdc, int type, ReadOnlySpan<byte> rbsp)
    {
        var nal = new byte[EscapedSize(rbsp)];
        WriteNal(nalRefIdc, type, rbsp, nal);
        return nal;
    }

    /// <summary>Writes vui_parameters() (E.1.1).</summary>
    /// <param name="w">The writer.</param>
    /// <param name="layout">The sequence layout.</param>
    private static void WriteVui(H264BitWriter w, H264SequenceLayout layout)
    {
        w.WriteBit(false);
        w.WriteBit(false);
        w.WriteBit(true);
        w.Write(5, 3);
        w.WriteBit(false);
        w.WriteBit(true);
        w.Write(1, 8);
        w.Write(1, 8);
        w.Write(1, 8);
        w.WriteBit(false);
        w.WriteBit(false);
        w.WriteBit(false);
        w.WriteBit(false);
        w.WriteBit(false);
        w.WriteBit(true);
        w.WriteBit(true);
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteUe(15);
        w.WriteUe(15);
        w.WriteUe(layout.MaxNumReorderFrames);
        w.WriteUe(Math.Max(layout.MaxReferenceFrames, layout.MaxNumReorderFrames));
    }
}
