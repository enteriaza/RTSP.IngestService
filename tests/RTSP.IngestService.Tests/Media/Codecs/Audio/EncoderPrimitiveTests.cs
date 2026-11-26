// Tests for the encoder-side primitives: the DCT-IV and forward MDCT against their definitions, perfect reconstruction
// through the forward and inverse MDCT with the AAC windows, exact agreement between the spectral coder's bit count
// and the bits it writes, codewords that decode back through the decoder's codebooks, and the transient detector.

using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Encoder;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;
using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Tests.Media.Codecs.Audio;

/// <summary>Tests for the encoder primitives.</summary>
public sealed class EncoderPrimitiveTests
{
    /// <summary>The DCT-IV matches its definition.</summary>
    /// <param name="length">The transform length.</param>
    [Theory]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(1024)]
    public void DctIvMatchesDefinition(int length)
    {
        var input = AudioTestSupport.Noise(length, 1, length);
        var output = new float[length];
        new DctIv(length).Transform(input, output, 1f);
        for (var k = 0; k < length; k++)
        {
            var expected = 0.0;
            for (var n = 0; n < length; n++)
            {
                expected += input[n] * Math.Cos(Math.PI / length * (n + 0.5) * (k + 0.5));
            }

            Assert.Equal(expected, output[k], 1e-3 * Math.Sqrt(length));
        }
    }

    /// <summary>The forward MDCT matches X[k] = 2 sum z[n] cos(2 pi / N (n + n0)(k + 1/2)).</summary>
    /// <param name="length">The input length N.</param>
    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(2048)]
    public void ForwardMdctMatchesDefinition(int length)
    {
        var input = AudioTestSupport.Noise(length, 1, length + 1);
        var output = new float[length / 2];
        new ForwardMdct(length, 2.0).Transform(input, output);
        var n0 = ((length / 2.0) + 1) / 2;
        for (var k = 0; k < length / 2; k++)
        {
            var expected = 0.0;
            for (var n = 0; n < length; n++)
            {
                expected += 2 * input[n] * Math.Cos(2 * Math.PI / length * (n + n0) * (k + 0.5));
            }

            Assert.Equal(expected, output[k], 2e-3 * Math.Sqrt(length));
        }
    }

    /// <summary>Forward MDCT, inverse MDCT and overlap-add with the sine window reconstruct the input (TDAC).</summary>
    [Fact]
    public void ForwardAndInverseMdctReconstruct()
    {
        const int n = 2048;
        var window = AacWindows.Long[0];
        var signal = AudioTestSupport.Noise(n * 4, 1, 9);
        var forward = new ForwardMdct(n, 2.0);
        var inverse = new InverseMdct(n, 2.0 / n);
        var output = new float[signal.Length];
        var block = new float[n];
        var coefficients = new float[n / 2];
        var synthesis = new float[n];
        for (var start = 0; start + n <= signal.Length; start += n / 2)
        {
            for (var i = 0; i < n; i++)
            {
                block[i] = signal[start + i] * (i < n / 2 ? window[i] : window[n - 1 - i]);
            }

            forward.Transform(block, coefficients);
            inverse.Transform(coefficients, synthesis);
            for (var i = 0; i < n; i++)
            {
                output[start + i] += synthesis[i] * (i < n / 2 ? window[i] : window[n - 1 - i]);
            }
        }

        for (var i = n / 2; i < signal.Length - (n / 2); i++)
        {
            Assert.Equal(signal[i], output[i], 1e-4f);
        }
    }

    /// <summary>For every codebook the counted bits equal the written bits.</summary>
    /// <param name="book">The codebook.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void CountEqualsWrittenBits(int book)
    {
        var random = new Random(book);
        var largest = AacSpectralCoder.GetLargestMagnitude(book);
        var values = new int[512];
        for (var i = 0; i < values.Length; i++)
        {
            var magnitude = book == 11 && random.Next(4) == 0 ? random.Next(16, 8192) : random.Next(0, Math.Min(largest, 16) + 1);
            values[i] = random.Next(2) == 0 ? magnitude : -magnitude;
        }

        var buffer = new byte[16384];
        var writer = new BitstreamWriter(buffer);
        AacSpectralCoder.Write(ref writer, values, book);
        Assert.Equal(AacSpectralCoder.Count(values, book), writer.Position);
    }

    /// <summary>Signed-codebook codewords decode back to the expected symbols with the decoder's tables.</summary>
    /// <param name="book">A signed codebook.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void CodewordsDecodeWithDecoderTables(int book)
    {
        var random = new Random(book);
        var largest = AacSpectralCoder.GetLargestMagnitude(book);
        var values = Enumerable.Range(0, 256).Select(_ => random.Next(-largest, largest + 1)).ToArray();
        var buffer = new byte[4096];
        var writer = new BitstreamWriter(buffer);
        AacSpectralCoder.Write(ref writer, values, book);
        var reader = new BitstreamReader(buffer.AsSpan(0, writer.BytesWritten));
        var codebook = AacTables.SpectralCodebooks[book]!;
        if (book <= 2)
        {
            for (var i = 0; i < values.Length; i += 4)
            {
                Assert.Equal((27 * (values[i] + 1)) + (9 * (values[i + 1] + 1)) + (3 * (values[i + 2] + 1)) + values[i + 3] + 1, codebook.Decode(ref reader));
            }
        }
        else
        {
            for (var i = 0; i < values.Length; i += 2)
            {
                Assert.Equal((9 * (values[i] + 4)) + values[i + 1] + 4, codebook.Decode(ref reader));
            }
        }
    }

    /// <summary>The transient detector flags a click and a sudden onset but not steady tones or silence.</summary>
    [Fact]
    public void TransientDetectorFindsAttacks()
    {
        var length = AacTransientDetector.RegionLength;
        Assert.False(AacTransientDetector.HasAttack(new float[length]));
        Assert.False(AacTransientDetector.HasAttack(AudioTestSupport.Sine(length, 48000, 1000, 16000)));
        var click = AudioTestSupport.Sine(length, 48000, 1000, 300);
        click[700] = 30000;
        Assert.True(AacTransientDetector.HasAttack(click));
        var onset = new float[length];
        AudioTestSupport.Noise(length / 2, 20000, 3).CopyTo(onset, length / 2);
        Assert.True(AacTransientDetector.HasAttack(onset));
        Assert.False(AacTransientDetector.HasAttack(AudioTestSupport.Noise(length, 20, 4)));
    }
}
