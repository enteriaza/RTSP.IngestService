// The encoder's 4x4 SATD equals half the sum of magnitudes of the 4x4 Hadamard transform, rounded up.

using RTSP.IngestService.Media.Codecs.Video.H264;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>Encoder cost primitives.</summary>
public sealed class H264EncoderTablesTests
{
    /// <summary>Random differences and the extreme sample differences give the transform's SATD.</summary>
    [Fact]
    public void SatdMatchesTheHadamardTransform()
    {
        var random = new Random(23);
        var difference = new int[16];
        var transformed = new int[16];
        for (var trial = 0; trial < 2000; trial++)
        {
            for (var i = 0; i < 16; i++)
            {
                difference[i] = trial switch
                {
                    0 => 255,
                    1 => -255,
                    2 => (i & 1) == 0 ? 255 : -255,
                    _ => random.Next(-255, 256),
                };
            }

            H264Transform.InverseHadamard4x4(difference, transformed);
            var expected = (transformed.Sum(Math.Abs) + 1) >> 1;
            Assert.Equal(expected, H264EncoderTables.Satd4x4(difference));
        }
    }

    /// <summary>The SATD of two sample blocks at their strides is the SATD of their differences.</summary>
    [Fact]
    public void SampleSatdMatchesDifferenceSatd()
    {
        var random = new Random(29);
        var source = new byte[16 * 4];
        var prediction = new byte[8 * 4];
        var difference = new int[16];
        for (var trial = 0; trial < 2000; trial++)
        {
            random.NextBytes(source);
            random.NextBytes(prediction);
            if (trial < 2)
            {
                source.AsSpan().Fill((byte)(trial == 0 ? 255 : 0));
                prediction.AsSpan().Fill((byte)(trial == 0 ? 0 : 255));
            }

            for (var j = 0; j < 4; j++)
            {
                for (var i = 0; i < 4; i++)
                {
                    difference[(j * 4) + i] = source[(j * 16) + i + 3] - prediction[(j * 8) + i + 1];
                }
            }

            Assert.Equal(H264EncoderTables.Satd4x4(difference), H264EncoderTables.Satd4x4(source.AsSpan(3), 16, prediction.AsSpan(1), 8));
        }
    }
}
