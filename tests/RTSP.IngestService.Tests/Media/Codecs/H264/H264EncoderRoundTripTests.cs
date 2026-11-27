// Round trip of the managed H.264 encoder through the managed decoder. The encoder's reconstruction is, by
// construction, the picture any conforming decoder outputs; the requirement fixed before evaluation is therefore
// bit-exact equality of every plane of every frame, for every pattern, size, slice count and reference count.

using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Encoder reconstruction equals the managed decoder's output.</summary>
public sealed class H264EncoderRoundTripTests
{
    /// <summary>Gets the pattern, size and coding variations.</summary>
    public static TheoryData<SyntheticPattern, int, int, int, int, int> Cases => new()
    {
        { SyntheticPattern.Flat, 64, 48, 26, 1, 1 },
        { SyntheticPattern.Bars, 160, 96, 22, 1, 1 },
        { SyntheticPattern.Ramp, 128, 64, 30, 1, 1 },
        { SyntheticPattern.Gradient, 96, 80, 18, 1, 2 },
        { SyntheticPattern.Detail, 176, 144, 22, 2, 1 },
        { SyntheticPattern.Noise, 64, 64, 12, 1, 1 },
        { SyntheticPattern.Motion, 176, 144, 26, 1, 3 },
        { SyntheticPattern.Motion, 198, 110, 34, 3, 2 },
        { SyntheticPattern.Cuts, 128, 96, 40, 1, 2 },
        { SyntheticPattern.Motion, 50, 34, 51, 1, 1 },
        { SyntheticPattern.Detail, 32, 32, 0, 1, 1 },
    };

    /// <summary>Every decoded frame equals the encoder's reconstruction.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="qp">The QP.</param>
    /// <param name="slices">Slices per picture.</param>
    /// <param name="references">Reference frames.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ManagedDecoderMatchesReconstruction(SyntheticPattern pattern, int width, int height, int qp, int slices, int references)
    {
        var options = H264EncoderTestSupport.Options(width, height, qp) with { SlicesPerPicture = slices, MaxReferenceFrames = references, IntraQpOffset = 0 };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, pattern, 30);
        var decoded = H264EncoderTestSupport.DecodeManaged(encoder.DecoderConfigurationRecord, frames);
        H264EncoderTestSupport.AssertMatchesReconstruction(frames, decoded);
    }

    /// <summary>Annex B output decodes identically and carries parameter sets before every IDR picture.</summary>
    [Fact]
    public void AnnexBMatchesReconstruction()
    {
        var options = H264EncoderTestSupport.Options(176, 144) with { Format = H264OutputFormat.AnnexB, KeyframeInterval = TimeSpan.FromSeconds(1) };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 60);
        Assert.Equal(3, frames.Count(static f => f.IsKeyframe));
        var decoded = H264EncoderTestSupport.DecodeManagedAnnexB(frames);
        H264EncoderTestSupport.AssertMatchesReconstruction(frames, decoded);
        foreach (var frame in frames)
        {
            var types = H264TestSupport.SplitNalUnits(frame.Data).Select(static n => n[0] & 0x1F).ToArray();
            Assert.Equal(frame.IsKeyframe ? [7, 8, 5] : [1], types);
        }
    }
}
