// Tests of the shared managed-codec primitives: bit reader and writer, Exp-Golomb coding, the prefix-code decoder
// (including the AAC codebooks: symbol counts, prefix-freedom and completeness), the FFT and IMDCT against their
// direct definitions, time-base rescaling and audio frame ownership.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;
using RTSP.IngestService.Media.Codecs.Common.Entropy;
using RTSP.IngestService.Media.Codecs.Common.Transforms;

namespace RTSP.IngestService.Tests.Media.Codecs;

/// <summary>Managed codec primitive tests.</summary>
public sealed class CodecPrimitiveTests
{
    /// <summary>Bits round-trip through the writer and reader at every width and alignment.</summary>
    [Fact]
    public void BitsRoundTrip()
    {
        var random = new Random(7);
        var fields = Enumerable.Range(0, 500).Select(_ => random.Next(0, 33)).Select(width => (width, value: width == 0 ? 0u : (uint)random.NextInt64(0, 1L << width))).ToArray();
        var buffer = new byte[4096];
        var writer = new BitstreamWriter(buffer);
        foreach (var (width, value) in fields)
        {
            writer.Write(value, width);
        }

        var reader = new BitstreamReader(buffer.AsSpan(0, writer.BytesWritten));
        foreach (var (width, value) in fields)
        {
            Assert.Equal(value, reader.Peek(width));
            Assert.Equal(value, reader.Read(width));
        }

        Assert.Equal(writer.Position, reader.Position);
    }

    /// <summary>Reads are most-significant-bit first across byte boundaries.</summary>
    [Fact]
    public void ReadsMostSignificantBitFirst()
    {
        var reader = new BitstreamReader([0b1010_0000, 0xFF, 0x01]);
        Assert.True(reader.ReadBit());
        Assert.Equal(0b01u, reader.Read(2));
        Assert.Equal(0b0_0000_1111u, reader.Read(9));
        reader.AlignToByte();
        Assert.Equal(16, reader.Position);
        Assert.Equal(0x01u, reader.Read(8));
        Assert.Equal(0, reader.Remaining);
    }

    /// <summary>Peeking past the end pads with zeros; consuming past the end is a truncation error.</summary>
    [Fact]
    public void ConsumingPastTheEndFails()
    {
        var reader = new BitstreamReader([0xFF]);
        Assert.Equal(0xFF000000u, reader.Peek(32));
        reader.Skip(4);
        var error = Assert.Throws<CodecException>(() => Read(new BitstreamReader([0xFF]), 9));
        Assert.Equal(CodecErrorKind.InvalidData, error.Kind);
        Assert.Equal(4, reader.Remaining);

        static uint Read(BitstreamReader reader, int count) => reader.Read(count);
    }

    /// <summary>Exp-Golomb codes match the ITU-T H.264 9.1 table and round-trip, including the extremes.</summary>
    [Fact]
    public void ExpGolombMatchesTheStandard()
    {
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, ReadAll("1 010 011 00100 00101 00110 00111 0001000 0001001", static (ref r) => r.ReadExpGolomb()));
        Assert.Equal(new[] { 0, 1, -1, 2, -2, 3, -3 }, ReadAll("1 010 011 00100 00101 00110 00111", static (ref r) => r.ReadSignedExpGolomb()));

        uint[] values = [0, 1, 254, 255, 65535, 1u << 31, uint.MaxValue - 1];
        int[] signed = [0, 1, -1, 1000, -1000, int.MaxValue, int.MinValue + 1];
        var buffer = new byte[64];
        var writer = new BitstreamWriter(buffer);
        foreach (var value in values)
        {
            writer.WriteExpGolomb(value);
        }

        foreach (var value in signed)
        {
            writer.WriteSignedExpGolomb(value);
        }

        var reader = new BitstreamReader(buffer.AsSpan(0, writer.BytesWritten));
        foreach (var value in values)
        {
            Assert.Equal(value, reader.ReadExpGolomb());
        }

        foreach (var value in signed)
        {
            Assert.Equal(value, reader.ReadSignedExpGolomb());
        }
    }

    /// <summary>More than 31 leading zeros, or truncation, is invalid.</summary>
    [Fact]
    public void ExpGolombRejectsOverlongCodes()
    {
        Assert.Equal(CodecErrorKind.InvalidData, Assert.Throws<CodecException>(() => ReadCode(new byte[8])).Kind);
        Assert.Equal(CodecErrorKind.InvalidData, Assert.Throws<CodecException>(() => ReadCode([0x00, 0x01])).Kind);

        static uint ReadCode(byte[] data)
        {
            var reader = new BitstreamReader(data);
            return reader.ReadExpGolomb();
        }
    }

    /// <summary>The writer refuses to overrun its buffer and values wider than the field.</summary>
    [Fact]
    public void WriterEnforcesLimits()
    {
        var error = Assert.Throws<CodecException>(() =>
        {
            var writer = new BitstreamWriter(new byte[1]);
            writer.Write(0, 9);
        });
        Assert.Equal(CodecErrorKind.ResourceLimit, error.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var writer = new BitstreamWriter(new byte[4]);
            writer.Write(4, 2);
        });
    }

    /// <summary>A prefix code decodes short and long (second-level) codewords and rejects unused patterns.</summary>
    [Fact]
    public void PrefixCodeDecodes()
    {
        uint[] codes = [0b0, 0b10, 0b110, 0b1110_0000_0000, 0b1110_0000_0001];
        byte[] lengths = [1, 2, 3, 12, 12];
        var decoder = new PrefixCodeDecoder(codes, lengths, rootBits: 4);
        var reader = new BitstreamReader([0b0101_1011, 0b1000_0000, 0b0011_1000, 0b0000_0100]);
        Assert.Equal([0, 1, 2, 3, 4], new[] { decoder.Decode(ref reader), decoder.Decode(ref reader), decoder.Decode(ref reader), decoder.Decode(ref reader), decoder.Decode(ref reader) });

        Assert.Equal(CodecErrorKind.InvalidData, Assert.Throws<CodecException>(() => Decode(decoder, [0xFF, 0xFF])).Kind);
        Assert.Equal(CodecErrorKind.InvalidData, Assert.Throws<CodecException>(() => Decode(decoder, [0xE0])).Kind);

        static int Decode(PrefixCodeDecoder decoder, byte[] data)
        {
            var reader = new BitstreamReader(data);
            return decoder.Decode(ref reader);
        }
    }

    /// <summary>Codes that are not prefix-free, or malformed, are rejected at construction.</summary>
    [Fact]
    public void PrefixCodeRejectsInvalidCodes()
    {
        Assert.Throws<ArgumentException>(() => new PrefixCodeDecoder([0b0, 0b01], [1, 2]));
        Assert.Throws<ArgumentException>(() => new PrefixCodeDecoder([0b1110_0000_0000, 0b111], [12, 3], rootBits: 4));
        Assert.Throws<ArgumentException>(() => new PrefixCodeDecoder([0b100], [2]));
        Assert.Throws<ArgumentException>(() => new PrefixCodeDecoder([0], [0]));
    }

    /// <summary>The AAC codebooks have the specified sizes and are prefix-free and complete (Kraft sum exactly 1).</summary>
    [Fact]
    public void AacCodebooksAreCompletePrefixCodes()
    {
        int[] sizes = [0, 81, 81, 81, 81, 81, 81, 64, 64, 169, 169, 289];
        for (var book = 1; book <= 11; book++)
        {
            Assert.Equal(sizes[book], AacTables.SpectralCodebooks[book]!.SymbolCount);
        }

        Assert.Equal(121, AacTables.ScaleFactorCodebook.SymbolCount);
        Assert.Equal(1.0, Kraft(AacHuffmanData.ScaleFactorLengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book1Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book2Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book3Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book4Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book5Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book6Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book7Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book8Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book9Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book10Lengths));
        Assert.Equal(1.0, Kraft(AacHuffmanData.Book11Lengths));
        Assert.Equal(1u, AacHuffmanData.ScaleFactorLengths[60]);

        static double Kraft(ReadOnlySpan<byte> lengths)
        {
            var sum = 0.0;
            foreach (var length in lengths)
            {
                sum += Math.Pow(2, -length);
            }

            return sum;
        }
    }

    /// <summary>The FFT matches the direct DFT at several sizes.</summary>
    /// <param name="size">The size.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(512)]
    public void FftMatchesDirectDft(int size)
    {
        var random = new Random(size);
        var real = Enumerable.Range(0, size).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var imaginary = Enumerable.Range(0, size).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var expectedReal = new double[size];
        var expectedImaginary = new double[size];
        for (var k = 0; k < size; k++)
        {
            for (var n = 0; n < size; n++)
            {
                var angle = -2 * Math.PI * n * k / size;
                expectedReal[k] += (real[n] * Math.Cos(angle)) - (imaginary[n] * Math.Sin(angle));
                expectedImaginary[k] += (real[n] * Math.Sin(angle)) + (imaginary[n] * Math.Cos(angle));
            }
        }

        new ComplexFft(size).Forward(real, imaginary);
        for (var k = 0; k < size; k++)
        {
            Assert.Equal(expectedReal[k], real[k], 1e-4);
            Assert.Equal(expectedImaginary[k], imaginary[k], 1e-4);
        }
    }

    /// <summary>The FFT rejects sizes that are not powers of two.</summary>
    /// <param name="size">The size.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    public void FftRejectsInvalidSizes(int size) => Assert.Throws<ArgumentOutOfRangeException>(() => new ComplexFft(size));

    /// <summary>The IMDCT matches its definition (ISO/IEC 14496-3, 4.6.11.3.1) at the AAC sizes.</summary>
    /// <param name="length">The output length N.</param>
    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(2048)]
    public void InverseMdctMatchesDefinition(int length)
    {
        var random = new Random(length);
        var coefficients = Enumerable.Range(0, length / 2).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var output = new float[length];
        new InverseMdct(length, 2.0 / length).Transform(coefficients, output);
        var n0 = ((length / 2.0) + 1) / 2;
        for (var n = 0; n < length; n++)
        {
            var expected = 0.0;
            for (var k = 0; k < length / 2; k++)
            {
                expected += coefficients[k] * Math.Cos(2 * Math.PI / length * (n + n0) * (k + 0.5));
            }

            Assert.Equal(2.0 / length * expected, output[n], 1e-5);
        }
    }

    /// <summary>MDCT then IMDCT with a sine window and overlap-add reconstructs the signal (time-domain aliasing cancellation).</summary>
    [Fact]
    public void InverseMdctCancelsAliasingWithOverlapAdd()
    {
        const int N = 256;
        var random = new Random(3);
        var signal = Enumerable.Range(0, N * 4).Select(_ => random.NextDouble() - 0.5).ToArray();
        var window = Enumerable.Range(0, N).Select(n => Math.Sin(Math.PI / N * (n + 0.5))).ToArray();
        var imdct = new InverseMdct(N, 2.0 / N);
        var reconstructed = new double[signal.Length + N];
        var block = new float[N];
        var spectrum = new float[N / 2];
        var n0 = ((N / 2.0) + 1) / 2;
        for (var start = 0; start + N <= signal.Length; start += N / 2)
        {
            for (var k = 0; k < N / 2; k++)
            {
                var sum = 0.0;
                for (var n = 0; n < N; n++)
                {
                    sum += signal[start + n] * window[n] * Math.Cos(2 * Math.PI / N * (n + n0) * (k + 0.5));
                }

                spectrum[k] = (float)(2 * sum);
            }

            imdct.Transform(spectrum, block);
            for (var n = 0; n < N; n++)
            {
                reconstructed[start + n] += block[n] * window[n];
            }
        }

        for (var n = N / 2; n < signal.Length - N; n++)
        {
            Assert.Equal(signal[n], reconstructed[n], 1e-4);
        }
    }

    /// <summary>Rescaling rounds to nearest with halves away from zero, and overflow is detected.</summary>
    [Fact]
    public void TimeBaseRescales()
    {
        var samples48k = new MediaTimeBase(1, 48000);
        Assert.Equal(21, samples48k.Rescale(1024, MediaTimeBase.Milliseconds));
        Assert.Equal(48000, MediaTimeBase.Milliseconds.Rescale(1000, samples48k));
        Assert.Equal(2, new MediaTimeBase(1, 4).Rescale(1, new MediaTimeBase(1, 6)));
        Assert.Equal(-2, new MediaTimeBase(1, 4).Rescale(-1, new MediaTimeBase(1, 6)));
        Assert.Equal(long.MaxValue, MediaTimeBase.Milliseconds.Rescale(long.MaxValue, MediaTimeBase.Milliseconds));
        Assert.Throws<OverflowException>(() => MediaTimeBase.Milliseconds.Rescale(long.MaxValue, samples48k));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTimeBase(0, 1));
    }

    /// <summary>A frame owns a zeroed pooled buffer, exposes planes, and is unusable after disposal; disposal is idempotent.</summary>
    [Fact]
    public void AudioFrameOwnsItsBuffer()
    {
        var frame = new AudioFrame(new[] { AudioChannel.FrontLeft, AudioChannel.FrontRight }, 1024, 48000, 10, MediaTimeBase.Milliseconds);
        Assert.Equal(2, frame.ChannelCount);
        Assert.All(frame.GetChannel(1).ToArray(), static sample => Assert.Equal(0f, sample));
        frame.GetChannel(0)[0] = 1;
        Assert.Equal(0f, frame.GetChannel(1)[0]);
        Assert.Equal(21, frame.Duration);
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetChannel(2));
        frame.Dispose();
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() => frame.GetChannel(0));
    }

    /// <summary>Reads every code from a space-separated bit string.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="bits">The bits.</param>
    /// <param name="read">Reads one value.</param>
    /// <returns>The values.</returns>
    private static T[] ReadAll<T>(string bits, ReadFunc<T> read)
    {
        var compact = bits.Replace(" ", string.Empty, StringComparison.Ordinal);
        var buffer = new byte[(compact.Length + 7) / 8];
        var writer = new BitstreamWriter(buffer);
        foreach (var bit in compact)
        {
            writer.WriteBit(bit == '1');
        }

        var reader = new BitstreamReader(buffer);
        var values = new List<T>();
        while (reader.Position < compact.Length)
        {
            values.Add(read(ref reader));
        }

        return [.. values];
    }

    /// <summary>Reads a value from a reader.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="reader">The reader.</param>
    /// <returns>The value.</returns>
    private delegate T ReadFunc<T>(ref BitstreamReader reader);
}
