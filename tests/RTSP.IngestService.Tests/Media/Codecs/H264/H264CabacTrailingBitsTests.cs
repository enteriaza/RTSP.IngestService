// The CABAC end-of-slice check (7.3.4, 9.3.3.2.2.3): after the terminating bin the engine must not have read past
// the rbsp_stop_one_bit, its last consumed bit must be 1, and at most MaxCabacTrailingBits (16) bits may separate the
// termination position from the stop bit. NVENC slices end exactly on the stop bit, so on that stream a tail of
// exactly 16 bits must decode identically and 17 must be rejected. libx264 slices already end before the stop bit,
// so their accepted padding must be a prefix 1..T with T below 16 (measured T is logged: 9, so the longest libx264
// tail is 7 bits, not the 1 to 4 the Phase 3 report states). Arbitrary bits inside the tolerated tail are not
// detected (the documented limitation); cabac_zero_words are ignored; and a cleared stop bit on an exact-end stream
// is rejected (on libx264 streams the engine's last bit may become the stop bit).

using System.Globalization;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Bounds of the CABAC end-of-slice tolerance on real CABAC streams.</summary>
public sealed class H264CabacTrailingBitsTests
{
    /// <summary>Fixtures whose CABAC slices end exactly on the stop bit.</summary>
    public static TheoryData<string> ExactEnd => ["nvenc-high-bframes-320x240"];

    /// <summary>libx264 fixtures whose CABAC slices end 1 to 4 bits before the stop bit.</summary>
    public static TheoryData<string> Padded => ["main-cabac-bpyramid-weightp-320x240", "high-8x8-temporal-cqm-352x288"];

    /// <summary>A 16-bit tail on every slice of an exact-end stream decodes identically; 17 bits are rejected.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [MemberData(nameof(ExactEnd))]
    public void ExactEndToleratesSixteenBits(string name)
    {
        AssertDecodesIdentically(name, rbsp => Pad(rbsp, Zeros(16)));
        AssertRejected(name, rbsp => Pad(rbsp, Zeros(17)));
    }

    /// <summary>On libx264 streams, which already end before the stop bit, the accepted padding is a prefix 1..T with T below 16.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [MemberData(nameof(Padded))]
    public void PaddedStreamsKeepTheBound(string name)
    {
        var threshold = 0;
        for (var k = 1; k <= 17; k++)
        {
            if (threshold == k - 1 && Decodes(name, rbsp => Pad(rbsp, Zeros(k))))
            {
                threshold = k;
            }
            else
            {
                Assert.False(Decodes(name, rbsp => Pad(rbsp, Zeros(k))), $"{k} added bits decode after a shorter padding was rejected.");
            }
        }

        Assert.InRange(threshold, 1, 15);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: up to {threshold} added bits accepted, so the longest existing tail is {16 - threshold} bits."));
    }

    /// <summary>Arbitrary bits inside the tolerated tail are not detected: the stream decodes identically.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [MemberData(nameof(ExactEnd))]
    public void BitsInsideTheTailAreNotDetected(string name) =>
        AssertDecodesIdentically(name, static rbsp => Pad(rbsp, [true, false, true, true, false, false, true, false, true, true, true, false, false, true, false, true]));

    /// <summary>cabac_zero_words after the RBSP trailing bits are ignored.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [MemberData(nameof(ExactEnd))]
    [MemberData(nameof(Padded))]
    public void CabacZeroWordsAreIgnored(string name)
    {
        var units = Rewrite(H264TestSupport.ReadAccessUnits(name), static nal => [.. nal, 0, 0, 3, 0, 0, 3, 0, 0, 3], firstOnly: false);
        AssertDecodes(name, units);
    }

    /// <summary>On an exact-end stream, a slice whose stop bit is cleared is rejected: the engine has read past the remaining last set bit.</summary>
    /// <param name="name">The fixture.</param>
    [Theory]
    [MemberData(nameof(ExactEnd))]
    public void ClearedStopBitIsRejected(string name) => AssertRejected(name, static rbsp =>
    {
        var stop = H264NalUnits.PayloadBits(rbsp);
        var copy = (byte[])rbsp.Clone();
        copy[stop >> 3] &= (byte)~(0x80 >> (stop & 7));
        return copy;
    });

    /// <summary>Applies an RBSP edit to every slice and asserts that the stream still decodes to the reference.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="edit">The edit.</param>
    private static void AssertDecodesIdentically(string name, Func<byte[], byte[]> edit) =>
        AssertDecodes(name, Rewrite(H264TestSupport.ReadAccessUnits(name), nal => EditRbsp(nal, edit), firstOnly: false));

    /// <summary>Returns whether a stream with an RBSP edit on every slice decodes to the reference; any other outcome than a rejection fails.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="edit">The edit.</param>
    /// <returns>Whether it decoded; false when it was rejected as invalid data.</returns>
    private static bool Decodes(string name, Func<byte[], byte[]> edit)
    {
        try
        {
            AssertDecodesIdentically(name, edit);
            return true;
        }
        catch (CodecException e) when (e.Kind == CodecErrorKind.InvalidData)
        {
            return false;
        }
    }

    /// <summary>Asserts that access units decode to the fixture's reference.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="units">The access units.</param>
    private static void AssertDecodes(string name, List<byte[]> units)
    {
        using var decoder = H264TestSupport.CreateAnnexB();
        var result = new List<DecodedFrameInfo>();
        var frames = new List<VideoFrame>();
        for (var i = 0; i < units.Count; i++)
        {
            decoder.Decode(new EncodedPacket(units[i], i, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken);
            H264TestSupport.Collect(frames, result);
        }

        decoder.Drain(frames);
        H264TestSupport.Collect(frames, result);
        Assert.Equal(H264TestSupport.ReadReference(name), result.Select(static f => f.Md5));
    }

    /// <summary>Applies an RBSP edit to the first slice of the first access unit and asserts that it is rejected as invalid data.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="edit">The edit.</param>
    private static void AssertRejected(string name, Func<byte[], byte[]> edit)
    {
        var units = Rewrite(H264TestSupport.ReadAccessUnits(name), nal => EditRbsp(nal, edit), firstOnly: true);
        using var decoder = H264TestSupport.CreateAnnexB();
        var frames = new List<VideoFrame>();
        var e = Assert.Throws<CodecException>(() => decoder.Decode(new EncodedPacket(units[0], 0, H264TestSupport.TimeBase), frames, TestContext.Current.CancellationToken));
        Assert.Equal(CodecErrorKind.InvalidData, e.Kind);
        frames.ForEach(static f => f.Dispose());
    }

    /// <summary>Rewrites slice NAL units.</summary>
    /// <param name="units">Annex B access units.</param>
    /// <param name="rewrite">The NAL unit rewrite.</param>
    /// <param name="firstOnly">Whether only the first slice of the first access unit is rewritten.</param>
    /// <returns>The rewritten access units.</returns>
    private static List<byte[]> Rewrite(List<byte[]> units, Func<byte[], byte[]> rewrite, bool firstOnly)
    {
        var done = false;
        var result = new List<byte[]>();
        foreach (var unit in units)
        {
            var nals = H264TestSupport.SplitNalUnits(unit);
            for (var k = 0; k < nals.Count; k++)
            {
                if ((nals[k][0] & 0x1F) is 1 or 5 && !(firstOnly && done))
                {
                    nals[k] = rewrite(nals[k]);
                    done = true;
                }
            }

            result.Add(H264TestSupport.Join(nals));
        }

        return result;
    }

    /// <summary>Unescapes a NAL unit, edits its RBSP and escapes it again.</summary>
    /// <param name="nal">The NAL unit.</param>
    /// <param name="edit">The edit.</param>
    /// <returns>The new NAL unit.</returns>
    private static byte[] EditRbsp(byte[] nal, Func<byte[], byte[]> edit)
    {
        var rbsp = new byte[nal.Length];
        var length = H264NalUnits.Unescape(nal.AsSpan(1), rbsp);
        return H264ParameterSetWriter.Nal((nal[0] >> 5) & 3, nal[0] & 0x1F, edit(rbsp[..length]));
    }

    /// <summary>Keeps the RBSP through its stop bit, appends tail bits (the last must be 1, the new stop bit) and zero-aligns.</summary>
    /// <param name="rbsp">The RBSP.</param>
    /// <param name="tail">The appended bits.</param>
    /// <returns>The new RBSP, whose stop bit is <paramref name="tail"/>.Length bits later.</returns>
    private static byte[] Pad(byte[] rbsp, bool[] tail)
    {
        Assert.True(tail[^1]);
        var stop = H264NalUnits.PayloadBits(rbsp);
        using var w = new H264BitWriter(rbsp.Length + 8, rbsp.Length + 8);
        for (var b = 0; b <= stop; b++)
        {
            w.WriteBit((rbsp[b >> 3] & (0x80 >> (b & 7))) != 0);
        }

        foreach (var bit in tail)
        {
            w.WriteBit(bit);
        }

        w.AlignZero();
        return w.Written.ToArray();
    }

    /// <summary>Returns a tail of zero bits ending in the new stop bit.</summary>
    /// <param name="count">The tail length.</param>
    /// <returns>The bits.</returns>
    private static bool[] Zeros(int count)
    {
        var bits = new bool[count];
        bits[^1] = true;
        return bits;
    }
}
