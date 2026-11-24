// Tests for the minimal H.264 SPS reader: the fixture's real SPS, synthetic SPS covering cropping, high-profile
// fields, scaling lists, interlaced and 4:4:4 cropping units, picture order count type 1, emulation prevention, and
// rejection of truncated, wrongly typed and out-of-range SPS.

using RTSP.IngestService.Hls;

namespace RTSP.IngestService.Tests.Hls;

/// <summary>
/// Tests for <see cref="AvcSequenceParameterSet"/>.
/// </summary>
public sealed class AvcSequenceParameterSetTests
{
    /// <summary>Verifies the FFmpeg-generated fixture's SPS.</summary>
    [Fact]
    public void FixtureSps_GivesItsDimensions()
    {
        Assert.True(AvcSequenceParameterSet.TryParse(HlsFixture.Sps, out var sps, out var error), error);
        Assert.Equal((77, 160, 120), (sps.Profile, sps.Width, sps.Height));
        Assert.Equal(8, sps.BitDepthLuma);
    }

    /// <summary>Verifies frame cropping of a 1088-line baseline SPS to 1080 lines.</summary>
    [Fact]
    public void Cropping_IsApplied()
    {
        Assert.True(AvcSequenceParameterSet.TryParse(Baseline(119, 67, crop: (0, 0, 0, 4)), out var sps, out var error), error);
        Assert.Equal((66, 31, 1920, 1080), (sps.Profile, sps.Level, sps.Width, sps.Height));
    }

    /// <summary>Verifies an interlaced (field-coded) SPS doubles map-unit height and the vertical crop unit.</summary>
    [Fact]
    public void Interlaced_DoublesHeightAndCropUnit()
    {
        Assert.True(AvcSequenceParameterSet.TryParse(Baseline(44, 17, crop: (0, 0, 0, 2), frameMbsOnly: false), out var sps, out var error), error);
        Assert.Equal((720, 576 - 8), (sps.Width, sps.Height));
    }

    /// <summary>Verifies picture order count type 1 fields are skipped.</summary>
    [Fact]
    public void PocType1_IsSkipped()
    {
        Assert.True(AvcSequenceParameterSet.TryParse(Baseline(39, 29, pocType: 1), out var sps, out var error), error);
        Assert.Equal((640, 480), (sps.Width, sps.Height));
    }

    /// <summary>Verifies high-profile chroma, bit depth and scaling lists are read before the picture size.</summary>
    [Fact]
    public void HighProfile_WithScalingLists_IsRead()
    {
        Assert.True(AvcSequenceParameterSet.TryParse(High(chromaFormat: 1, bitDepthMinus8: 2, scalingLists: true, crop: (0, 0, 0, 4)), out var sps, out var error), error);
        Assert.Equal((100, 1, 10, 1920, 1080), (sps.Profile, sps.ChromaFormat, sps.BitDepthLuma, sps.Width, sps.Height));
    }

    /// <summary>Verifies 4:4:4 uses single-sample crop units.</summary>
    [Fact]
    public void Chroma444_UsesUnitCrop()
    {
        Assert.True(AvcSequenceParameterSet.TryParse(High(chromaFormat: 3, bitDepthMinus8: 0, scalingLists: false, crop: (1, 1, 0, 8)), out var sps, out var error), error);
        Assert.Equal((3, 1918, 1080), (sps.ChromaFormat, sps.Width, sps.Height));
    }

    /// <summary>Verifies emulation prevention bytes are removed before reading.</summary>
    [Fact]
    public void EmulationPrevention_IsRemoved()
    {
        // ue(0) x 2, then profile/level fields that contain 00 00 sequences once escaped.
        var rbsp = new BitWriter().Bits(66, 8).Bits(0, 8).Bits(0, 8).Ue(0).Ue(0).Ue(2).Ue(0).Bits(0, 1).Ue(9).Ue(7).Bits(1, 1).Bits(1, 1).Bits(0, 1).Bits(0, 1).ToRbsp();
        Assert.Equal(new byte[] { 66, 0, 0 }, rbsp[..3]);
        byte[] escaped = [0x67, 66, 0, 0, 3, .. rbsp[3..]];
        Assert.True(AvcSequenceParameterSet.TryParse(escaped, out var sps, out var error), error);
        Assert.Equal((0, 160, 128), (sps.Level, sps.Width, sps.Height));
    }

    /// <summary>Verifies invalid SPS NAL units are rejected with a reason.</summary>
    /// <param name="name">The case.</param>
    [Theory]
    [InlineData("short")]
    [InlineData("not-sps")]
    [InlineData("truncated")]
    [InlineData("crop-all")]
    [InlineData("too-wide")]
    [InlineData("chroma-4")]
    public void Invalid_IsRejected(string name)
    {
        var full = Baseline(119, 67, crop: (0, 0, 0, 4));
        byte[] nal = name switch
        {
            "short" => [0x67, 66, 0],
            "not-sps" => [0x68, .. full[1..]],
            "truncated" => full[..8],
            "crop-all" => Baseline(9, 9, crop: (0, 80, 0, 0)),
            "too-wide" => Baseline(1024, 9),
            _ => High(chromaFormat: 4, bitDepthMinus8: 0, scalingLists: false, crop: (0, 0, 0, 0)),
        };

        Assert.False(AvcSequenceParameterSet.TryParse(nal, out var sps, out var error));
        Assert.Null(sps);
        Assert.False(string.IsNullOrEmpty(error));
    }

    /// <summary>Builds a baseline SPS NAL unit.</summary>
    /// <param name="widthMbsMinus1">pic_width_in_mbs_minus1.</param>
    /// <param name="heightMapUnitsMinus1">pic_height_in_map_units_minus1.</param>
    /// <param name="crop">Left, right, top and bottom crop offsets, or none.</param>
    /// <param name="frameMbsOnly">frame_mbs_only_flag.</param>
    /// <param name="pocType">pic_order_cnt_type (0 or 1).</param>
    /// <returns>The NAL unit.</returns>
    internal static byte[] Baseline(int widthMbsMinus1, int heightMapUnitsMinus1, (int L, int R, int T, int B)? crop = null, bool frameMbsOnly = true, int pocType = 0)
    {
        var bits = new BitWriter().Bits(66, 8).Bits(0xC0, 8).Bits(31, 8).Ue(0).Ue(0).Ue(pocType);
        if (pocType == 0)
        {
            bits.Ue(2);
        }
        else
        {
            bits.Bits(0, 1).Se(-1).Se(2).Ue(2).Se(1).Se(-3);
        }

        bits.Ue(1).Bits(0, 1).Ue(widthMbsMinus1).Ue(heightMapUnitsMinus1).Bits(frameMbsOnly ? 1 : 0, 1);
        if (!frameMbsOnly)
        {
            bits.Bits(0, 1);
        }

        return Finish(bits.Bits(1, 1), crop);
    }

    /// <summary>Builds a 1920x1088 high-profile SPS NAL unit.</summary>
    /// <param name="chromaFormat">chroma_format_idc.</param>
    /// <param name="bitDepthMinus8">bit_depth_luma_minus8.</param>
    /// <param name="scalingLists">Whether scaling matrices are present.</param>
    /// <param name="crop">Crop offsets.</param>
    /// <returns>The NAL unit.</returns>
    private static byte[] High(int chromaFormat, int bitDepthMinus8, bool scalingLists, (int L, int R, int T, int B) crop)
    {
        var bits = new BitWriter().Bits(100, 8).Bits(0, 8).Bits(40, 8).Ue(0).Ue(chromaFormat);
        if (chromaFormat == 3)
        {
            bits.Bits(0, 1);
        }

        bits.Ue(bitDepthMinus8).Ue(bitDepthMinus8).Bits(0, 1).Bits(scalingLists ? 1 : 0, 1);
        if (scalingLists)
        {
            // List 0 explicit (16 deltas, the last ending the list early with next = 0), list 6 explicit (64 deltas).
            bits.Bits(1, 1);
            for (var j = 0; j < 15; j++)
            {
                bits.Se(j % 2 == 0 ? 3 : -2);
            }

            bits.Se(-18).Bits(0, 1).Bits(0, 1).Bits(0, 1).Bits(0, 1).Bits(0, 1).Bits(1, 1);
            for (var j = 0; j < 64; j++)
            {
                bits.Se(1);
            }

            bits.Bits(0, 1);
        }

        bits.Ue(0).Ue(0).Ue(2).Ue(4).Bits(0, 1).Ue(119).Ue(67).Bits(1, 1).Bits(1, 1);
        return Finish(bits, crop);
    }

    /// <summary>Adds cropping and the VUI flag.</summary>
    /// <param name="bits">The writer, positioned at frame_cropping_flag.</param>
    /// <param name="crop">Crop offsets.</param>
    /// <returns>The NAL unit.</returns>
    private static byte[] Finish(BitWriter bits, (int L, int R, int T, int B)? crop)
    {
        if (crop is { } c)
        {
            bits.Bits(1, 1).Ue(c.L).Ue(c.R).Ue(c.T).Ue(c.B);
        }
        else
        {
            bits.Bits(0, 1);
        }

        return [0x67, .. bits.Bits(0, 1).ToRbsp()];
    }
}
