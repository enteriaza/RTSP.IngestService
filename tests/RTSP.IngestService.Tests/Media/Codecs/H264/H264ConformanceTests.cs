// Conformance of the managed H.264 decoder against committed fixtures from four independent encoders (libx264,
// Cisco OpenH264, Microsoft Media Foundation, NVIDIA NVENC). Every output frame must be bit-identical to FFmpeg's
// decode of the same stream (the committed .framemd5), in the same order, with presentation timestamps ascending and
// equal to the container's. These tests need no FFmpeg.

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Bit-exact fixture decoding.</summary>
public sealed class H264ConformanceTests
{
    /// <summary>Every frame matches the reference decoder exactly, in presentation order.</summary>
    /// <param name="name">The fixture.</param>
    /// <param name="width">The display width.</param>
    /// <param name="height">The display height.</param>
    /// <param name="frames">The frame count.</param>
    [Theory]
    [MemberData(nameof(H264TestSupport.DecodableFixtures), MemberType = typeof(H264TestSupport))]
    public void DecodesFixtureBitExactInPresentationOrder(string name, int width, int height, int frames)
    {
        var units = H264TestSupport.ReadAccessUnits(name);
        var timestamps = H264TestSupport.ReadTimestamps(name);
        var reference = H264TestSupport.ReadReference(name);
        Assert.Equal(frames, units.Count);
        Assert.Equal(frames, timestamps.Length);
        Assert.Equal(frames, reference.Count);

        using var decoder = H264TestSupport.CreateAnnexB();
        var decoded = H264TestSupport.DecodeAll(decoder, units, timestamps);

        Assert.Equal(frames, decoded.Count);
        for (var i = 0; i < frames; i++)
        {
            Assert.True(reference[i] == decoded[i].Md5, $"Frame {i} of {name} differs from the reference.");
            Assert.Equal(width, decoded[i].Width);
            Assert.Equal(height, decoded[i].Height);
        }

        Assert.Equal(timestamps.Order(), decoded.Select(static f => f.Timestamp));
        Assert.True(decoded[0].IsKeyframe);
        Assert.Equal(0, decoder.OutstandingFrames);
        Assert.Equal(0, decoder.SkippedPictures);
    }
}
