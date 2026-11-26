// Tests of the codec-aware source parser on legacy FLV and Enhanced RTMP payloads: every supported codec is identified
// from FFmpeg-generated fixtures; unsupported legacy codec IDs, sound formats and FourCCs are rejected rather than
// treated as H.264/AAC; malformed, truncated, unconfigured and oversized packets are dropped with a reason; ModEx,
// multitrack, command frames, colorInfo metadata and the codec, resolution and frame-rate policies are applied.

using System.Buffers;
using System.Text;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for <see cref="SourceMediaParser"/>.</summary>
public sealed class SourceMediaParserTests
{
    /// <summary>Every fixture is identified with its codec and parameters and parses without drops.</summary>
    /// <param name="file">The fixture.</param>
    /// <param name="video">The expected video codec.</param>
    /// <param name="bitDepth">The expected video bit depth, when the configuration record states it.</param>
    /// <param name="audio">The expected audio codec.</param>
    /// <param name="sampleRate">The expected sample rate.</param>
    /// <param name="channels">The expected channel count.</param>
    [Theory]
    [InlineData("h264-aac.flv", SourceVideoCodec.H264, 8, SourceAudioCodec.Aac, 44100, 2)]
    [InlineData("hevc.flv", SourceVideoCodec.Hevc, 8, null, 0, 0)]
    [InlineData("hevc-pq.flv", SourceVideoCodec.Hevc, 10, null, 0, 0)]
    [InlineData("av1.flv", SourceVideoCodec.Av1, 8, null, 0, 0)]
    [InlineData("vp9.flv", SourceVideoCodec.Vp9, 8, null, 0, 0)]
    [InlineData("opus.flv", null, 0, SourceAudioCodec.Opus, 48000, 2)]
    [InlineData("flac.flv", null, 0, SourceAudioCodec.Flac, 48000, 2)]
    [InlineData("ac3.flv", null, 0, SourceAudioCodec.Ac3, 48000, 6)]
    [InlineData("eac3.flv", null, 0, SourceAudioCodec.Eac3, 48000, 6)]
    [InlineData("mp3.flv", null, 0, SourceAudioCodec.Mp3, 44100, 1)]
    public void Fixture_IsIdentified(string file, SourceVideoCodec? video, int bitDepth, SourceAudioCodec? audio, int sampleRate, int channels)
    {
        var parser = NewParser();
        var samples = 0;
        var keyframeFirst = true;
        var sawVideo = false;
        foreach (var tag in FlvFile.ReadFixture(file))
        {
            if (tag.Type is not (8 or 9))
            {
                continue;
            }

            var result = Parse(parser, TranscodingHarness.Kind(tag), tag.Timestamp, tag.Body);
            Assert.True(result.Outcome is not (SourceParseOutcome.Dropped or SourceParseOutcome.Rejected), $"{file}: {result.Outcome} {result.ErrorKind} {result.Rejection}: {result.Detail}");
            if (result.Outcome == SourceParseOutcome.Sample)
            {
                samples++;
                if (result.Track == MediaTrack.Video && !sawVideo)
                {
                    sawVideo = true;
                    keyframeFirst = result.IsKeyframe;
                }
            }
        }

        var configuration = parser.Configuration;
        Assert.True(samples > 0, file);
        Assert.Equal(video, configuration.Video?.Codec);
        Assert.Equal(audio, configuration.Audio?.Codec);
        if (configuration.Video is { } v)
        {
            Assert.True(keyframeFirst, "The first video sample is a keyframe.");
            if (v.BitDepth is { } depth)
            {
                Assert.Equal(bitDepth, depth);
            }

            Assert.True(v.Width is null || v.Width is 64 or 160, $"width {v.Width}");
        }

        if (configuration.Audio is { } a)
        {
            Assert.Equal(sampleRate, a.SampleRate);
            Assert.Equal(channels, a.Channels);
        }
    }

    /// <summary>Legacy FLV video codec IDs other than 7 (AVC) end the publish.</summary>
    /// <param name="codecId">The codec ID.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(15)]
    public void LegacyVideoCodec_OtherThanAvc_IsRejected(int codecId)
    {
        var result = Parse(NewParser(), PublishedMessageKind.Video, 0, [(byte)(0x10 | codecId), 0, 0, 0, 0, 1, 2, 3]);
        Assert.Equal(SourceParseOutcome.Rejected, result.Outcome);
        Assert.Equal(TranscodingRejection.UnsupportedCodec, result.Rejection);
    }

    /// <summary>Legacy FLV sound formats other than AAC and MP3 end the publish.</summary>
    /// <param name="format">The sound format.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(15)]
    public void LegacySoundFormat_OtherThanAacOrMp3_IsRejected(int format)
    {
        var result = Parse(NewParser(), PublishedMessageKind.Audio, 0, [(byte)((format << 4) | 0x0F), 0, 1, 2, 3]);
        Assert.Equal(SourceParseOutcome.Rejected, result.Outcome);
        Assert.Equal(TranscodingRejection.UnsupportedCodec, result.Rejection);
    }

    /// <summary>Enhanced RTMP FourCCs that are not supported end the publish, including in multitrack messages.</summary>
    /// <param name="video">Whether the FourCC is a video one.</param>
    /// <param name="fourCc">The FourCC.</param>
    [Theory]
    [InlineData(true, "vp08")]
    [InlineData(true, "vvc1")]
    [InlineData(true, "abcd")]
    [InlineData(true, "\u0001\u0002\u0003\u0004")]
    [InlineData(false, "abcd")]
    [InlineData(false, "mp4v")]
    [InlineData(false, "ac-4")]
    public void UnsupportedFourCc_IsRejected(bool video, string fourCc)
    {
        var code = Encoding.Latin1.GetBytes(fourCc);
        var kind = video ? PublishedMessageKind.Video : PublishedMessageKind.Audio;
        var single = Parse(NewParser(), kind, 0, [0x90, .. code, 1, 2, 3]);
        Assert.Equal(SourceParseOutcome.Rejected, single.Outcome);
        Assert.Equal(TranscodingRejection.UnsupportedCodec, single.Rejection);
        Assert.All(single.Detail ?? string.Empty, static c => Assert.False(char.IsControl(c)));

        byte[] multitrack = video ? [0x96, 0x00, .. code, 0, 1, 2, 3] : [0x95, 0x00, .. code, 0, 1, 2, 3];
        var multi = Parse(NewParser(), kind, 0, multitrack);
        Assert.Equal(SourceParseOutcome.Rejected, multi.Outcome);
    }

    /// <summary>Codecs excluded by the accepted-codec lists end the publish.</summary>
    [Fact]
    public void ExcludedCodec_IsRejected()
    {
        var parser = NewParser(static o =>
        {
            o.AcceptedVideoCodecs = [SourceVideoCodec.Hevc];
            o.AcceptedAudioCodecs = [SourceAudioCodec.Opus];
        });
        var video = Parse(parser, PublishedMessageKind.Video, 0, FlvPackets.AvcSequenceHeader(H264Record()));
        Assert.Equal(TranscodingRejection.CodecNotAccepted, video.Rejection);
        var audio = Parse(parser, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader());
        Assert.Equal(TranscodingRejection.CodecNotAccepted, audio.Rejection);
    }

    /// <summary>Pictures larger than the configured maximum end the publish.</summary>
    /// <param name="file">The fixture.</param>
    /// <param name="width">The maximum width.</param>
    /// <param name="height">The maximum height.</param>
    [Theory]
    [InlineData("h264-aac.flv", 100, 3840)]
    [InlineData("h264-aac.flv", 3840, 100)]
    [InlineData("hevc.flv", 32, 3840)]
    [InlineData("av1.flv", 32, 3840)]
    [InlineData("vp9.flv", 3840, 32)]
    public void OversizedPicture_IsRejected(string file, int width, int height)
    {
        var parser = NewParser(o =>
        {
            o.MaxInputWidth = width;
            o.MaxInputHeight = height;
        });
        Assert.Contains(ParseVideo(parser, file), static r => r.Outcome == SourceParseOutcome.Rejected && r.Rejection == TranscodingRejection.ResolutionLimit);
    }

    /// <summary>A stream faster than the maximum frame rate ends the publish; one at the limit does not.</summary>
    [Fact]
    public void FrameRate_AboveTheLimit_IsRejected()
    {
        var fast = ParseVideo(NewParser(static o => o.MaxInputFrameRate = 5), "h264-aac-4s.flv");
        Assert.Contains(fast, static r => r.Outcome == SourceParseOutcome.Rejected && r.Rejection == TranscodingRejection.FrameRateLimit);
        var ok = ParseVideo(NewParser(static o => o.MaxInputFrameRate = 10), "h264-aac-4s.flv");
        Assert.DoesNotContain(ok, static r => r.Outcome == SourceParseOutcome.Rejected);
    }

    /// <summary>Malformed video packets are dropped as malformed and never rejected or delivered.</summary>
    /// <param name="hex">The payload in hex.</param>
    [Theory]
    [InlineData("")]
    [InlineData("17")]
    [InlineData("1703000000")]
    [InlineData("67010000")]
    [InlineData("9068")]
    [InlineData("97")]
    [InlineData("9702")]
    [InlineData("96")]
    [InlineData("96106876633100")]
    [InlineData("96106876633100000010")]
    [InlineData("963068766331")]
    [InlineData("966068766331")]
    [InlineData("9968766331")]
    [InlineData("F168766331000000")]
    [InlineData("D7")]
    public void MalformedVideo_IsDropped(string hex)
    {
        var result = Parse(NewParser(), PublishedMessageKind.Video, 0, Convert.FromHexString(hex));
        Assert.Equal(SourceParseOutcome.Dropped, result.Outcome);
        Assert.Equal(MediaErrorKind.MalformedPacket, result.ErrorKind);
        Assert.False(string.IsNullOrEmpty(result.Detail));
    }

    /// <summary>Malformed audio packets are dropped as malformed.</summary>
    /// <param name="hex">The payload in hex.</param>
    [Theory]
    [InlineData("AF")]
    [InlineData("AF02")]
    [InlineData("904F70")]
    [InlineData("97")]
    [InlineData("95")]
    [InlineData("952000")]
    [InlineData("98" + "4F707573")]
    [InlineData("94" + "4F707573" + "03")]
    [InlineData("94" + "4F707573" + "0206")]
    public void MalformedAudio_IsDropped(string hex)
    {
        var result = Parse(NewParser(), PublishedMessageKind.Audio, 0, Convert.FromHexString(hex));
        Assert.Equal(SourceParseOutcome.Dropped, result.Outcome);
        Assert.Equal(MediaErrorKind.MalformedPacket, result.ErrorKind);
    }

    /// <summary>Coded frames before their codec's sequence start are dropped as unconfigured.</summary>
    [Fact]
    public void FrameBeforeConfiguration_IsDropped()
    {
        var parser = NewParser();
        Assert.Equal(MediaErrorKind.MissingConfiguration, Parse(parser, PublishedMessageKind.Video, 0, FlvPackets.AvcNalu(true, 0, 4, [0x65, 0x88])).ErrorKind);
        Assert.Equal(MediaErrorKind.MissingConfiguration, Parse(parser, PublishedMessageKind.Video, 0, [0x91, .. "hvc1"u8, 0, 0, 0, 0, 0, 0, 2, 0x26, 0x01]).ErrorKind);
        Assert.Equal(MediaErrorKind.MissingConfiguration, Parse(parser, PublishedMessageKind.Audio, 0, FlvPackets.AacRaw(1, 2, 3)).ErrorKind);
        Assert.Equal(MediaErrorKind.MissingConfiguration, Parse(parser, PublishedMessageKind.Audio, 0, [0x91, .. "Opus"u8, 0xFC]).ErrorKind);

        Parse(parser, PublishedMessageKind.Video, 0, FlvPackets.AvcSequenceHeader(H264Record()));
        var otherCodec = Parse(parser, PublishedMessageKind.Video, 0, [0x91, .. "hvc1"u8, 0, 0, 0, 0, 0, 0, 2, 0x26, 0x01]);
        Assert.Equal(MediaErrorKind.MissingConfiguration, otherCodec.ErrorKind);
    }

    /// <summary>Invalid sequence starts are dropped and clear an existing configuration.</summary>
    [Fact]
    public void InvalidConfiguration_IsDroppedAndClearsTheTrack()
    {
        var parser = NewParser();
        Assert.Equal(MediaErrorKind.InvalidConfiguration, Parse(parser, PublishedMessageKind.Video, 0, [0x90, .. "hvc1"u8, 1, 2, 3]).ErrorKind);
        Assert.Equal(MediaErrorKind.InvalidConfiguration, Parse(parser, PublishedMessageKind.Video, 0, [0x90, .. "av01"u8, 0x81]).ErrorKind);
        Assert.Equal(MediaErrorKind.InvalidConfiguration, Parse(parser, PublishedMessageKind.Audio, 0, [0x90, .. "Opus"u8, .. "OpusHead"u8]).ErrorKind);

        Assert.Equal(SourceParseOutcome.Configured, Parse(parser, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader()).Outcome);
        var cleared = Parse(parser, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader([0xF8]));
        Assert.Equal(MediaErrorKind.InvalidConfiguration, cleared.ErrorKind);
        Assert.True(cleared.ConfigurationChanged);
        Assert.Null(parser.Configuration.Audio);
    }

    /// <summary>Samples above the media sample-size limit are dropped.</summary>
    [Fact]
    public void OversizedSample_IsDropped()
    {
        var parser = new SourceMediaParser(new TranscodingOptions(), new MediaOptions { MaxSampleSize = 16 });
        var results = ParseVideo(parser, "h264-aac.flv");
        Assert.Contains(results, static r => r.Outcome == SourceParseOutcome.Dropped && r.ErrorKind == MediaErrorKind.SampleTooLarge);
        Assert.DoesNotContain(results, static r => r.Outcome == SourceParseOutcome.Sample);
    }

    /// <summary>Track 0 of a multitrack message is used and other tracks are counted and discarded.</summary>
    [Fact]
    public void Multitrack_UsesTrackZero()
    {
        var parser = NewParser();
        var record = HevcRecord();
        var size = new[] { (byte)(record.Length >> 16), (byte)(record.Length >> 8), (byte)record.Length };
        byte[] both = [0x96, 0x10, .. "hvc1"u8, 1, .. size, .. record, 0, .. size, .. record];
        var result = Parse(parser, PublishedMessageKind.Video, 0, both);
        Assert.Equal(SourceParseOutcome.Configured, result.Outcome);
        Assert.Equal(SourceVideoCodec.Hevc, parser.Configuration.Video?.Codec);
        Assert.Equal(1, parser.IgnoredTrackPackets);

        byte[] otherOnly = [0x96, 0x10, .. "hvc1"u8, 3, .. size, .. record];
        Assert.Equal(SourceParseOutcome.Ignored, Parse(parser, PublishedMessageKind.Video, 0, otherOnly).Outcome);
        Assert.Equal(2, parser.IgnoredTrackPackets);

        byte[] perTrackCodec = [0x96, 0x20, .. "vp08"u8, 1, 0, 0, 1, 0, .. "hvc1"u8, 0, .. size, .. record];
        Assert.Equal(SourceParseOutcome.Ignored, Parse(parser, PublishedMessageKind.Video, 0, perTrackCodec).Outcome);
    }

    /// <summary>ModEx extensions before the packet type are skipped.</summary>
    [Fact]
    public void ModEx_IsSkipped()
    {
        var parser = NewParser();
        byte[] payload = [0x97, 0x02, 0x00, 0x01, 0x02, 0x00, .. "hvc1"u8, .. HevcRecord()];
        Assert.Equal(SourceParseOutcome.Configured, Parse(parser, PublishedMessageKind.Video, 0, payload).Outcome);

        var chain = new List<byte> { 0x97 };
        for (var i = 0; i < 17; i++)
        {
            chain.AddRange([0x02, 0, 0, 0, 0x07]);
        }

        var tooMany = Parse(parser, PublishedMessageKind.Video, 0, [.. chain]);
        Assert.Equal(SourceParseOutcome.Dropped, tooMany.Outcome);
    }

    /// <summary>Command frames and sequence ends are ignored.</summary>
    /// <param name="hex">The payload in hex.</param>
    [Theory]
    [InlineData("5700")]
    [InlineData("D000")]
    [InlineData("D0" + "68766331" + "00")]
    [InlineData("1702000000")]
    [InlineData("92" + "68766331")]
    public void CommandFrameAndSequenceEnd_AreIgnored(string hex)
    {
        Assert.Equal(SourceParseOutcome.Ignored, Parse(NewParser(), PublishedMessageKind.Video, 0, Convert.FromHexString(hex)).Outcome);
    }

    /// <summary>colorInfo metadata declaring BT.2100 PQ or HLG is recorded; SDR metadata clears it; repeats are ignored.</summary>
    /// <param name="transfer">The declared transfer characteristics.</param>
    [Theory]
    [InlineData(16)]
    [InlineData(18)]
    public void ColorInfo_DeclaresHdr(int transfer)
    {
        var parser = NewParser();
        var hdr = Parse(parser, PublishedMessageKind.Video, 0, ColorInfo(9, transfer, 9));
        Assert.Equal(SourceParseOutcome.Configured, hdr.Outcome);
        Assert.True(parser.Configuration.Color?.IsHighDynamicRange);
        Assert.Equal(transfer, parser.Configuration.Color?.Transfer);
        Assert.Equal(SourceParseOutcome.Ignored, Parse(parser, PublishedMessageKind.Video, 0, ColorInfo(9, transfer, 9)).Outcome);

        Assert.Equal(SourceParseOutcome.Configured, Parse(parser, PublishedMessageKind.Video, 0, ColorInfo(1, 1, 1)).Outcome);
        Assert.Null(parser.Configuration.Color);
        Assert.Equal(SourceParseOutcome.Dropped, Parse(parser, PublishedMessageKind.Video, 0, [0x94, .. "hvc1"u8, 0x03, 0x00]).Outcome);
    }

    /// <summary>Enhanced RTMP AAC, empty audio frames and multichannel configuration packets are handled.</summary>
    [Fact]
    public void EnhancedAudio_IsParsed()
    {
        var parser = NewParser();
        Assert.Equal(SourceParseOutcome.Configured, Parse(parser, PublishedMessageKind.Audio, 0, [0x90, .. "mp4a"u8, 0x11, 0x90]).Outcome);
        Assert.Equal(48000, parser.Configuration.Audio?.SampleRate);
        Assert.Equal(SourceParseOutcome.Ignored, Parse(parser, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader([0x11, 0x90])).Outcome);
        Assert.Equal(SourceParseOutcome.Ignored, Parse(parser, PublishedMessageKind.Audio, 0, [0xAF, 0x01]).Outcome);
        Assert.Equal(SourceParseOutcome.Ignored, Parse(parser, PublishedMessageKind.Audio, 0, [0x94, .. "mp4a"u8, 0x01, 0x06, 0, 0, 0, 0x3F]).Outcome);
        Assert.Equal(SourceParseOutcome.Sample, Parse(parser, PublishedMessageKind.Audio, 23, [0x91, .. "mp4a"u8, 0x21, 0x00]).Outcome);
    }

    /// <summary>Creates a parser.</summary>
    /// <param name="configure">Adjusts the options.</param>
    /// <returns>The parser.</returns>
    private static SourceMediaParser NewParser(Action<TranscodingOptions>? configure = null)
    {
        var options = new TranscodingOptions();
        configure?.Invoke(options);
        return new SourceMediaParser(options, new MediaOptions());
    }

    /// <summary>Parses one payload, releasing the message or sample.</summary>
    /// <param name="parser">The parser.</param>
    /// <param name="kind">The message kind.</param>
    /// <param name="timestamp">The RTMP timestamp.</param>
    /// <param name="payload">The payload.</param>
    /// <returns>The result, with the sample's keyframe flag copied.</returns>
    private static ParsedPacket Parse(SourceMediaParser parser, PublishedMessageKind kind, uint timestamp, byte[] payload)
    {
        var buffer = new PooledBuffer(ArrayPool<byte>.Shared);
        buffer.EnsureCapacity(Math.Max(payload.Length, 1));
        buffer.Append(new ReadOnlySequence<byte>(payload));
        var message = new PublishedMessage(kind, kind == PublishedMessageKind.Audio ? (byte)8 : (byte)9, timestamp, buffer);
        var result = parser.Parse(message);
        var keyframe = result.Sample?.IsKeyframe ?? false;
        if (result.Sample is { } sample)
        {
            sample.Dispose();
        }
        else
        {
            message.Dispose();
        }

        return new ParsedPacket(result.Outcome, result.Track, result.ErrorKind, result.Rejection, result.Detail, result.ConfigurationChanged, keyframe);
    }

    /// <summary>Parses a fixture's video tags.</summary>
    /// <param name="parser">The parser.</param>
    /// <param name="file">The fixture.</param>
    /// <returns>The results.</returns>
    private static List<ParsedPacket> ParseVideo(SourceMediaParser parser, string file) =>
        [.. FlvFile.ReadFixture(file).Where(static t => t.Type == 9).Select(t => Parse(parser, PublishedMessageKind.Video, t.Timestamp, t.Body))];

    /// <summary>Gets the H.264 configuration record of the H.264 fixture.</summary>
    /// <returns>The AVCDecoderConfigurationRecord.</returns>
    private static byte[] H264Record() => FlvFile.ReadFixture("h264-aac.flv").First(static t => t.Type == 9 && t.Body[1] == 0).Body[5..];

    /// <summary>Gets the HEVC configuration record of the HEVC fixture.</summary>
    /// <returns>The HEVCDecoderConfigurationRecord.</returns>
    private static byte[] HevcRecord() => FlvFile.ReadFixture("hevc.flv").First(static t => t.Type == 9 && (t.Body[0] & 0x0F) == 0).Body[5..];

    /// <summary>Builds an Enhanced RTMP HEVC colorInfo metadata packet.</summary>
    /// <param name="primaries">The colour primaries.</param>
    /// <param name="transfer">The transfer characteristics.</param>
    /// <param name="matrix">The matrix coefficients.</param>
    /// <returns>The payload.</returns>
    private static byte[] ColorInfo(int primaries, int transfer, int matrix)
    {
        var output = new ArrayBufferWriter<byte>();
        Amf0Writer.WriteValue(output, AmfValue.FromString("colorInfo"));
        Amf0Writer.WriteValue(output, AmfValue.CreateObject(
        [
            new("colorConfig", AmfValue.CreateObject(
            [
                new("bitDepth", AmfValue.FromNumber(10)),
                new("colorPrimaries", AmfValue.FromNumber(primaries)),
                new("transferCharacteristics", AmfValue.FromNumber(transfer)),
                new("matrixCoefficients", AmfValue.FromNumber(matrix)),
            ])),
        ]));
        return [0x94, .. "hvc1"u8, .. output.WrittenSpan];
    }

    /// <summary>A parse result without the sample.</summary>
    /// <param name="Outcome">The outcome.</param>
    /// <param name="Track">The track.</param>
    /// <param name="ErrorKind">The drop reason.</param>
    /// <param name="Rejection">The rejection reason.</param>
    /// <param name="Detail">The description.</param>
    /// <param name="ConfigurationChanged">Whether the configuration changed.</param>
    /// <param name="IsKeyframe">Whether the sample was a keyframe.</param>
    private sealed record ParsedPacket(SourceParseOutcome Outcome, MediaTrack Track, MediaErrorKind ErrorKind, TranscodingRejection Rejection, string? Detail, bool ConfigurationChanged, bool IsKeyframe);
}
