// Tests of the AVCDecoderConfigurationRecord and AudioSpecificConfig parsers: valid records, every NAL length size,
// sampling-frequency and channel-configuration mappings, SBR/PS signalling, and rejection of truncated, reserved
// and inconsistent records without reading out of bounds.

using RTSP.IngestService.Media;

namespace RTSP.IngestService.Tests.Media;

/// <summary>Tests for <see cref="AvcDecoderConfiguration"/>.</summary>
public sealed class AvcDecoderConfigurationTests
{
    /// <summary>A Main-profile SPS NAL unit (type 7).</summary>
    private static readonly byte[] Sps = [0x67, 0x4D, 0x40, 0x0A, 0xEC, 0xA4, 0xBF];

    /// <summary>A PPS NAL unit (type 8).</summary>
    private static readonly byte[] Pps = [0x68, 0xEB, 0xEC, 0xB2];

    /// <summary>Builds a record.</summary>
    /// <param name="lengthSizeMinusOne">The lengthSizeMinusOne field.</param>
    /// <param name="sps">SPS units.</param>
    /// <param name="pps">PPS units.</param>
    /// <returns>The record bytes.</returns>
    internal static byte[] Record(int lengthSizeMinusOne = 3, byte[][]? sps = null, byte[][]? pps = null)
    {
        sps ??= [Sps];
        pps ??= [Pps];
        var bytes = new List<byte> { 1, 0x4D, 0x40, 0x0A, (byte)(0xFC | lengthSizeMinusOne), (byte)(0xE0 | sps.Length) };
        foreach (var unit in sps)
        {
            bytes.AddRange([(byte)(unit.Length >> 8), (byte)unit.Length, .. unit]);
        }

        bytes.Add((byte)pps.Length);
        foreach (var unit in pps)
        {
            bytes.AddRange([(byte)(unit.Length >> 8), (byte)unit.Length, .. unit]);
        }

        return [.. bytes];
    }

    /// <summary>A well-formed record parses into its fields and parameter sets.</summary>
    [Fact]
    public void ValidRecord_Parses()
    {
        var record = Record();

        Assert.True(AvcDecoderConfiguration.TryParse(record, out var config, out var error), error);
        Assert.Equal(0x4D, config.Profile);
        Assert.Equal(0x40, config.ProfileCompatibility);
        Assert.Equal(0x0A, config.Level);
        Assert.Equal(4, config.NalLengthSize);
        Assert.Equal("avc1.4D400A", config.CodecString);
        Assert.Equal(Sps, Assert.Single(config.SequenceParameterSets).ToArray());
        Assert.Equal(Pps, Assert.Single(config.PictureParameterSets).ToArray());
        Assert.Equal(record, config.Record.ToArray());
        Assert.True(config.HasRecord(record));
    }

    /// <summary>The record is copied, so later changes to the source do not affect it.</summary>
    [Fact]
    public void Record_IsCopied()
    {
        var record = Record();
        Assert.True(AvcDecoderConfiguration.TryParse(record, out var config, out _));

        record[^1] ^= 0xFF;

        Assert.False(config.HasRecord(record));
        Assert.Equal(Pps, config.PictureParameterSets[0].ToArray());
    }

    /// <summary>NAL length sizes 1, 2 and 4 are accepted.</summary>
    /// <param name="lengthSizeMinusOne">The field value.</param>
    /// <param name="expected">The NAL length size.</param>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public void NalLengthSize_IsRead(int lengthSizeMinusOne, int expected)
    {
        Assert.True(AvcDecoderConfiguration.TryParse(Record(lengthSizeMinusOne), out var config, out _));
        Assert.Equal(expected, config.NalLengthSize);
    }

    /// <summary>A 3-byte NAL length is not allowed by ISO/IEC 14496-15.</summary>
    [Fact]
    public void NalLengthSizeThree_IsRejected()
    {
        Assert.False(AvcDecoderConfiguration.TryParse(Record(2), out _, out var error));
        Assert.Contains("3 bytes", error, StringComparison.Ordinal);
    }

    /// <summary>Multiple SPS and PPS are kept in order.</summary>
    [Fact]
    public void MultipleParameterSets_AreKept()
    {
        byte[] sps2 = [0x67, 0x01];
        byte[] pps2 = [0x68, 0x02];

        Assert.True(AvcDecoderConfiguration.TryParse(Record(sps: [Sps, sps2], pps: [Pps, pps2]), out var config, out _));

        Assert.Equal([Sps, sps2], config.SequenceParameterSets.Select(static s => s.ToArray()));
        Assert.Equal([Pps, pps2], config.PictureParameterSets.Select(static s => s.ToArray()));
    }

    /// <summary>Every truncation of a valid record is rejected.</summary>
    [Fact]
    public void EveryTruncation_IsRejected()
    {
        var record = Record();
        for (var length = 0; length < record.Length; length++)
        {
            Assert.False(AvcDecoderConfiguration.TryParse(record.AsSpan(0, length), out _, out var error), $"length {length}");
            Assert.NotEmpty(error);
        }
    }

    /// <summary>Records without an SPS or a PPS are rejected.</summary>
    [Fact]
    public void MissingParameterSets_AreRejected()
    {
        Assert.False(AvcDecoderConfiguration.TryParse(Record(sps: []), out _, out var spsError));
        Assert.Contains("no sequence", spsError, StringComparison.Ordinal);
        Assert.False(AvcDecoderConfiguration.TryParse(Record(pps: []), out _, out var ppsError));
        Assert.Contains("no picture", ppsError, StringComparison.Ordinal);
    }

    /// <summary>Parameter sets with the wrong NAL type, a forbidden bit or zero length are rejected.</summary>
    [Fact]
    public void InvalidParameterSets_AreRejected()
    {
        Assert.False(AvcDecoderConfiguration.TryParse(Record(sps: [Pps]), out _, out _));
        Assert.False(AvcDecoderConfiguration.TryParse(Record(pps: [Sps]), out _, out _));
        Assert.False(AvcDecoderConfiguration.TryParse(Record(sps: [[0xE7, 0x00]]), out _, out _));
        Assert.False(AvcDecoderConfiguration.TryParse(Record(sps: [[]]), out _, out _));
    }

    /// <summary>A parameter-set length beyond the record is rejected.</summary>
    [Fact]
    public void ParameterSetLengthBeyondRecord_IsRejected()
    {
        var record = Record();
        record[7] = 0xFF;

        Assert.False(AvcDecoderConfiguration.TryParse(record, out _, out var error));
        Assert.Contains("remain", error, StringComparison.Ordinal);
    }

    /// <summary>Unknown versions and oversized records are rejected.</summary>
    [Fact]
    public void VersionAndSize_AreChecked()
    {
        var record = Record();
        record[0] = 2;
        Assert.False(AvcDecoderConfiguration.TryParse(record, out _, out _));
        Assert.False(AvcDecoderConfiguration.TryParse(new byte[AvcDecoderConfiguration.MaxRecordSize + 1], out _, out _));
    }
}

/// <summary>Tests for <see cref="AacAudioConfiguration"/>.</summary>
public sealed class AacAudioConfigurationTests
{
    /// <summary>Builds an AudioSpecificConfig from bit fields.</summary>
    /// <param name="fields">(value, bit count) pairs, most significant first.</param>
    /// <returns>The bytes, zero-padded.</returns>
    internal static byte[] Bits(params (int Value, int Count)[] fields)
    {
        var bits = new List<bool>();
        foreach (var (value, count) in fields)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    /// <summary>The FFmpeg AAC-LC stereo 44.1 kHz config parses.</summary>
    [Fact]
    public void LcStereo_Parses()
    {
        byte[] record = [0x12, 0x10];

        Assert.True(AacAudioConfiguration.TryParse(record, out var config, out var error), error);
        Assert.Equal(2, config.AudioObjectType);
        Assert.Equal(4, config.SamplingFrequencyIndex);
        Assert.Equal(44100, config.SampleRate);
        Assert.Equal(2, config.ChannelConfiguration);
        Assert.Equal(2, config.ChannelCount);
        Assert.Null(config.ExtensionAudioObjectType);
        Assert.Equal("mp4a.40.2", config.CodecString);
        Assert.True(config.HasRecord(record));
    }

    /// <summary>Every defined sampling-frequency index maps to its rate.</summary>
    /// <param name="index">The index.</param>
    /// <param name="rate">The rate in hertz.</param>
    [Theory]
    [InlineData(0, 96000)]
    [InlineData(3, 48000)]
    [InlineData(4, 44100)]
    [InlineData(8, 16000)]
    [InlineData(11, 8000)]
    [InlineData(12, 7350)]
    public void SamplingFrequencyIndex_MapsToRate(int index, int rate)
    {
        Assert.True(AacAudioConfiguration.TryParse(Bits((2, 5), (index, 4), (1, 4)), out var config, out _));
        Assert.Equal(rate, config.SampleRate);
    }

    /// <summary>Reserved frequency indices 13 and 14 are rejected.</summary>
    /// <param name="index">The index.</param>
    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    public void ReservedFrequencyIndex_IsRejected(int index)
    {
        Assert.False(AacAudioConfiguration.TryParse(Bits((2, 5), (index, 4), (2, 4)), out _, out var error));
        Assert.Contains("reserved sampling frequency", error, StringComparison.Ordinal);
    }

    /// <summary>An explicit 24-bit frequency (index 15) is read; zero is rejected.</summary>
    [Fact]
    public void ExplicitFrequency_IsRead()
    {
        Assert.True(AacAudioConfiguration.TryParse(Bits((2, 5), (15, 4), (22050, 24), (1, 4)), out var config, out _));
        Assert.Equal(15, config.SamplingFrequencyIndex);
        Assert.Equal(22050, config.SampleRate);
        Assert.Equal(1, config.ChannelConfiguration);

        Assert.False(AacAudioConfiguration.TryParse(Bits((2, 5), (15, 4), (0, 24), (1, 4)), out _, out _));
    }

    /// <summary>Channel configurations map to channel counts; 0 (defined in the PCE) has no derivable count.</summary>
    /// <param name="configuration">The channel configuration.</param>
    /// <param name="count">The expected count, or -1 for none.</param>
    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 6)]
    [InlineData(7, 8)]
    [InlineData(11, 7)]
    [InlineData(12, 8)]
    [InlineData(13, 24)]
    [InlineData(14, 8)]
    public void ChannelConfiguration_MapsToCount(int configuration, int count)
    {
        Assert.True(AacAudioConfiguration.TryParse(Bits((2, 5), (4, 4), (configuration, 4)), out var config, out _));
        Assert.Equal(configuration, config.ChannelConfiguration);
        Assert.Equal(count < 0 ? null : count, config.ChannelCount);
    }

    /// <summary>Reserved channel configurations are rejected.</summary>
    /// <param name="configuration">The channel configuration.</param>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(15)]
    public void ReservedChannelConfiguration_IsRejected(int configuration)
    {
        Assert.False(AacAudioConfiguration.TryParse(Bits((2, 5), (4, 4), (configuration, 4)), out _, out _));
    }

    /// <summary>Explicit HE-AAC (SBR) signalling reports the core and extension types and rates.</summary>
    [Fact]
    public void ExplicitSbr_IsParsed()
    {
        var record = Bits((5, 5), (7, 4), (2, 4), (3, 4), (2, 5));

        Assert.True(AacAudioConfiguration.TryParse(record, out var config, out var error), error);
        Assert.Equal(2, config.AudioObjectType);
        Assert.Equal(22050, config.SampleRate);
        Assert.Equal(5, config.ExtensionAudioObjectType);
        Assert.Equal(48000, config.ExtensionSampleRate);
        Assert.Equal("mp4a.40.5", config.CodecString);
    }

    /// <summary>Escaped object types (31 + 6 bits) are decoded; non-AAC object types are rejected.</summary>
    [Fact]
    public void ObjectTypes_AreValidated()
    {
        Assert.False(AacAudioConfiguration.TryParse(Bits((0, 5), (4, 4), (2, 4)), out _, out _));
        Assert.False(AacAudioConfiguration.TryParse(Bits((7, 5), (4, 4), (2, 4)), out _, out _));
        Assert.True(AacAudioConfiguration.TryParse(Bits((31, 5), (7, 6), (4, 4), (2, 4)), out var config, out _));
        Assert.Equal(39, config.AudioObjectType);
        Assert.False(AacAudioConfiguration.TryParse(Bits((31, 5), (0, 6), (4, 4), (2, 4)), out _, out _));
    }

    /// <summary>Truncated and oversized configs are rejected.</summary>
    [Fact]
    public void TruncatedAndOversized_AreRejected()
    {
        Assert.False(AacAudioConfiguration.TryParse([], out _, out _));
        Assert.False(AacAudioConfiguration.TryParse([0x12], out _, out _));
        Assert.False(AacAudioConfiguration.TryParse([0xF8, 0x00], out _, out var escaped));
        Assert.NotEmpty(escaped);
        Assert.False(AacAudioConfiguration.TryParse(Bits((2, 5), (15, 4), (1, 7)), out _, out _));
        Assert.False(AacAudioConfiguration.TryParse(new byte[AacAudioConfiguration.MaxRecordSize + 1], out _, out _));
    }
}
