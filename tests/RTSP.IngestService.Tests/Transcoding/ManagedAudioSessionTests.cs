// Tests of managed AAC-LC audio inside a transcoding session, with the real managed codecs, multiplexer and session
// lifecycle and the scripted in-memory transcoder for video: engine routing and fallback, FFmpeg video-only arguments,
// audio-only publishes that start no transcoder, drain and kill, the timeline controller (absorbed jitter, concealed
// gaps, trimmed overlaps, restarts, drops, timestamp wrap), decoder recovery and persistent decode failure,
// downstream failure, the buffer budget, and stream replacement.

using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Audio.Aac;
using RTSP.IngestService.Media.Codecs.Audio.Aac.Decoder;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Tests for managed audio in <see cref="TranscodingSession"/>.</summary>
public sealed class ManagedAudioSessionTests
{
    /// <summary>The managed engine's FLV AAC sequence header.</summary>
    private static readonly byte[] CanonicalHeader = [0xAF, 0x00, 0x11, 0x90];

    /// <summary>An AAC-LC configuration with 960-sample frames, which the managed decoder does not support.</summary>
    private static readonly byte[] Aac960Config = [0x11, 0x94];

    /// <summary>AAC-LC audio and H.264 video: audio goes to the managed engine and FFmpeg is started for video only.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AacLc_IsTranscodedByTheManagedEngine_AndFfmpegGetsVideoOnly()
    {
        using var harness = Managed();
        var tags = FlvFile.ReadFixture("h264-aac.flv");
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await harness.SendTagsAsync(session, tags);
        Assert.Equal(AudioRoute.Managed, pipeline.AudioEngine);
        await harness.EndAsync(session);

        var process = Assert.Single(harness.Factory.Started);
        Assert.True(process.InputCompleted);
        Assert.Contains("libx264", process.Arguments);
        Assert.DoesNotContain("0:a:0", process.Arguments);
        Assert.DoesNotContain("-c:a", process.Arguments);
        var sourceAudio = tags.Count(static t => t.Type == 8 && t.Body.Length > 2 && t.Body[1] == 1);
        var rate = SourceRate(tags);
        AssertCanonicalAudio(harness, session, ExpectedUnits(sourceAudio, rate));
        Assert.NotEmpty(harness.Delivered(session, PublishedMessageKind.Video));
        Assert.Contains(10024, harness.EventIds());
        Assert.Contains(10031, harness.EventIds());
        Assert.DoesNotContain(10001, harness.EventIds());
        Assert.True(harness.Handler.Budget.Peak <= harness.Handler.Budget.Limit);
        AssertReleased(harness);
    }

    /// <summary>The default engine keeps audio in FFmpeg and logs the choice.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DefaultEngine_KeepsAudioInFfmpeg()
    {
        using var harness = new TranscodingHarness();
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await harness.SendTagsAsync(session, FlvFile.ReadFixture("h264-aac.flv"));
        Assert.Equal(AudioRoute.Ffmpeg, pipeline.AudioEngine);
        Assert.Null(pipeline.AudioStage);
        await harness.EndAsync(session);

        Assert.Contains("0:a:0", Assert.Single(harness.Factory.Started).Arguments);
        Assert.Contains(10024, harness.EventIds());
        Assert.DoesNotContain(10031, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>An audio-only managed publish starts no transcoder and drains all its audio, encoder tail included.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioOnly_StartsNoProcess_AndDrains()
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        await SendAudioAsync(harness, session, config, units, 48000);
        await harness.EndAsync(session);

        Assert.Equal(0, harness.Factory.Starts);
        AssertCanonicalAudio(harness, session, units.Count + 1);
        AssertReleased(harness);
    }

    /// <summary>A killed audio-only publish starts no transcoder, does not flush and releases everything.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task AudioOnly_Kill_DoesNotFlush()
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        await SendAudioAsync(harness, session, config, units, 48000);
        await harness.EndAsync(session, PublishEndReason.ConnectionError);

        Assert.Equal(0, harness.Factory.Starts);
        Assert.True(Samples(harness, session).Count < units.Count + 1, "A killed publish does not deliver the encoder's flush tail.");
        AssertReleased(harness);
    }

    /// <summary>Unsupported AAC profiles and non-AAC codecs are not eligible for the managed engine.</summary>
    [Fact]
    public void Eligibility_RequiresAacLcTheDecoderAccepts()
    {
        Assert.True(ManagedAudioStage.IsEligible(new SourceAudioConfiguration(SourceAudioCodec.Aac, [0x11, 0x90], 48000, 2, "AAC"), out _));
        Assert.False(ManagedAudioStage.IsEligible(new SourceAudioConfiguration(SourceAudioCodec.Aac, Aac960Config, 48000, 2, "AAC"), out var profile));
        Assert.Equal(ManagedAudioIneligibility.Profile, profile);
        Assert.False(ManagedAudioStage.IsEligible(new SourceAudioConfiguration(SourceAudioCodec.Aac, [0x2B, 0x92, 0x08, 0x00], 24000, 2, "HE-AAC"), out var heAac));
        Assert.Equal(ManagedAudioIneligibility.Profile, heAac);
        Assert.False(ManagedAudioStage.IsEligible(new SourceAudioConfiguration(SourceAudioCodec.Aac, [0xF8], 48000, 2, "AAC"), out var invalid));
        Assert.Equal(ManagedAudioIneligibility.Configuration, invalid);
        foreach (var codec in new[] { SourceAudioCodec.Mp3, SourceAudioCodec.Opus, SourceAudioCodec.Flac, SourceAudioCodec.Ac3, SourceAudioCodec.Eac3 })
        {
            Assert.False(ManagedAudioStage.IsEligible(new SourceAudioConfiguration(codec, [], 48000, 2, codec.ToString()), out var reason));
            Assert.Equal(ManagedAudioIneligibility.Codec, reason);
        }
    }

    /// <summary>An unsupported profile falls back to FFmpeg audio with a warning, and the managed stage never runs.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task UnsupportedProfile_FallsBackToFfmpeg()
    {
        using var harness = Managed();
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader(Aac960Config));
        for (var i = 0; i < 80; i++)
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, (uint)(i * 20), FlvPackets.AacRaw(0x21, 0x10, 0x04));
        }

        Assert.Equal(AudioRoute.Ffmpeg, pipeline.AudioEngine);
        Assert.Null(pipeline.AudioStage);
        await harness.EndAsync(session);
        Assert.Contains("0:a:0", Assert.Single(harness.Factory.Started).Arguments);
        Assert.Contains(10025, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>A managed publish whose audio changes to an ineligible configuration is rejected rather than switching engines.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task ManagedThenIneligible_EndsThePublish()
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        await SendAudioAsync(harness, session, config, units.Take(10).ToList(), 48000);
        var ex = await Assert.ThrowsAsync<TranscodingRejectedException>(() => harness.SendAsync(session, PublishedMessageKind.Audio, 300, FlvPackets.AacSequenceHeader(Aac960Config)));
        Assert.Equal(TranscodingRejectionReason.UnsupportedCodec, ex.Reason);
        Assert.Contains(10011, harness.EventIds());
        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        AssertReleased(harness);
    }

    /// <summary>A change to another eligible configuration keeps the managed engine and the output timeline.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task EligibleConfigurationChange_ContinuesTheStream()
    {
        using var harness = Managed();
        var (stereo, stereoUnits) = Fixture("aac-lc-48k-stereo.aac");
        var (mono, monoUnits) = Fixture("aac-lc-44k-mono.aac");
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await SendAudioAsync(harness, session, stereo, stereoUnits.Take(50).ToList(), 48000);
        var next = 50L * 1024 * 1000 / 48000;
        await SendAudioAsync(harness, session, mono, monoUnits.Take(50).ToList(), 44100, (uint)next);
        await harness.EndAsync(session);

        var stage = pipeline.AudioStage!;
        Assert.Equal(0, stage.Resets);
        Assert.Equal(100, stage.DecodedUnits);
        var canonical = (50 * 1024) + (long)Math.Ceiling(50 * 1024 * 48000.0 / 44100);
        AssertCanonicalAudio(harness, session, (int)((canonical + 1023) / 1024) + 1);
        AssertReleased(harness);
    }

    /// <summary>Timestamp jitter and gaps below 100 ms are absorbed; the output stays sample-continuous.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SmallGap_IsAbsorbed()
    {
        var stage = await RunAudioAsync(units => Shifted(units, 100, 50));
        Assert.Equal(0, stage.ConcealedSamples);
        Assert.Equal(0, stage.TrimmedSamples);
        Assert.Equal(0, stage.Resets);
    }

    /// <summary>A gap above 100 ms and below one second is concealed with exactly its duration of silence.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MediumGap_IsConcealed()
    {
        var stage = await RunAudioAsync(units => Shifted(units, 100, 300));
        Assert.InRange(stage.ConcealedSamples, (300 * 48) - 96, (300 * 48) + 96);
        Assert.Equal(0, stage.Resets);
    }

    /// <summary>A gap above one second restarts the output stream on a new anchor.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task LargeGap_RestartsTheStream()
    {
        var stage = await RunAudioAsync(units => Shifted(units, 100, 2000));
        Assert.Equal(1, stage.Resets);
        Assert.Equal(0, stage.ConcealedSamples);
    }

    /// <summary>A timestamp regression restarts the output stream.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Regression_RestartsTheStream()
    {
        var stage = await RunAudioAsync(units => Shifted(units, 100, -500));
        Assert.Equal(1, stage.Resets);
    }

    /// <summary>Source timestamps that advance more slowly than the audio are trimmed once the overlap exceeds 100 ms.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task Overlap_IsTrimmed()
    {
        var stage = await RunAudioAsync(static units => units.Select(static (u, i) => (i < 100 ? (uint)(i * 1024 * 1000 / 48000) : (uint)((100 * 1024 * 1000 / 48000) + ((i - 100) * 10)), u)).ToList());
        Assert.True(stage.TrimmedSamples > 0);
        Assert.Equal(0, stage.Resets);
    }

    /// <summary>RTMP timestamps that wrap past 2^32 milliseconds continue the stream without a restart or concealment.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task TimestampWrap_ContinuesTheStream()
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await SendAudioAsync(harness, session, config, units, 48000, uint.MaxValue - 1000);
        await harness.EndAsync(session);

        var stage = pipeline.AudioStage!;
        Assert.Equal(0, stage.Resets);
        Assert.Equal(0, stage.ConcealedSamples);
        AssertCanonicalAudio(harness, session, units.Count + 1);
        AssertReleased(harness);
    }

    /// <summary>A source frame dropped before decoding is concealed with exactly one frame of silence.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DroppedFrame_IsConcealedExactly()
    {
        using var harness = Managed(media: new MediaOptions { MaxSampleSize = 1024 });
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader(config));
        for (var i = 0; i < 200; i++)
        {
            var payload = i == 100 ? FlvPackets.AacRaw(new byte[1100]) : FlvPackets.AacRaw(units[i].ToArray());
            await harness.SendAsync(session, PublishedMessageKind.Audio, (uint)(i * 1024 * 1000 / 48000), payload);
        }

        await harness.EndAsync(session);
        var stage = pipeline.AudioStage!;
        Assert.Equal(1024, stage.ConcealedSamples);
        Assert.Equal(199, stage.DecodedUnits);
        AssertCanonicalAudio(harness, session, 201);
        AssertReleased(harness);
    }

    /// <summary>A malformed access unit recreates the decoder, is concealed, and the publish continues.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task MalformedUnit_RecreatesTheDecoder_AndIsConcealed()
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var garbage = Undecodable(config);
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        var corrupted = units.Select((u, i) => i == 100 ? garbage : u).ToList();
        await SendAudioAsync(harness, session, config, corrupted, 48000);
        await harness.EndAsync(session);

        var stage = pipeline.AudioStage!;
        Assert.Equal(1, stage.Recreations);
        Assert.Equal(1024, stage.ConcealedSamples);
        Assert.Null(pipeline.Fault);
        AssertCanonicalAudio(harness, session, units.Count + 1);
        Assert.Contains(10029, harness.EventIds());
        AssertReleased(harness);
    }

    /// <summary>The persistent-failure thresholds: more than 50 consecutive failures, or more than 10 % of at least a full 10 s window of units.</summary>
    [Fact]
    public void FailureThresholds_FollowTheDesign()
    {
        Assert.False(ManagedAudioStage.ExceedsFailureThresholds(50, 50, 2000, 96000));
        Assert.True(ManagedAudioStage.ExceedsFailureThresholds(51, 51, 2000, 96000));
        Assert.False(ManagedAudioStage.ExceedsFailureThresholds(1, 46, 468, 48000));
        Assert.True(ManagedAudioStage.ExceedsFailureThresholds(1, 47, 468, 48000));
        Assert.False(ManagedAudioStage.ExceedsFailureThresholds(1, 3, 5, 48000));
        Assert.True(ManagedAudioStage.ExceedsFailureThresholds(1, 8, 70, 8000));
    }

    /// <summary>
    /// After a full window of good audio, sustained decode failures end the publish with a decoder failure; every
    /// failure before that is concealed with a recreated decoder. At 48 kHz the 10 % rule (47 of about 469 units) is
    /// reached before 51 consecutive failures.
    /// </summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task SustainedDecodeFailures_EndThePublish()
    {
        var (pipeline, harness) = await DecodeFailuresAsync(good: 600, schedule: static i => true);
        Assert.InRange(pipeline.AudioStage!.Recreations, 44, 47);
        harness.Dispose();
    }

    /// <summary>Isolated decode failures above 10 % of the units in 10 s end the publish, although none are consecutive.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FrequentDecodeFailures_EndThePublish()
    {
        var (pipeline, harness) = await DecodeFailuresAsync(good: 0, schedule: static i => i % 5 == 0);
        var recreations = pipeline.AudioStage!.Recreations;
        Assert.InRange(recreations, 40, ManagedAudioStage.MaxConsecutiveDecodeFailures);
        harness.Dispose();
    }

    /// <summary>Isolated decode failures at 5 % of units are concealed and never end the publish.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task OccasionalDecodeFailures_AreConcealed()
    {
        using var harness = Managed(static o => o.OverloadPolicy = TranscodingOverloadPolicy.Backpressure);
        var (config, units) = Fixture("aac-lc-48k-stereo.aac", 1000);
        var garbage = Undecodable(config);
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await SendAudioAsync(harness, session, config, [.. units.Select((u, i) => i % 20 == 10 ? garbage : u)], 48000);
        await harness.EndAsync(session);

        Assert.Null(pipeline.Fault);
        Assert.Equal(50, pipeline.AudioStage!.Recreations);
        AssertCanonicalAudio(harness, session, units.Count + 1);
        AssertReleased(harness);
    }

    /// <summary>Creates a managed harness, sends good frames and then frames that fail by a schedule until the publish fails.</summary>
    /// <param name="good">Good frames sent first.</param>
    /// <param name="schedule">Whether the frame at an index after the good ones is undecodable.</param>
    /// <returns>The failed pipeline and the harness, whose session has ended.</returns>
    private static async Task<(TranscodingSession Pipeline, TranscodingHarness Harness)> DecodeFailuresAsync(int good, Func<int, bool> schedule)
    {
        var harness = Managed(static o => o.OverloadPolicy = TranscodingOverloadPolicy.Backpressure);
        var (config, units) = Fixture("aac-lc-48k-stereo.aac", good + 1000);
        var garbage = Undecodable(config);
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader(config));
        var failed = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < units.Count && pipeline.Fault is null; i++)
            {
                var payload = i >= good && schedule(i - good) ? garbage : units[i];
                await harness.SendAsync(session, PublishedMessageKind.Audio, (uint)(i * 1024L * 1000 / 48000), FlvPackets.AacRaw(payload.ToArray()));
            }
        });

        await Eventually.WaitUntilAsync(() => pipeline.Fault is not null, "the publish fails");
        Assert.True(failed is null or TranscodingFailedException, failed?.ToString());
        await Assert.ThrowsAsync<TranscodingFailedException>(() => harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacRaw(units[0].ToArray())));
        var failure = Assert.IsType<TranscodingFailedException>(pipeline.Fault);
        Assert.Equal(TranscodingStage.Decoder, failure.Stage);
        Assert.Contains(10001, harness.EventIds());
        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        AssertReleased(harness);
        return (pipeline, harness);
    }

    /// <summary>A failing downstream stage ends a managed-audio publish.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DownstreamFailure_EndsThePublish()
    {
        using var harness = Managed();
        harness.Downstream.FailMessages = true;
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        var failed = await Record.ExceptionAsync(() => SendAudioAsync(harness, session, config, units, 48000, 0, header: true));
        await Eventually.WaitUntilAsync(() => pipeline.Fault is not null, "the downstream failure ends the publish");
        await Assert.ThrowsAnyAsync<Exception>(() => harness.SendAsync(session, PublishedMessageKind.Audio, 10_000, FlvPackets.AacRaw(units[0].ToArray())));

        Assert.True(failed is null or TranscodingFailedException, failed?.ToString());
        Assert.NotNull(pipeline.Fault);
        await harness.EndAsync(session, PublishEndReason.ConnectionError);
        AssertReleased(harness);
    }

    /// <summary>A drain whose downstream never accepts output is cut off by the drain timeout without hanging or leaking.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task DrainTimeout_CancelsTheFlush()
    {
        using var harness = Managed(static o => o.DrainTimeout = TimeSpan.FromSeconds(1));
        harness.Downstream.MessageGate = new TaskCompletionSource();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        await SendAudioAsync(harness, session, config, units, 48000);
        await harness.EndAsync(session);
        harness.Downstream.MessageGate.TrySetResult();
        AssertReleased(harness);
    }

    /// <summary>A replacement publish (a new incarnation) starts its own managed stage and timeline.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task StreamReplacement_StartsANewStage()
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var first = await harness.StartAsync();
        await SendAudioAsync(harness, first, config, units, 48000);
        await harness.EndAsync(first, PublishEndReason.ConnectionError);
        var second = await harness.StartAsync();
        await SendAudioAsync(harness, second, config, units, 48000, 5000);
        await harness.EndAsync(second);

        AssertCanonicalAudio(harness, second, units.Count + 1);
        Assert.Equal(0, harness.Factory.Starts);
        AssertReleased(harness);
    }

    /// <summary>Creates a harness configured for the managed engine.</summary>
    /// <param name="configure">Further option changes.</param>
    /// <param name="media">Media options.</param>
    /// <returns>The harness.</returns>
    private static TranscodingHarness Managed(Action<TranscodingOptions>? configure = null, MediaOptions? media = null) =>
        new(o =>
        {
            o.Audio.Engine = TranscodingAudioEngine.ManagedAacLc;
            configure?.Invoke(o);
        }, media);

    /// <summary>Reads an ADTS fixture.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="count">The access units to return, repeating the file's units in order.</param>
    /// <returns>Its AudioSpecificConfig and raw access units.</returns>
    private static (byte[] Config, List<ReadOnlyMemory<byte>> Units) Fixture(string name, int count = 200)
    {
        var units = AdtsFraming.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", "Codecs", "Fixtures", name)), out var config);
        return (config, [.. Enumerable.Range(0, count).Select(i => units[i % units.Count])]);
    }

    /// <summary>Sends an AAC sequence header and access units at their nominal millisecond timestamps.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <param name="units">The access units.</param>
    /// <param name="rate">The sample rate.</param>
    /// <param name="start">The first timestamp.</param>
    /// <param name="header">Whether to send the sequence header first.</param>
    /// <returns>A task.</returns>
    private static async Task SendAudioAsync(TranscodingHarness harness, PublisherSession session, byte[] config, List<ReadOnlyMemory<byte>> units, int rate, uint start = 0, bool header = true)
    {
        if (header)
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, start, FlvPackets.AacSequenceHeader(config));
        }

        for (var i = 0; i < units.Count; i++)
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, unchecked(start + (uint)(i * 1024L * 1000 / rate)), FlvPackets.AacRaw(units[i].ToArray()));
        }
    }

    /// <summary>Runs the 48 kHz stereo fixture through an audio-only managed publish with rewritten timestamps.</summary>
    /// <param name="schedule">Maps the access units to (timestamp, unit) pairs.</param>
    /// <returns>The finished stage.</returns>
    private static async Task<ManagedAudioStage> RunAudioAsync(Func<List<ReadOnlyMemory<byte>>, List<(uint Timestamp, ReadOnlyMemory<byte> Unit)>> schedule)
    {
        using var harness = Managed();
        var (config, units) = Fixture("aac-lc-48k-stereo.aac");
        var session = await harness.StartAsync();
        var pipeline = harness.Handler.FindSession(session.SessionId)!;
        await harness.SendAsync(session, PublishedMessageKind.Audio, 0, FlvPackets.AacSequenceHeader(config));
        foreach (var (timestamp, unit) in schedule(units))
        {
            await harness.SendAsync(session, PublishedMessageKind.Audio, timestamp, FlvPackets.AacRaw(unit.ToArray()));
        }

        await harness.EndAsync(session);
        var stage = pipeline.AudioStage!;
        Assert.Equal(units.Count, stage.DecodedUnits);
        AssertTimelineIsContinuous(harness, session);
        AssertReleased(harness);
        return stage;
    }

    /// <summary>Schedules 48 kHz units at nominal timestamps, shifting every unit from an index on.</summary>
    /// <param name="units">The units.</param>
    /// <param name="from">The first shifted index.</param>
    /// <param name="shift">The shift in milliseconds.</param>
    /// <returns>The schedule.</returns>
    private static List<(uint Timestamp, ReadOnlyMemory<byte> Unit)> Shifted(List<ReadOnlyMemory<byte>> units, int from, int shift) =>
        [.. units.Select((u, i) => ((uint)((i * 1024L * 1000 / 48000) + (i >= from ? shift + 1000 : 1000)), u))];

    /// <summary>Finds a payload the managed decoder rejects for a configuration.</summary>
    /// <param name="config">The AudioSpecificConfig.</param>
    /// <returns>The payload.</returns>
    private static ReadOnlyMemory<byte> Undecodable(byte[] config)
    {
        var random = new Random(1234);
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var payload = new byte[64];
            random.NextBytes(payload);
            using var decoder = AacDecoder.Create(config);
            try
            {
                decoder.Decode(new EncodedPacket(payload, 0, new MediaTimeBase(1, 48000))).Dispose();
            }
            catch (CodecException)
            {
                return payload;
            }
        }

        throw new InvalidOperationException("No undecodable payload was found.");
    }

    /// <summary>The source audio sample rate of a fixture's sequence header.</summary>
    /// <param name="tags">The fixture tags.</param>
    /// <returns>The rate.</returns>
    private static int SourceRate(IEnumerable<Media.FlvTag> tags)
    {
        var header = tags.First(static t => t.Type == 8 && t.Body.Length > 2 && t.Body[1] == 0);
        using var decoder = AacDecoder.Create(header.Body.AsSpan(2));
        return decoder.SampleRate;
    }

    /// <summary>The canonical units an N-unit source stream produces once drained.</summary>
    /// <param name="units">Source units of 1024 samples.</param>
    /// <param name="rate">The source rate.</param>
    /// <returns>The canonical units, priming included.</returns>
    private static int ExpectedUnits(int units, int rate)
    {
        var canonical = (long)Math.Ceiling((units * 1024.0 * 48000 / rate) - 1e-9);
        return (int)((canonical + 1023) / 1024) + 1;
    }

    /// <summary>The delivered canonical audio samples (packet type 1) of a session.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    /// <returns>The messages.</returns>
    private static List<HandlerEvent> Samples(TranscodingHarness harness, PublisherSession session) =>
        [.. harness.Delivered(session, PublishedMessageKind.Audio).Where(static e => e.Payload is [0xAF, 0x01, ..])];

    /// <summary>
    /// Asserts the delivered audio: the canonical sequence header first and once, then the expected number of FLV AAC
    /// raw units that the managed decoder decodes as 48 kHz stereo, on non-decreasing timestamps.
    /// </summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    /// <param name="expected">The expected sample units.</param>
    private static void AssertCanonicalAudio(TranscodingHarness harness, PublisherSession session, int expected)
    {
        var audio = harness.Delivered(session, PublishedMessageKind.Audio);
        Assert.Equal(CanonicalHeader, audio[0].Payload);
        Assert.Single(audio, static e => e.Payload is [0xAF, 0x00, ..]);
        var samples = Samples(harness, session);
        Assert.Equal(expected, samples.Count);
        using var decoder = AacDecoder.Create([0x11, 0x90]);
        Assert.Equal(48000, decoder.SampleRate);
        Assert.Equal(2, decoder.Layout.Length);
        foreach (var sample in samples)
        {
            decoder.Decode(new EncodedPacket(sample.Payload.AsMemory(2), 0, new MediaTimeBase(1, 48000))).Dispose();
        }

        AssertTimelineIsContinuous(harness, session);
    }

    /// <summary>Asserts that consecutive delivered audio units within one output stream are 21 or 22 ms apart.</summary>
    /// <param name="harness">The harness.</param>
    /// <param name="session">The session.</param>
    private static void AssertTimelineIsContinuous(TranscodingHarness harness, PublisherSession session)
    {
        var samples = Samples(harness, session);
        for (var i = 1; i < samples.Count; i++)
        {
            var step = unchecked((int)(samples[i].Timestamp - samples[i - 1].Timestamp));
            Assert.True(step is 21 or 22 || step > 1000 || step < 0, $"Audio unit {i} is {step} ms after the previous one.");
        }
    }

    /// <summary>Asserts that slots, queues and pooled payloads were released.</summary>
    /// <param name="harness">The harness.</param>
    private static void AssertReleased(TranscodingHarness harness)
    {
        Assert.Equal(0, harness.Handler.ActiveSessionCount);
        Assert.Equal(0, harness.Handler.BufferedBytes);
        Assert.Equal(0, harness.Pool.Outstanding);
    }
}
