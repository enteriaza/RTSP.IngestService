// The encoder's precomputed half-sample planes predict exactly what the reference interpolation predicts, and only
// for the reference they were computed from.

using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Half-sample planes for motion-search cost evaluation.</summary>
public sealed class H264HalfSamplePlanesTests
{
    /// <summary>
    /// Every fractional class at every block size equals <see cref="H264InterPrediction.Luma"/> wherever the planes
    /// predict, and the planes predict exactly the blocks whose samples and right and lower neighbours are inside.
    /// </summary>
    [Fact]
    public void MatchesLumaInterpolationInsideThePicture()
    {
        using var picture = new H264EncoderPicture(3, 2) { Id = 1 };
        int width = picture.Width, height = picture.Height;
        var random = new Random(17);
        random.NextBytes(picture.Luma.AsSpan(0, width * height));
        var planes = new H264HalfSamplePlanes(width, height);
        planes.Prepare(picture);

        var expected = new byte[256];
        var actual = new byte[256];
        for (var y = -20; y < height + 4; y++)
        {
            for (var x = -20; x < width + 4; x++)
            {
                foreach (var size in new[] { 4, 8, 16 })
                {
                    var inside = x >= 0 && y >= 0 && x + size < width && y + size < height;
                    for (var fy = 0; fy < 4; fy++)
                    {
                        for (var fx = 0; fx < 4; fx++)
                        {
                            Assert.Equal(inside, planes.TryLuma(picture, x, y, fx, fy, size, actual));
                            if (!inside)
                            {
                                continue;
                            }

                            H264InterPrediction.Luma(picture.Luma, width, height, x, y, fx, fy, size, expected);
                            Assert.True(expected.AsSpan(0, size * size).SequenceEqual(actual.AsSpan(0, size * size)), $"({x}, {y}) size {size} frac ({fx}, {fy}) differs.");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Planes of one reference are not used for another, and are recomputed when it is prepared.</summary>
    [Fact]
    public void FollowsThePreparedReference()
    {
        using var first = new H264EncoderPicture(2, 2) { Id = 1 };
        using var second = new H264EncoderPicture(2, 2) { Id = 2 };
        first.Luma.AsSpan(0, first.Width * first.Height).Fill(10);
        second.Luma.AsSpan(0, second.Width * second.Height).Fill(200);
        var planes = new H264HalfSamplePlanes(first.Width, first.Height);
        var output = new byte[64];

        planes.Prepare(first);
        Assert.True(planes.TryLuma(first, 4, 4, 2, 2, 8, output));
        Assert.All(output, static s => Assert.Equal(10, s));
        Assert.False(planes.TryLuma(second, 4, 4, 2, 2, 8, output));

        planes.Prepare(second);
        Assert.False(planes.TryLuma(first, 4, 4, 2, 2, 8, output));
        Assert.True(planes.TryLuma(second, 4, 4, 1, 3, 8, output));
        Assert.All(output, static s => Assert.Equal(200, s));
    }
}
