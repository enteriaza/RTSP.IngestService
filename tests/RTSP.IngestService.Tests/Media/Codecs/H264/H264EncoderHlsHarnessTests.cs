// Test-only HLS preparation harness for the managed H.264 encoder. Production video stays on FFmpeg; this checks
// that the encoder's output already fits the service's packaging path unchanged: access units become FLV AVC tag
// bodies (avcC sequence header, then length-prefixed NAL units with composition time 0) submitted on the 1/90000
// timeline to the real MediaMultiplexer, which delivers into the real media stage and HlsPackager. The packaged
// presentation must read as H.264 High in ffprobe, cut a segment at every IDR picture of the keyframe interval,
// decode in FFmpeg to exactly the encoder's reconstruction, keep the packaged A/V timing with fixture AAC audio,
// and return every buffer. Requirements were fixed before the first run. Skips without FFmpeg unless required.

using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;
using RTSP.IngestService.Rtmp.Protocol;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Transcoding;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>The managed encoder's output through the multiplexer, media stage and HLS packager.</summary>
public sealed class H264EncoderHlsHarnessTests
{
    /// <summary>Video only: segments at every IDR, canonical stream description, bit-exact decode of the packaged presentation.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task VideoOnly_PackagesIntoExactHls()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        using var run = new HarnessRun();
        var frames = await run.PublishAsync(seconds: 8, audio: null);

        var playlist = run.Hls.Playlist();
        Assert.NotNull(playlist);
        Assert.True(playlist.Ended);
        Assert.Equal(4, playlist.Segments.Count);
        Assert.All(playlist.Segments.SkipLast(1), static s => Assert.Equal(TimeSpan.FromSeconds(2), s.Duration));

        using var directory = new HlsFfmpegInteropTests.TemporaryDirectory();
        var path = directory.Write(run.Hls.Store, "live", "camera1", playlist);
        var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, path);
        var video = Assert.Single(probe.Streams);
        Assert.Equal(("video", "h264", 320, 180, frames.Count), (video.CodecType, video.CodecName, video.Width, video.Height, video.Frames));
        var profile = await HlsFfmpegInteropTests.RunAsync(tools.Probe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=profile,pix_fmt,color_space,color_range", "-of", "csv=p=0", path);
        Assert.All(
            profile.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            static line => Assert.Equal("High,yuv420p,tv,bt709", line));
        var keys = await HlsFfmpegInteropTests.RunAsync(tools.Probe, "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=key_frame,pts_time", "-of", "csv=p=0", path);
        var keyTimes = keys.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static l => l.Split(','))
            .Where(static p => p[0] == "1")
            .Select(static p => double.Parse(p[1], CultureInfo.InvariantCulture))
            .ToList();
        var first = keyTimes[0];
        Assert.Equal([0.0, 2.0, 4.0, 6.0], keyTimes.Select(t => Math.Round(t - first, 3)));

        var raw = Path.Combine(Path.GetDirectoryName(path)!, "decoded.yuv");
        var decode = await HlsFfmpegInteropTests.RunAsync(tools.Ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-xerror", "-threads", "1", "-i", path, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "yuv420p", raw);
        Assert.True(decode.Code == 0 && decode.Errors.Length == 0, decode.Errors);
        var decoded = await File.ReadAllBytesAsync(raw, TestContext.Current.CancellationToken);
        var size = frames[0].Reconstruction.Length;
        Assert.Equal(frames.Count * size, decoded.Length);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.True(decoded.AsSpan(i * size, size).SequenceEqual(frames[i].Reconstruction), $"Frame {i} of the packaged presentation differs from the encoder reconstruction.");
        }
    }

    /// <summary>With fixture AAC audio interleaved, the presentation keeps contiguous, exactly packaged A/V timing and decodes cleanly.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task WithAudio_KeepsPackagedTiming()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var audio = HlsFixture.Tags.Where(static t => t.Type == 8).ToList();
        var config = HlsFixture.AacSequenceHeader;
        ReadOnlySpan<int> rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        var sampleRate = rates[((config[2] & 0x07) << 1) | (config[3] >> 7)];
        var seconds = (int)(audio.Max(static t => t.Timestamp) / 1000);

        using var run = new HarnessRun();
        await run.PublishAsync(seconds, audio);
        var playlist = run.Hls.Playlist();
        Assert.NotNull(playlist);
        Assert.True(playlist.Ended);
        using var directory = new HlsFfmpegInteropTests.TemporaryDirectory();
        var path = directory.Write(run.Hls.Store, "live", "camera1", playlist);
        var probe = await HlsFfmpegInteropTests.ProbeAsync(tools.Probe, path);
        Assert.Equal(["audio", "video"], probe.Streams.Select(static s => s.CodecType).Order());
        await HlsFfmpegInteropTests.AssertTimingAsync(tools.Probe, path, run.Hls.Store, "camera1", playlist, sampleRate);
        await HlsFfmpegInteropTests.DecodeAsync(tools.Ffmpeg, path);
    }

    /// <summary>One publish through the multiplexer into the media and HLS stages.</summary>
    private sealed class HarnessRun : IDisposable
    {
        /// <summary>Initializes a new instance of the <see cref="HarnessRun"/> class.</summary>
        public HarnessRun()
        {
            Hls = new HlsHarness(new HlsOptions { TargetSegmentDuration = TimeSpan.FromSeconds(2), MaxSegmentDuration = TimeSpan.FromSeconds(4) });
        }

        /// <summary>Gets the media and HLS stages.</summary>
        public HlsHarness Hls { get; }

        /// <summary>Encodes and publishes a motion sequence, optionally interleaving FLV audio tags, then ends the publish.</summary>
        /// <param name="seconds">The video duration at 25 frames per second.</param>
        /// <param name="audio">Audio tags on the millisecond timeline, or null for video only.</param>
        /// <returns>The encoded frames.</returns>
        public async Task<List<EncodedFrame>> PublishAsync(int seconds, List<FlvTag>? audio)
        {
            var token = TestContext.Current.CancellationToken;
            var session = await Hls.StartAsync();
            var faults = new List<Exception>();
            var budget = new TranscodingBufferBudget(1L << 30);
            await using var mux = new MediaMultiplexer(
                session,
                Hls.Processor,
                MultiplexerLimits.FromOptions(new TranscodingOptions()),
                budget,
                new TranscodingMetrics(Hls.MeterFactory),
                NullLogger.Instance,
                TimeProvider.System,
                faults.Add,
                token);
            mux.Video.Declare(true);
            mux.Audio.Declare(audio is not null);
            mux.Start();

            var options = H264EncoderTestSupport.Options(320, 180) with { MaxBitrateKilobits = 8000 };
            using var encoder = new H264Encoder(options);
            await mux.Video.SubmitAsync(Unit(EncodedUnitKind.SequenceStart, 0, MediaTimeBase.Milliseconds, true, [0x17, 0, 0, 0, 0, .. encoder.DecoderConfigurationRecord.Span]), token);
            var audioIndex = 0;
            var frames = new List<EncodedFrame>();
            var units = new List<H264EncodedAccessUnit>();
            for (var i = 0; i < seconds * 25; i++)
            {
                using (var frame = H264EncoderTestSupport.CreateFrame(SyntheticPattern.Motion, options.Width, options.Height, i))
                {
                    encoder.Encode(frame, units, cancellationToken: token);
                }

                var encoded = H264EncoderTestSupport.Take(encoder, units);
                frames.Add(encoded);
                while (audio is not null && audioIndex < audio.Count && audio[audioIndex].Timestamp * 90L <= encoded.Timestamp)
                {
                    await SubmitAudioAsync(mux, audio[audioIndex++], token);
                }

                await mux.Video.SubmitAsync(Unit(EncodedUnitKind.Sample, encoded.Timestamp, H264Encoder.TimeBase, encoded.IsKeyframe, [encoded.IsKeyframe ? (byte)0x17 : (byte)0x27, 1, 0, 0, 0, .. encoded.Data]), token);
            }

            while (audio is not null && audioIndex < audio.Count)
            {
                await SubmitAudioAsync(mux, audio[audioIndex++], token);
            }

            await mux.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(60), token);
            Assert.Null(mux.Fault);
            Assert.Empty(faults);
            await Hls.EndAsync(session);
            Assert.Equal(0, budget.Used);
            Assert.Equal(0, Hls.Pool.Outstanding);
            return frames;
        }

        /// <inheritdoc />
        public void Dispose() => Hls.Dispose();

        /// <summary>Submits one FLV audio tag.</summary>
        /// <param name="mux">The multiplexer.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="token">Cancellation.</param>
        /// <returns>A task.</returns>
        private ValueTask SubmitAudioAsync(MediaMultiplexer mux, FlvTag tag, CancellationToken token)
        {
            var kind = tag.Body.Length >= 2 && tag.Body[1] == 0 ? EncodedUnitKind.SequenceStart : EncodedUnitKind.Sample;
            return mux.Audio.SubmitAsync(Unit(kind, tag.Timestamp, MediaTimeBase.Milliseconds, true, tag.Body), token);
        }

        /// <summary>Creates a unit over a pooled copy of a payload.</summary>
        /// <param name="kind">The kind.</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <param name="timeBase">Its time base.</param>
        /// <param name="sync">Whether it is a sync point.</param>
        /// <param name="payload">The FLV tag body.</param>
        /// <returns>The unit.</returns>
        private EncodedAccessUnit Unit(EncodedUnitKind kind, long timestamp, MediaTimeBase timeBase, bool sync, byte[] payload)
        {
            var buffer = new PooledBuffer(Hls.Pool);
            buffer.EnsureCapacity(payload.Length);
            buffer.Append(new ReadOnlySequence<byte>(payload));
            return new EncodedAccessUnit(kind, timestamp, timeBase, sync, buffer);
        }
    }
}
