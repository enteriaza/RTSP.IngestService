// Hand-built AAC-LC access units for coding paths the encoder fixtures do not reach, written bit by bit with
// BitstreamWriter from the ISO/IEC 14496-3 syntax. Expected output is computed independently, with the IMDCT
// definition evaluated directly rather than through the FFT-based transform under test.

using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Media.Codecs.Common.Bitstreams;

namespace RTSP.IngestService.Tests.Media.Codecs.Aac;

/// <summary>Synthetic AAC-LC bitstream tests.</summary>
public sealed class AacSyntheticStreamTests
{
    /// <summary>AudioSpecificConfig: AAC-LC, 48 kHz, mono.</summary>
    private static readonly byte[] MonoConfig = [0x11, 0x88];

    /// <summary>A pulse on an all-zero band yields one coefficient of -amplitude^(4/3), synthesised with the sine window.</summary>
    /// <param name="offset">The pulse offset within band 0.</param>
    /// <param name="amplitude">The pulse amplitude.</param>
    /// <param name="globalGain">The global gain (scalefactor of band 0).</param>
    [Theory]
    [InlineData(2, 5, 100)]
    [InlineData(0, 15, 120)]
    [InlineData(3, 1, 80)]
    public void PulseDataModifiesTheQuantisedSpectrum(int offset, int amplitude, int globalGain)
    {
        var unit = MonoUnit(globalGain, pulse: (offset, amplitude));
        using var decoder = AacDecoder.Create(MonoConfig);
        using var frame = decoder.Decode(new EncodedPacket(unit, 0, MediaTimeBase.Milliseconds));

        var coefficient = -Math.Pow(amplitude, 4.0 / 3.0) * Math.Pow(2, 0.25 * (globalGain - 100));
        var output = frame.GetChannel(0);
        for (var n = 0; n < 1024; n++)
        {
            var x = 2.0 / 2048 * coefficient * Math.Cos(2 * Math.PI / 2048 * (n + 512.5) * (offset + 0.5));
            var expected = x * Math.Sin(Math.PI / 2048 * (n + 0.5)) / 32768;
            Assert.Equal(expected, output[n], 1e-6 * Math.Max(1, Math.Abs(coefficient) / 1000));
        }

        Assert.Equal(1, decoder.Usage.PulseChannels);
    }

    /// <summary>An all-zero spectrum decodes to silence; the second frame carries the first frame's overlap.</summary>
    [Fact]
    public void SilentFramesDecodeToSilence()
    {
        using var decoder = AacDecoder.Create(MonoConfig);
        using var frame = decoder.Decode(new EncodedPacket(MonoUnit(100, pulse: null), 0, MediaTimeBase.Milliseconds));
        Assert.All(frame.GetChannel(0).ToArray(), static sample => Assert.Equal(0f, sample));
    }

    /// <summary>A pulse with short windows, a reserved codebook, max_sfb overflow and gain control are invalid.</summary>
    /// <param name="defect">The defect.</param>
    [Theory]
    [InlineData("reserved-codebook")]
    [InlineData("max-sfb")]
    [InlineData("gain-control")]
    [InlineData("prediction")]
    [InlineData("coupling")]
    [InlineData("missing-end")]
    public void RejectsInvalidSyntax(string defect)
    {
        var buffer = new byte[64];
        var writer = new BitstreamWriter(buffer);
        if (defect == "coupling")
        {
            writer.Write(2, 3);
        }
        else
        {
            writer.Write(0, 3);
            writer.Write(0, 4);
            writer.Write(100, 8);
            writer.Write(0, 1);
            writer.Write(0, 2);
            writer.Write(0, 1);
            writer.Write(defect == "max-sfb" ? 50u : 1u, 6);
            writer.WriteBit(defect == "prediction");
            writer.Write(defect == "reserved-codebook" ? 12u : 0u, 4);
            writer.Write(1, 5);
            writer.WriteBit(false);
            writer.WriteBit(false);
            writer.WriteBit(defect == "gain-control");
            if (defect != "missing-end")
            {
                writer.Write(7, 3);
            }
        }

        writer.AlignToByte();
        var length = writer.BytesWritten;
        using var decoder = AacDecoder.Create(MonoConfig);
        var error = Assert.Throws<CodecException>(() => decoder.Decode(new EncodedPacket(buffer.AsMemory(0, length), 0, MediaTimeBase.Milliseconds)).Dispose());
        Assert.Equal(defect == "coupling" ? CodecErrorKind.Unsupported : CodecErrorKind.InvalidData, error.Kind);
    }

    /// <summary>A fill element carrying SBR data (implicit HE-AAC) is rejected rather than decoded as half-band LC.</summary>
    [Fact]
    public void RejectsImplicitSbr()
    {
        var buffer = new byte[64];
        var writer = new BitstreamWriter(buffer);
        writer.Write(6, 3);
        writer.Write(2, 4);
        writer.Write(13, 4);
        writer.Write(0, 12);
        writer.Write(7, 3);
        writer.AlignToByte();
        var length = writer.BytesWritten;
        using var decoder = AacDecoder.Create(MonoConfig);
        var error = Assert.Throws<CodecException>(() => decoder.Decode(new EncodedPacket(buffer.AsMemory(0, length), 0, MediaTimeBase.Milliseconds)).Dispose());
        Assert.Equal(CodecErrorKind.Unsupported, error.Kind);
        Assert.Contains("SBR", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Data stream, other fill and program config elements are skipped.</summary>
    [Fact]
    public void SkipsAncillaryElements()
    {
        var body = MonoUnit(100, pulse: null);
        var buffer = new byte[128];
        var writer = new BitstreamWriter(buffer);
        writer.Write(4, 3);
        writer.Write(0, 4);
        writer.WriteBit(true);
        writer.Write(2, 8);
        writer.AlignToByte();
        writer.Write(0xABCD, 16);
        writer.Write(6, 3);
        writer.Write(1, 4);
        writer.Write(0, 8);
        writer.Write(5, 3);
        writer.Write(0, 4 + 2 + 4);
        writer.Write(1, 4);
        writer.Write(0, 4 + 4 + 2 + 3 + 4);
        writer.Write(0, 3);
        writer.Write(0, 5);
        writer.AlignToByte();
        writer.Write(1, 8);
        writer.Write(0x41, 8);
        var reader = new BitstreamReader(body);
        while (reader.Remaining > 0)
        {
            var bits = Math.Min(reader.Remaining, 8);
            writer.Write(reader.Read(bits), bits);
        }

        using var decoder = AacDecoder.Create(MonoConfig);
        using var frame = decoder.Decode(new EncodedPacket(buffer.AsMemory(0, writer.BytesWritten), 0, MediaTimeBase.Milliseconds));
        Assert.Equal(1024, frame.SampleCount);
    }

    /// <summary>Builds a mono access unit: one long sine-window SCE, band 0 coded with codebook 1 as zeros.</summary>
    /// <param name="globalGain">The global gain.</param>
    /// <param name="pulse">An optional pulse (offset in band 0, amplitude).</param>
    /// <returns>The access unit.</returns>
    private static byte[] MonoUnit(int globalGain, (int Offset, int Amplitude)? pulse)
    {
        var buffer = new byte[64];
        var writer = new BitstreamWriter(buffer);
        writer.Write(0, 3);
        writer.Write(0, 4);
        writer.Write((uint)globalGain, 8);
        writer.Write(0, 1);
        writer.Write(0, 2);
        writer.Write(0, 1);
        writer.Write(1, 6);
        writer.WriteBit(false);
        writer.Write(1, 4);
        writer.Write(1, 5);
        writer.Write(AacHuffmanData.ScaleFactorCodes[60], AacHuffmanData.ScaleFactorLengths[60]);
        writer.WriteBit(pulse is not null);
        if (pulse is { } value)
        {
            writer.Write(0, 2);
            writer.Write(0, 6);
            writer.Write((uint)value.Offset, 5);
            writer.Write((uint)value.Amplitude, 4);
        }

        writer.WriteBit(false);
        writer.WriteBit(false);
        writer.Write(AacHuffmanData.Book1Codes[40], AacHuffmanData.Book1Lengths[40]);
        writer.Write(7, 3);
        writer.AlignToByte();
        return buffer.AsSpan(0, writer.BytesWritten).ToArray();
    }
}
