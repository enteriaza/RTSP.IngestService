// Interoperability of the managed H.264 encoder with FFmpeg. FFmpeg's decoder (single-threaded) must output exactly
// the encoder's reconstruction for Annex B and for avcC/length-prefixed streams carried in FLV, and ffprobe must
// report the canonical stream description: H.264 High, yuv420p, progressive, BT.709 limited range, no B frames, the
// signalled level, I and P pictures only, IDR pictures where the encoder reports keyframes and the encoder's
// timestamps. All requirements were fixed before the first run. The tests skip when FFmpeg is unavailable (fail
// when RTSP_FFMPEG_REQUIRED=true).

using System.Globalization;
using System.Text.Json;
using RTSP.IngestService.Media.Codecs.Video.H264.Encoder;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Media.Codecs.H264;

/// <summary>The managed encoder's output decodes identically in FFmpeg and probes as canonical.</summary>
public sealed class H264EncoderFfmpegInteropTests
{
    /// <summary>Gets the stream variations: pattern, size, QP, slices, references, frames.</summary>
    public static TheoryData<SyntheticPattern, int, int, int, int, int, int> Streams => new()
    {
        { SyntheticPattern.Motion, 176, 144, 26, 1, 1, 40 },
        { SyntheticPattern.Detail, 160, 96, 18, 2, 2, 20 },
        { SyntheticPattern.Noise, 96, 64, 8, 1, 1, 10 },
        { SyntheticPattern.Cuts, 198, 110, 32, 3, 3, 40 },
        { SyntheticPattern.Gradient, 128, 96, 44, 1, 2, 30 },
        { SyntheticPattern.Bars, 50, 34, 51, 1, 1, 10 },
        { SyntheticPattern.Detail, 64, 48, 0, 1, 1, 6 },
    };

    /// <summary>FFmpeg decodes the Annex B stream to exactly the encoder's reconstruction.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="qp">The QP.</param>
    /// <param name="slices">Slices per picture.</param>
    /// <param name="references">Reference frames.</param>
    /// <param name="count">Frames.</param>
    /// <returns>A task.</returns>
    [Theory]
    [MemberData(nameof(Streams))]
    public async Task FfmpegDecodesAnnexBExactly(SyntheticPattern pattern, int width, int height, int qp, int slices, int references, int count)
    {
        var ffmpeg = FfmpegTool.Require();
        var options = H264EncoderTestSupport.Options(width, height, qp) with
        {
            Format = H264OutputFormat.AnnexB,
            SlicesPerPicture = slices,
            MaxReferenceFrames = references,
            IntraQpOffset = 0,
            KeyframeInterval = TimeSpan.FromSeconds(1),
        };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, pattern, count);
        await AssertFfmpegMatchesAsync(ffmpeg, frames, "h264", H264EncoderTestSupport.Concatenate(frames), "h264");
    }

    /// <summary>FFmpeg decodes the avcC/length-prefixed output carried in FLV to exactly the encoder's reconstruction.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FfmpegDecodesAvcCInFlvExactly()
    {
        var ffmpeg = FfmpegTool.Require();
        var options = H264EncoderTestSupport.Options(176, 144) with { MaxReferenceFrames = 2, KeyframeInterval = TimeSpan.FromSeconds(1) };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Cuts, 50);
        await AssertFfmpegMatchesAsync(ffmpeg, frames, "flv", H264EncoderTestSupport.BuildFlv(encoder.DecoderConfigurationRecord.Span, frames), "flv");
    }

    /// <summary>Forced I_PCM macroblocks in P pictures decode identically in FFmpeg.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FfmpegDecodesPcmExactly()
    {
        var ffmpeg = FfmpegTool.Require();
        var options = H264EncoderTestSupport.Options(96, 64) with { Format = H264OutputFormat.AnnexB };
        var hooks = new H264EncoderTestHooks { Control = static i => new H264PictureControl { ForcePcm = mb => ((mb + i) % 5) == 0 } };
        using var encoder = new H264Encoder(options, hooks);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 8);
        Assert.All(frames, static f => Assert.True(f.Statistics.PcmMacroblocks > 0));
        await AssertFfmpegMatchesAsync(ffmpeg, frames, "h264", H264EncoderTestSupport.Concatenate(frames), "h264");
    }

    /// <summary>ffprobe reports the canonical stream description and the encoder's picture types and timestamps.</summary>
    /// <returns>A task.</returns>
    [Fact]
    public async Task FfprobeReportsCanonicalStream()
    {
        FfmpegTool.Require(probe: true);
        var probe = FfmpegTool.ProbePath!;
        var options = H264EncoderTestSupport.Options(320, 180) with { KeyframeInterval = TimeSpan.FromSeconds(1), MaxBitrateKilobits = 8000 };
        using var encoder = new H264Encoder(options);
        var frames = H264EncoderTestSupport.EncodeAll(encoder, SyntheticPattern.Motion, 60);
        var directory = Directory.CreateTempSubdirectory("h264-encoder-probe-");
        try
        {
            var path = Path.Combine(directory.FullName, "probe.flv");
            await File.WriteAllBytesAsync(path, H264EncoderTestSupport.BuildFlv(encoder.DecoderConfigurationRecord.Span, frames), TestContext.Current.CancellationToken);
            var json = await H264EncoderTestSupport.RunAsync(probe, ["-v", "error", "-select_streams", "v:0", "-show_streams", "-show_frames", "-of", "json", path]);
            using var document = JsonDocument.Parse(json);
            var stream = document.RootElement.GetProperty("streams")[0];
            Assert.Equal("h264", stream.GetProperty("codec_name").GetString());
            Assert.Equal("High", stream.GetProperty("profile").GetString());
            Assert.Equal(encoder.LevelIdc, stream.GetProperty("level").GetInt32());
            Assert.Equal(320, stream.GetProperty("width").GetInt32());
            Assert.Equal(180, stream.GetProperty("height").GetInt32());
            Assert.Equal("yuv420p", stream.GetProperty("pix_fmt").GetString());
            Assert.Equal("tv", stream.GetProperty("color_range").GetString());
            Assert.Equal("bt709", stream.GetProperty("color_space").GetString());
            Assert.Equal("bt709", stream.GetProperty("color_transfer").GetString());
            Assert.Equal("bt709", stream.GetProperty("color_primaries").GetString());
            Assert.Equal(0, stream.GetProperty("has_b_frames").GetInt32());
            if (stream.TryGetProperty("field_order", out var order))
            {
                Assert.Equal("progressive", order.GetString());
            }

            var probed = document.RootElement.GetProperty("frames").EnumerateArray().ToList();
            Assert.Equal(frames.Count, probed.Count);
            for (var i = 0; i < frames.Count; i++)
            {
                var type = probed[i].GetProperty("pict_type").GetString();
                Assert.Equal(frames[i].IsKeyframe ? "I" : "P", type);
                Assert.Equal(frames[i].IsKeyframe ? 1 : 0, probed[i].GetProperty("key_frame").GetInt32());
                Assert.Equal(frames[i].Timestamp / 90, probed[i].GetProperty("pts").GetInt64());
            }

            Assert.Equal([0, 25, 50], frames.Select(static (f, i) => (f, i)).Where(static x => x.f.IsKeyframe).Select(static x => x.i));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>Decodes a file with FFmpeg and compares every frame with the encoder's reconstruction.</summary>
    /// <param name="ffmpeg">The FFmpeg executable.</param>
    /// <param name="frames">The encoded frames.</param>
    /// <param name="extension">The file extension.</param>
    /// <param name="data">The file bytes.</param>
    /// <param name="format">The demuxer.</param>
    /// <returns>A task.</returns>
    private static async Task AssertFfmpegMatchesAsync(string ffmpeg, List<EncodedFrame> frames, string extension, byte[] data, string format)
    {
        var directory = Directory.CreateTempSubdirectory("h264-encoder-ffmpeg-");
        try
        {
            var input = Path.Combine(directory.FullName, $"stream.{extension}");
            var raw = Path.Combine(directory.FullName, "stream.yuv");
            await File.WriteAllBytesAsync(input, data, TestContext.Current.CancellationToken);
            await H264EncoderTestSupport.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-f", format, "-i", input, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "yuv420p", raw]);
            var reference = await File.ReadAllBytesAsync(raw, TestContext.Current.CancellationToken);
            var size = frames[0].Reconstruction.Length;
            Assert.Equal(frames.Count * size, reference.Length);
            for (var i = 0; i < frames.Count; i++)
            {
                var actual = reference.AsSpan(i * size, size);
                if (!actual.SequenceEqual(frames[i].Reconstruction))
                {
                    Assert.Fail(string.Create(CultureInfo.InvariantCulture, $"Frame {i}: FFmpeg differs from the encoder reconstruction at byte {actual.CommonPrefixLength(frames[i].Reconstruction)} of {size}."));
                }
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
