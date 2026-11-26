// NAL unit framing, emulation prevention, parameter set parsing and the entropy tables of the managed H.264 decoder.
// Parameter sets are taken from the committed fixtures; the unsupported-feature cases use small hand-assembled RBSPs.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;
using RTSP.IngestService.Media.Codecs.Video.H264;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Parser and table tests.</summary>
public sealed class H264ParserTests
{
    /// <summary>Annex B splitting accepts 3- and 4-byte start codes and trims trailing zero bytes.</summary>
    [Fact]
    public void SplitsAnnexBWithMixedStartCodes()
    {
        byte[] data = [0, 0, 0, 1, 0x67, 0xAA, 0, 0, 1, 0x68, 0xBB, 0, 0, 0, 0, 1, 0x65, 0xCC, 0];
        var units = new List<(int Offset, int Length)>();
        H264NalUnits.SplitAnnexB(data, units, 16);
        Assert.Equal([(4, 2), (9, 2), (16, 2)], units);
    }

    /// <summary>Malformed Annex B framing is invalid data; too many NAL units is a resource limit.</summary>
    [Fact]
    public void RejectsMalformedAnnexB()
    {
        var units = new List<(int Offset, int Length)>();
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitAnnexB([0x65, 0, 0, 1, 0x65, 0x80], units, 16));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitAnnexB([0x65, 0x88, 0x80], units, 16));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitAnnexB([0, 0, 1, 0, 0, 1, 0x65, 0x80], units, 16));
        AssertKind(CodecErrorKind.ResourceLimit, () => H264NalUnits.SplitAnnexB([0, 0, 1, 9, 0x10, 0, 0, 1, 9, 0x10], units, 1));
    }

    /// <summary>Length-prefixed splitting honours 1, 2 and 4-byte length fields and rejects bad lengths.</summary>
    [Fact]
    public void SplitsLengthPrefixed()
    {
        var units = new List<(int Offset, int Length)>();
        H264NalUnits.SplitLengthPrefixed([0, 0, 0, 2, 0x67, 0xAA, 0, 0, 0, 1, 0x68], 4, units, 16);
        Assert.Equal([(4, 2), (10, 1)], units);
        H264NalUnits.SplitLengthPrefixed([0, 2, 0x67, 0xAA], 2, units, 16);
        Assert.Equal([(2, 2)], units);
        H264NalUnits.SplitLengthPrefixed([1, 0x09, 1, 0x09], 1, units, 16);
        Assert.Equal([(1, 1), (3, 1)], units);

        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitLengthPrefixed([0, 0, 0, 0], 4, units, 16));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitLengthPrefixed([0, 0, 0, 5, 0x65], 4, units, 16));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitLengthPrefixed([0, 1, 0x65, 0], 2, units, 16));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.SplitLengthPrefixed([0xFF, 0xFF, 0xFF, 0xFF, 0x65], 4, units, 16));
        AssertKind(CodecErrorKind.ResourceLimit, () => H264NalUnits.SplitLengthPrefixed([1, 9, 1, 9], 1, units, 1));
    }

    /// <summary>Emulation prevention bytes are removed; forbidden sequences are invalid data.</summary>
    [Fact]
    public void UnescapesEmulationPrevention()
    {
        var rbsp = new byte[20];
        var length = H264NalUnits.Unescape([0x11, 0, 0, 3, 1, 0, 0, 3, 0, 0x22, 0, 0, 3, 3, 0x80, 0, 0], rbsp);
        Assert.Equal(new byte[] { 0x11, 0, 0, 1, 0, 0, 0, 0x22, 0, 0, 3, 0x80 }, rbsp[..length]);
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.Unescape([0x11, 0, 0, 0, 0x80], new byte[8]));

        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.Unescape([0x11, 0, 0, 1, 0x80], new byte[8]));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.Unescape([0x11, 0, 0, 2, 0x80], new byte[8]));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.Unescape([0x11, 0, 0, 3, 0x80], new byte[8]));
    }

    /// <summary>The stop bit is the last set bit; cabac_zero_words after it are ignored.</summary>
    [Fact]
    public void FindsStopBit()
    {
        Assert.Equal(7, H264NalUnits.PayloadBits([0x01]));
        Assert.Equal(0, H264NalUnits.PayloadBits([0x80, 0, 0]));
        Assert.Equal(12, H264NalUnits.PayloadBits([0xFF, 0x08, 0, 0, 0, 0]));
        AssertKind(CodecErrorKind.InvalidData, () => H264NalUnits.PayloadBits([0, 0]));
    }

    /// <summary>The fixtures' sequence parameter sets parse to the encoded geometry, profile and colour.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="profile">profile_idc.</param>
    /// <param name="widthInMbs">The width in macroblocks.</param>
    /// <param name="heightInMbs">The height in macroblocks.</param>
    /// <param name="displayWidth">The cropped width.</param>
    /// <param name="displayHeight">The cropped height.</param>
    /// <param name="bt709">Whether BT.709 colour is signalled.</param>
    [Theory]
    [InlineData("baseline-cavlc-176x144", 66, 11, 9, 176, 144, false)]
    [InlineData("constrained-baseline-openh264-320x240", 66, 20, 15, 320, 240, false)]
    [InlineData("main-cabac-bpyramid-weightp-320x240", 77, 20, 15, 320, 240, false)]
    [InlineData("high-cropped-slices-bt709-198x110", 100, 13, 7, 198, 110, true)]
    [InlineData("mf-high-320x240", 100, 20, 15, 320, 240, false)]
    [InlineData("nvenc-high-bframes-320x240", 100, 20, 15, 320, 240, false)]
    public void ParsesFixtureSps(string name, int profile, int widthInMbs, int heightInMbs, int displayWidth, int displayHeight, bool bt709)
    {
        var sps = H264Sps.Parse(Rbsp(FirstNal(name, H264NalUnitType.Sps)));
        Assert.Null(sps.UnsupportedReason);
        Assert.Equal(profile, sps.ProfileIdc);
        Assert.Equal(widthInMbs, sps.WidthInMbs);
        Assert.Equal(heightInMbs, sps.HeightInMbs);
        Assert.Equal(displayWidth, sps.DisplayWidth);
        Assert.Equal(displayHeight, sps.DisplayHeight);
        if (bt709)
        {
            Assert.Equal(VideoColorInfo.Bt709, sps.Color);
        }
    }

    /// <summary>The fixtures' picture parameter sets select the expected entropy coder and weighted prediction.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="cabac">Whether CABAC is used.</param>
    /// <param name="weighted">weighted_pred_flag.</param>
    /// <param name="transform8x8">transform_8x8_mode_flag.</param>
    [Theory]
    [InlineData("baseline-cavlc-176x144", false, false, false)]
    [InlineData("main-cabac-bpyramid-weightp-320x240", true, true, false)]
    [InlineData("high-cavlc-8x8-256x192", false, true, true)]
    [InlineData("high-8x8-temporal-cqm-352x288", true, true, true)]
    public void ParsesFixturePps(string name, bool cabac, bool weighted, bool transform8x8)
    {
        var pps = H264Pps.Parse(Rbsp(FirstNal(name, H264NalUnitType.Pps)));
        Assert.Null(pps.UnsupportedReason);
        Assert.Equal(cabac, pps.Cabac);
        Assert.Equal(weighted, pps.WeightedPred);
        Assert.Equal(transform8x8, pps.Transform8x8Mode);
    }

    /// <summary>Valid streams outside the supported matrix are recorded at parse time and reported as unsupported.</summary>
    /// <param name="name">The rejection fixture.</param>
    /// <param name="reason">A fragment of the expected reason.</param>
    [Theory]
    [InlineData("reject-high10", "High 10")]
    [InlineData("reject-high422", "High 4:2:2")]
    [InlineData("reject-interlaced", "Interlaced")]
    [InlineData("reject-lossless444", "High 4:4:4")]
    public void RecordsUnsupportedSps(string name, string reason)
    {
        var sps = H264Sps.Parse(Rbsp(FirstNal(name, H264NalUnitType.Sps)));
        Assert.Contains(reason, sps.UnsupportedReason, StringComparison.Ordinal);
    }

    /// <summary>A PPS with slice groups (FMO) is recorded as unsupported.</summary>
    [Fact]
    public void RecordsUnsupportedSliceGroups()
    {
        // pps_id ue(0) '1', sps_id ue(0) '1', entropy_coding_mode_flag 0, bottom_field_pic_order 0, num_slice_groups_minus1 ue(1) '010', stop bit.
        var pps = H264Pps.Parse([0b1100_0101]);
        Assert.Contains("slice groups", pps.UnsupportedReason, StringComparison.Ordinal);
    }

    /// <summary>A truncated SPS is invalid data, never a crash.</summary>
    [Fact]
    public void RejectsEveryTruncatedSps()
    {
        var rbsp = Rbsp(FirstNal("high-cropped-slices-bt709-198x110", H264NalUnitType.Sps));
        for (var length = 0; length < rbsp.Length - 1; length++)
        {
            var truncated = rbsp[..length];
            try
            {
                var sps = H264Sps.Parse(truncated);
                Assert.Fail($"A {length}-byte SPS prefix parsed as {sps.DisplayWidth}x{sps.DisplayHeight}.");
            }
            catch (CodecException e)
            {
                Assert.Equal(CodecErrorKind.InvalidData, e.Kind);
            }
        }
    }

    /// <summary>The CAVLC tables are prefix-free (checked at construction) and have the symbol counts of Tables 9-5, 9-7, 9-8 and 9-10.</summary>
    [Fact]
    public void CavlcTablesHaveSpecifiedShape()
    {
        foreach (var code in H264CavlcCodes.CoeffToken)
        {
            AssertCode(code, 62);
        }

        AssertCode(H264CavlcCodes.ChromaDcCoeffToken, 14);
        for (var r = 0; r < 15; r++)
        {
            AssertCode(H264CavlcCodes.TotalZeros[r], 16 - r);
        }

        for (var r = 0; r < 3; r++)
        {
            AssertCode(H264CavlcCodes.ChromaDcTotalZeros[r], 4 - r);
        }

        for (var r = 0; r < 7; r++)
        {
            AssertCode(H264CavlcCodes.RunBefore[r], r < 6 ? r + 2 : 15);
        }
    }

    /// <summary>Spot checks of Table 9-5 for 0 &lt;= nC &lt; 2.</summary>
    /// <param name="bits">The codeword followed by padding, MSB first.</param>
    /// <param name="totalCoeff">TotalCoeff.</param>
    /// <param name="trailingOnes">TrailingOnes.</param>
    [Theory]
    [InlineData(0b1000_0000u, 0, 0)]
    [InlineData(0b0100_0000u, 1, 1)]
    [InlineData(0b0010_0000u, 2, 2)]
    [InlineData(0b0001_1000u, 3, 3)]
    [InlineData(0b0001_0100u, 1, 0)]
    [InlineData(0b0001_0000u, 2, 1)]
    public void DecodesCoeffTokenCodewords(uint bits, int totalCoeff, int trailingOnes)
    {
        var code = H264CavlcCodes.CoeffToken[0];
        var reader = new BitstreamReader([(byte)bits, 0xFF, 0xFF]);
        Assert.Equal((totalCoeff * 4) + trailingOnes, code.Symbols[code.Decoder.Decode(ref reader)]);
    }

    /// <summary>The CABAC tables have the sizes and the first and last entries of Tables 9-12 to 9-33, 9-44 and 9-45.</summary>
    [Fact]
    public void CabacTablesHaveSpecifiedShape()
    {
        Assert.Equal(64 * 4, H264CabacTables.RangeLps.Length);
        Assert.Equal(new byte[] { 128, 176, 208, 240 }, H264CabacTables.RangeLps[..4].ToArray());
        Assert.Equal(new byte[] { 6, 7, 8, 9 }, H264CabacTables.RangeLps[248..252].ToArray());
        Assert.Equal(new byte[] { 2, 2, 2, 2 }, H264CabacTables.RangeLps[252..].ToArray());
        Assert.Equal(64, H264CabacTables.NextStateLps.Length);
        Assert.Equal(64, H264CabacTables.NextStateMps.Length);
        Assert.Equal(0, H264CabacTables.NextStateLps[0]);
        Assert.Equal(63, H264CabacTables.NextStateLps[63]);
        Assert.Equal(1, H264CabacTables.NextStateMps[0]);
        Assert.Equal(62, H264CabacTables.NextStateMps[62]);
        Assert.Equal(63, H264CabacTables.NextStateMps[63]);

        // ctxIdx 0 (mb_type SI prefix) is (m, n) = (20, -15) in every model; ctxIdx 11 (mb_skip_flag P) is (23, 33) for cabac_init_idc 0.
        Assert.Equal(H264CabacTables.ContextCount * 2 * 4, H264CabacTables.Initialization.Length);
        Assert.Equal(20, H264CabacTables.Initialization[0]);
        Assert.Equal(-15, H264CabacTables.Initialization[1]);
        Assert.Equal(23, H264CabacTables.Initialization[(H264CabacTables.ContextCount * 2) + 22]);
        Assert.Equal(33, H264CabacTables.Initialization[(H264CabacTables.ContextCount * 2) + 23]);
    }

    /// <summary>Asserts a code's symbol count and that its symbols are distinct.</summary>
    /// <param name="code">The code.</param>
    /// <param name="symbols">The expected symbol count.</param>
    private static void AssertCode(H264CavlcCodes.Code code, int symbols)
    {
        Assert.Equal(symbols, code.Symbols.Length);
        Assert.Equal(symbols, code.Decoder.SymbolCount);
        Assert.Equal(code.Symbols.Length, code.Symbols.Distinct().Count());
    }

    /// <summary>Returns the first NAL unit of a type in a fixture.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="type">The type.</param>
    /// <returns>The NAL unit including its header.</returns>
    private static byte[] FirstNal(string name, H264NalUnitType type) =>
        H264TestSupport.SplitNalUnits(File.ReadAllBytes(Path.Combine(H264TestSupport.FixtureDirectory, $"h264-{name}.h264")))
            .First(u => (H264NalUnitType)(u[0] & 0x1F) == type);

    /// <summary>Returns a NAL unit's RBSP.</summary>
    /// <param name="nal">The NAL unit.</param>
    /// <returns>The RBSP.</returns>
    private static byte[] Rbsp(byte[] nal)
    {
        var rbsp = new byte[nal.Length];
        return rbsp[..H264NalUnits.Unescape(nal.AsSpan(1), rbsp)];
    }

    /// <summary>Asserts that an action throws a codec exception of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="action">The action.</param>
    private static void AssertKind(CodecErrorKind kind, Action action) => Assert.Equal(kind, Assert.Throws<CodecException>(action).Kind);
}
