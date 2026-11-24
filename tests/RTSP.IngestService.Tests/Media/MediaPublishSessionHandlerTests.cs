// Tests of MediaPublishSessionHandler driven directly with pooled messages: AVC and AAC sample extraction, signed
// composition times, NAL framing validation, limits, configuration changes, missing configuration, unsupported
// codecs, metadata, timestamps, ownership of pooled payloads and per-session state cleanup.

using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media;
using RTSP.IngestService.Rtmp.Amf;
using RTSP.IngestService.Rtmp.Publishing;

namespace RTSP.IngestService.Tests.Media;

/// <summary>Tests for <see cref="MediaPublishSessionHandler"/>.</summary>
public sealed class MediaPublishSessionHandlerTests : IDisposable
{
    /// <summary>An IDR slice NAL unit.</summary>
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00, 0x33];

    /// <summary>A non-IDR slice NAL unit.</summary>
    private static readonly byte[] Slice = [0x41, 0x9A, 0x02];

    /// <summary>An SEI NAL unit.</summary>
    private static readonly byte[] Sei = [0x06, 0x05, 0x01];

    /// <summary>The harness.</summary>
    private readonly MediaHarness _harness = new();

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    /// <summary>A keyframe with several NAL units is delivered with its NAL boundaries and bytes intact.</summary>
    [Fact]
    public async Task Keyframe_WithMultipleNalUnits_IsDelivered()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);
        var packet = FlvPackets.AvcNalu(true, 0, 4, Sei, Idr);

        await _harness.VideoAsync(session, 40, packet);

        var video = Assert.Single(_harness.Downstream.Video);
        Assert.True(video.IsKeyframe);
        Assert.True(video.ContainsIdr);
        Assert.Equal(packet[5..], video.Data);
        Assert.Equal([Sei, Idr], video.NalUnits);
        Assert.Equal(0, video.SequenceNumber);
        Assert.Equal(4, video.Track.NalLengthSize);
        Assert.Equal(new MediaTimestamps(40, 40, 0), video.Timestamps);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>An interframe is delivered as a non-keyframe without an IDR.</summary>
    [Fact]
    public async Task Interframe_IsDelivered()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        await _harness.VideoAsync(session, 33, FlvPackets.AvcNalu(false, 0, 4, Slice));

        var video = _harness.Downstream.Video;
        Assert.Equal(2, video.Count);
        Assert.False(video[1].IsKeyframe);
        Assert.False(video[1].ContainsIdr);
        Assert.Equal(1, video[1].SequenceNumber);
        Assert.Equal(33, video[1].Delta);
        var media = video[1].Session;
        Assert.Equal(2, media.VideoSampleCount);
        Assert.Equal(1, media.KeyframeCount);
    }

    /// <summary>The signed 24-bit composition time gives PTS = DTS + CTO, including negative and boundary values.</summary>
    /// <param name="compositionTime">The composition time.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(-200)]
    [InlineData(8388607)]
    [InlineData(-8388608)]
    public async Task CompositionTime_IsSigned(int compositionTime)
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 1000, FlvPackets.AvcNalu(false, compositionTime, 4, Slice));

        var timestamps = Assert.Single(_harness.Downstream.Video).Timestamps;
        Assert.Equal(1000, timestamps.DecodeTimestamp);
        Assert.Equal(compositionTime, timestamps.CompositionTimeOffset);
        Assert.Equal(1000 + compositionTime, timestamps.PresentationTimestamp);
    }

    /// <summary>The NAL length size from the configuration is used for framing.</summary>
    /// <param name="lengthSizeMinusOne">The configuration's field.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task NalLengthSize_FromConfiguration_IsUsed(int lengthSizeMinusOne)
    {
        var session = await _harness.StartAsync();
        await _harness.VideoAsync(session, 0, FlvPackets.AvcSequenceHeader(AvcDecoderConfigurationTests.Record(lengthSizeMinusOne)));

        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, lengthSizeMinusOne + 1, Sei, Idr));

        Assert.Equal([Sei, Idr], Assert.Single(_harness.Downstream.Video).NalUnits);
    }

    /// <summary>NAL framing errors are dropped as malformed, without delivering a sample.</summary>
    /// <param name="hex">The access unit after the 5-byte header, as hex.</param>
    [Theory]
    [InlineData("")]
    [InlineData("000000")]
    [InlineData("00000000")]
    [InlineData("0000000541")]
    [InlineData("00000003419A02000000")]
    [InlineData("00000002C19A")]
    [InlineData("FFFFFFFF41")]
    public async Task InvalidNalFraming_IsMalformed(string hex)
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, [0x27, 0x01, 0, 0, 0, .. Convert.FromHexString(hex)]);

        Assert.Empty(_harness.Downstream.Video);
        var error = Assert.Single(_harness.Downstream.Errors);
        Assert.Equal(MediaErrorKind.MalformedPacket, error.Kind);
        Assert.Equal(MediaTrack.Video, error.Track);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Samples above the configured size limit are dropped.</summary>
    [Fact]
    public async Task OversizedSample_IsDropped()
    {
        using var harness = new MediaHarness(new MediaOptions { MaxSampleSize = 1024 });
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);

        await harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, [0x65, .. new byte[1019]]));
        await harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, [0x65, .. new byte[1020]]));
        await harness.AudioAsync(session, 0, FlvPackets.AacRaw(new byte[1025]));

        Assert.Single(harness.Downstream.Video);
        Assert.Equal([MediaErrorKind.SampleTooLarge, MediaErrorKind.SampleTooLarge], harness.Downstream.Errors.Select(static e => e.Kind));
        Assert.Equal(0, harness.Pool.Outstanding);
    }

    /// <summary>Access units with more NAL units than the limit are dropped.</summary>
    [Fact]
    public async Task TooManyNalUnits_IsDropped()
    {
        using var harness = new MediaHarness(new MediaOptions { MaxNalUnitsPerSample = 2 });
        var session = await harness.StartAsync();
        await harness.ConfigureAsync(session);

        await harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Sei, Idr));
        await harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Sei, Sei, Idr));

        Assert.Single(harness.Downstream.Video);
        Assert.Equal(MediaErrorKind.TooManyNalUnits, Assert.Single(harness.Downstream.Errors).Kind);
    }

    /// <summary>Invalid FLV video headers are malformed; unsupported codecs and enhanced RTMP are reported as such.</summary>
    /// <param name="hex">The packet.</param>
    /// <param name="kind">The expected error.</param>
    [Theory]
    [InlineData("", MediaErrorKind.MalformedPacket)]
    [InlineData("17", MediaErrorKind.MalformedPacket)]
    [InlineData("170100", MediaErrorKind.MalformedPacket)]
    [InlineData("1703000000", MediaErrorKind.MalformedPacket)]
    [InlineData("4701000000", MediaErrorKind.MalformedPacket)]
    [InlineData("0701000000", MediaErrorKind.MalformedPacket)]
    [InlineData("12000000", MediaErrorKind.UnsupportedCodec)]
    [InlineData("1C00000000", MediaErrorKind.UnsupportedCodec)]
    [InlineData("9068766331", MediaErrorKind.UnsupportedCodec)]
    public async Task InvalidVideoHeaders_AreReported(string hex, MediaErrorKind kind)
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, Convert.FromHexString(hex));

        Assert.Equal(kind, Assert.Single(_harness.Downstream.Errors).Kind);
        Assert.Empty(_harness.Downstream.Video);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Video info/command frames and end-of-sequence packets are ignored without errors.</summary>
    [Fact]
    public async Task CommandAndEndOfSequence_AreIgnored()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, [0x57, 0x00]);
        await _harness.VideoAsync(session, 0, [0x17, 0x02, 0, 0, 0]);

        Assert.Empty(_harness.Downstream.Errors);
        Assert.Empty(_harness.Downstream.Video);
    }

    /// <summary>AAC raw access units are delivered unchanged, without ADTS headers.</summary>
    [Fact]
    public async Task AacRaw_IsDelivered()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);
        byte[] accessUnit = [0x21, 0x10, 0x04, 0x60, 0x8C, 0x1C];

        await _harness.AudioAsync(session, 23, FlvPackets.AacRaw(accessUnit));
        await _harness.AudioAsync(session, 46, FlvPackets.AacRaw(accessUnit));

        var audio = _harness.Downstream.Audio;
        Assert.Equal(2, audio.Count);
        Assert.Equal(accessUnit, audio[0].Data);
        Assert.Equal(new MediaTimestamps(23, 23, 0), audio[0].Timestamps);
        Assert.Equal(23, audio[1].Delta);
        Assert.Equal(44100, audio[0].Track.SampleRate);
        Assert.Equal(2, audio[0].Track.ChannelCount);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Invalid audio packets are reported with the right kind.</summary>
    /// <param name="hex">The packet.</param>
    /// <param name="kind">The expected error.</param>
    [Theory]
    [InlineData("", MediaErrorKind.MalformedPacket)]
    [InlineData("AF", MediaErrorKind.MalformedPacket)]
    [InlineData("AF02", MediaErrorKind.MalformedPacket)]
    [InlineData("AF01", MediaErrorKind.MalformedPacket)]
    [InlineData("2F0100", MediaErrorKind.UnsupportedCodec)]
    [InlineData("82AABB", MediaErrorKind.UnsupportedCodec)]
    public async Task InvalidAudioPackets_AreReported(string hex, MediaErrorKind kind)
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.AudioAsync(session, 0, Convert.FromHexString(hex));

        var error = Assert.Single(_harness.Downstream.Errors);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(MediaTrack.Audio, error.Track);
        Assert.Empty(_harness.Downstream.Audio);
    }

    /// <summary>Samples before their track's sequence header are reported as missing configuration.</summary>
    [Fact]
    public async Task SamplesBeforeConfiguration_AreReported()
    {
        var session = await _harness.StartAsync();

        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        await _harness.AudioAsync(session, 0, FlvPackets.AacRaw(0x21));

        Assert.Equal(
            [(MediaErrorKind.MissingConfiguration, MediaTrack.Video), (MediaErrorKind.MissingConfiguration, MediaTrack.Audio)],
            _harness.Downstream.Errors.Select(static e => (e.Kind, e.Track)));
        Assert.Empty(_harness.Downstream.Video);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Configurations produce stream-info changes; identical repeats are not changes.</summary>
    [Fact]
    public async Task Configuration_ChangesStreamInfo()
    {
        var session = await _harness.StartAsync();

        await _harness.ConfigureAsync(session);
        await _harness.ConfigureAsync(session);

        var infos = _harness.Downstream.Infos;
        Assert.Equal(2, infos.Count);
        Assert.Equal(1, infos[0].Version);
        Assert.NotNull(infos[0].Video);
        Assert.Null(infos[0].Audio);
        var final = infos[1];
        Assert.Equal(2, final.Version);
        Assert.Equal("avc1.4D400A", final.Video!.Configuration.CodecString);
        Assert.Equal(1, final.Video.ConfigurationVersion);
        Assert.Equal("mp4a.40.2", final.Audio!.Configuration.CodecString);
        Assert.Same(final, Assert.Single(_harness.Downstream.Events, static e => e.Kind == "started").Session.StreamInfo);
    }

    /// <summary>A changed configuration applies to later samples, which carry the new NAL length size.</summary>
    [Fact]
    public async Task ConfigurationChange_AppliesToLaterSamples()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);
        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));

        await _harness.VideoAsync(session, 40, FlvPackets.AvcSequenceHeader(AvcDecoderConfigurationTests.Record(1)));
        await _harness.VideoAsync(session, 40, FlvPackets.AvcNalu(true, 0, 2, Idr));
        await _harness.AudioAsync(session, 40, FlvPackets.AacSequenceHeader([0x11, 0x88]));
        await _harness.AudioAsync(session, 40, FlvPackets.AacRaw(0x21));

        var video = _harness.Downstream.Video;
        Assert.Equal(4, video[0].Track.NalLengthSize);
        Assert.Equal(2, video[1].Track.NalLengthSize);
        Assert.Equal(2, video[1].Track.ConfigurationVersion);
        var audio = Assert.Single(_harness.Downstream.Audio);
        Assert.Equal(48000, audio.Track.SampleRate);
        Assert.Equal(1, audio.Track.ChannelConfiguration);
        Assert.Equal(2, audio.Track.ConfigurationVersion);
        Assert.Equal(4, video[1].Session.ConfigurationCount);
        Assert.Empty(_harness.Downstream.Errors);
    }

    /// <summary>An invalid sequence header clears the track until a valid one arrives.</summary>
    [Fact]
    public async Task InvalidConfiguration_ClearsTrack()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, [0x17, 0x00, 0, 0, 0, 0x01, 0x4D]);
        await _harness.AudioAsync(session, 0, [0xAF, 0x00, 0x12]);
        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        await _harness.AudioAsync(session, 0, FlvPackets.AacRaw(0x21));
        await _harness.ConfigureAsync(session);
        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));

        Assert.Equal(
            [MediaErrorKind.InvalidConfiguration, MediaErrorKind.InvalidConfiguration, MediaErrorKind.MissingConfiguration, MediaErrorKind.MissingConfiguration],
            _harness.Downstream.Errors.Select(static e => e.Kind));
        var info = _harness.Downstream.Infos;
        Assert.Null(info[2].Video);
        Assert.Null(info[3].Audio);
        Assert.Single(_harness.Downstream.Video);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Metadata is range-checked and applied to the stream info without overriding codec configuration.</summary>
    [Fact]
    public async Task Metadata_IsAppliedAsUntrusted()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);
        session.SetMetadata(
        [
            new("width", AmfValue.FromNumber(1280)),
            new("height", AmfValue.FromNumber(-5)),
            new("framerate", AmfValue.FromNumber(30)),
            new("audiosamplerate", AmfValue.FromNumber(8000)),
            new("encoder", AmfValue.FromString("Lavf61")),
        ]);

        await _harness.DataAsync(session);
        await _harness.DataAsync(session);

        var info = _harness.Downstream.Infos[^1];
        Assert.Equal(3, _harness.Downstream.Infos.Count);
        Assert.Equal(1280, info.Metadata.Width);
        Assert.Null(info.Metadata.Height);
        Assert.Equal(1280, info.Video!.Width);
        Assert.Equal(30, info.Video.FrameRate);
        Assert.Equal(8000, info.Metadata.AudioSampleRate);
        Assert.Equal(44100, info.Audio!.SampleRate);
        Assert.Equal("Lavf61", info.Metadata.Encoder);
        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Audio and video share one timeline that unwraps 32-bit rollover.</summary>
    [Fact]
    public async Task Timestamps_ShareTimelineAcrossRollover()
    {
        var session = await _harness.StartAsync();
        await _harness.VideoAsync(session, 0xFFFFFF00, FlvPackets.AvcSequenceHeader());
        await _harness.AudioAsync(session, 0xFFFFFF00, FlvPackets.AacSequenceHeader());

        await _harness.VideoAsync(session, 0xFFFFFFF0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        await _harness.AudioAsync(session, 0x00000010, FlvPackets.AacRaw(0x21));
        await _harness.VideoAsync(session, 0x00000030, FlvPackets.AvcNalu(false, 0, 4, Slice));

        var video = _harness.Downstream.Video;
        var audio = Assert.Single(_harness.Downstream.Audio);
        Assert.Equal(0xFFFFFFF0L, video[0].Timestamps.DecodeTimestamp);
        Assert.Equal(0x100000010L, audio.Timestamps.DecodeTimestamp);
        Assert.Equal(0x10u, audio.Timestamps.SourceTimestamp);
        Assert.Equal(0x100000030L, video[1].Timestamps.DecodeTimestamp);
        Assert.Equal(0x40, video[1].Delta);
        Assert.Equal(TimestampDiscontinuity.None, video[1].Discontinuity);
    }

    /// <summary>Regressions and jumps are flagged and counted but passed through unchanged.</summary>
    [Fact]
    public async Task Discontinuities_AreFlaggedNotRewritten()
    {
        using var collector = new MetricCollector<long>(_harness.MeterFactory, MediaMetrics.MeterName, "media.timestamp_discontinuities");
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.AudioAsync(session, 1000, FlvPackets.AacRaw(0x21));
        await _harness.AudioAsync(session, 900, FlvPackets.AacRaw(0x21));
        await _harness.AudioAsync(session, 900 + 10_001, FlvPackets.AacRaw(0x21));
        await _harness.AudioAsync(session, 900 + 20_001, FlvPackets.AacRaw(0x21));

        var audio = _harness.Downstream.Audio;
        Assert.Equal([1000L, 900L, 10_901L, 20_901L], audio.Select(static a => a.Timestamps.DecodeTimestamp));
        Assert.Equal(
            [TimestampDiscontinuity.None, TimestampDiscontinuity.Regression, TimestampDiscontinuity.Jump, TimestampDiscontinuity.None],
            audio.Select(static a => a.Discontinuity));
        Assert.Equal(2, audio[0].Session.DiscontinuityCount);
        Assert.Equal(["regression", "jump"], collector.GetMeasurementSnapshot().Select(static m => (string)m.Tags["kind"]!));
        Assert.Single(_harness.Logs.GetSnapshot(), static r => r.Id.Id == 6006);
        Assert.Single(_harness.Logs.GetSnapshot(), static r => r.Id.Id == 6007);
    }

    /// <summary>The first error of each kind logs a warning; repeats log at Debug.</summary>
    [Fact]
    public async Task Errors_AreRateLimitedInLogs()
    {
        var session = await _harness.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            await _harness.VideoAsync(session, 0, [0x22, 0x00]);
            await _harness.AudioAsync(session, 0, FlvPackets.AacRaw(0x21));
        }

        var logs = _harness.Logs.GetSnapshot();
        Assert.Single(logs, static r => r.Id.Id == 6003 && r.Level == LogLevel.Warning);
        Assert.Single(logs, static r => r.Id.Id == 6004 && r.Level == LogLevel.Warning);
        Assert.Equal(8, logs.Count(static r => r.Id.Id == 6005));
        Assert.Equal(10, _harness.Downstream.Errors.Count);
    }

    /// <summary>Metrics use low-cardinality tags only.</summary>
    [Fact]
    public async Task Metrics_UseFixedTags()
    {
        using var samples = new MetricCollector<long>(_harness.MeterFactory, MediaMetrics.MeterName, "media.samples");
        using var errors = new MetricCollector<long>(_harness.MeterFactory, MediaMetrics.MeterName, "media.errors");
        using var keyframes = new MetricCollector<long>(_harness.MeterFactory, MediaMetrics.MeterName, "media.keyframes");
        using var configurations = new MetricCollector<long>(_harness.MeterFactory, MediaMetrics.MeterName, "media.configurations");
        using var active = new MetricCollector<long>(_harness.MeterFactory, MediaMetrics.MeterName, "media.sessions.active");
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        await _harness.AudioAsync(session, 0, FlvPackets.AacRaw(0x21));
        await _harness.VideoAsync(session, 0, [0x22, 0x00]);
        await _harness.Processor.OnSessionEndedAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal(["video", "audio"], samples.GetMeasurementSnapshot().Select(static m => (string)m.Tags["track"]!));
        Assert.Equal(1, keyframes.GetMeasurementSnapshot().EvaluateAsCounter());
        Assert.Equal(2, configurations.GetMeasurementSnapshot().EvaluateAsCounter());
        var error = Assert.Single(errors.GetMeasurementSnapshot());
        Assert.Equal("unsupported_codec", error.Tags["kind"]);
        Assert.Equal("video", error.Tags["track"]);
        Assert.Equal(0, active.GetMeasurementSnapshot().EvaluateAsCounter());
        Assert.All(samples.GetMeasurementSnapshot(), static m => Assert.Equal(["track"], m.Tags.Keys));
    }

    /// <summary>A sample is valid until disposed; afterwards its data throws and the buffer is back in the pool.</summary>
    [Fact]
    public async Task Sample_OwnsPayloadUntilDisposed()
    {
        _harness.Downstream.RetainSamples = true;
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);

        await _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));

        Assert.Equal(1, _harness.Pool.Outstanding);
        Assert.True(_harness.Downstream.Retained.TryDequeue(out var sample));
        var video = Assert.IsType<EncodedVideoSample>(sample);
        Assert.Equal(Idr, video.GetNalUnitData(0).ToArray());
        video.Dispose();
        video.Dispose();
        Assert.Equal(0, _harness.Pool.Outstanding);
        Assert.Throws<ObjectDisposedException>(() => video.Data);
        Assert.Throws<ObjectDisposedException>(() => video.GetNalUnitData(0));
    }

    /// <summary>A downstream exception propagates (ending the session) and the sample is disposed.</summary>
    [Fact]
    public async Task DownstreamFailure_DisposesSampleAndPropagates()
    {
        var session = await _harness.StartAsync();
        await _harness.ConfigureAsync(session);
        _harness.Downstream.FailSamples = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _harness.VideoAsync(session, 0, FlvPackets.AvcNalu(true, 0, 4, Idr)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _harness.AudioAsync(session, 0, FlvPackets.AacRaw(0x21)));

        Assert.Equal(0, _harness.Pool.Outstanding);
    }

    /// <summary>Sessions have independent state; ending one releases only its state.</summary>
    [Fact]
    public async Task Sessions_AreIsolated()
    {
        var first = await _harness.StartAsync();
        var second = await _harness.StartAsync();
        await _harness.ConfigureAsync(first);

        await _harness.VideoAsync(second, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        await _harness.VideoAsync(first, 0, FlvPackets.AvcNalu(true, 0, 4, Idr));
        Assert.Equal(2, _harness.Processor.ActiveSessionCount);
        await _harness.Processor.OnSessionEndedAsync(first, TestContext.Current.CancellationToken);

        Assert.Equal(first.SessionId, Assert.Single(_harness.Downstream.Video).Session.SessionId);
        Assert.Equal(second.SessionId, Assert.Single(_harness.Downstream.Events.OfType<ErrorEvent>()).Session.SessionId);
        Assert.Equal(1, _harness.Processor.ActiveSessionCount);
        await _harness.Processor.OnSessionEndedAsync(second, TestContext.Current.CancellationToken);
        Assert.Equal(0, _harness.Processor.ActiveSessionCount);
        Assert.Single(_harness.Logs.GetSnapshot(), static r => r.Id.Id == 6008 && r.Message.Contains("1 video samples", StringComparison.Ordinal));
    }

    /// <summary>Messages for an unknown session are rejected; the caller disposes them.</summary>
    [Fact]
    public async Task UnknownSession_Throws()
    {
        var message = _harness.Message(PublishedMessageKind.Video, 0, [0x17]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _harness.Processor.OnMessageAsync(MediaHarness.NewSession(), message, TestContext.Current.CancellationToken).AsTask());

        message.Dispose();
        Assert.Equal(0, _harness.Pool.Outstanding);
    }
}
