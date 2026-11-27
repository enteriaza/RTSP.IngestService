// Regression of the production transcoding path into HLS: fixture FLV files (H.264 Main with B-frames and AAC-LC)
// are published through the managed transcoding stage into the real media and HLS stages, with no FFmpeg anywhere.
// The canonical video reaching the media stage is decoded with the Phase 3 managed decoder and must hold every source
// picture at its source presentation time (minus pictures a back-to-back fixture overlaps), and the stored HLS
// presentation is decoded independently of the packager and must hold the same pictures, bit for bit by mean luma,
// on the same relative timeline, with audio starting at the first picture. Setting RTSP_TRANSCODING_HLS_DUMP to a
// directory writes each scenario's playlist and segments there, so HLS output can be compared across builds. FFmpeg,
// when present, adds an ffprobe and full-decode oracle.

using System.Security.Cryptography;
using System.Text;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Media.Codecs;
using RTSP.IngestService.Rtmp.Publishing;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Media;
using FlvTag = RTSP.IngestService.Tests.Media.FlvTag;

namespace RTSP.IngestService.Tests.Transcoding;

/// <summary>Regression of the managed transcoding output through the media and HLS stages.</summary>
public sealed class TranscodingHlsRegressionTests
{
    /// <summary>The environment variable naming the dump directory.</summary>
    private const string DumpVariable = "RTSP_TRANSCODING_HLS_DUMP";

    /// <summary>One AAC frame at 48 kHz plus 1 ms of rounding: the A/V alignment tolerance.</summary>
    private static readonly double AlignmentToleranceMilliseconds = (1024 * 1000.0 / 48_000) + 1;

    /// <summary>Gets the scenarios: a name, the fixtures published one after another, and whether video timing is made variable.</summary>
    public static TheoryData<string, string[], bool> Scenarios => new()
    {
        { "h264-aac", ["h264-aac.flv"], false },
        { "h264-aac-4s", ["h264-aac-4s.flv"], false },
        { "restart", ["h264-aac-4s.flv", "h264-aac.flv"], false },
        { "long", ["h264-aac-4s.flv", "h264-aac-4s.flv", "h264-aac-4s.flv"], false },
        { "vfr", ["h264-aac-4s.flv"], true },
    };

    /// <summary>
    /// Every source picture reaches the media stage as canonical H.264 at its presentation time, and the HLS
    /// presentation decodes to exactly those pictures with audio aligned to the first one.
    /// </summary>
    /// <param name="name">The scenario.</param>
    /// <param name="fixtures">The fixtures, published back to back on one timeline.</param>
    /// <param name="variable">Whether to stretch the video timeline irregularly (variable frame rate).</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ManagedOutput_ReachesHlsIntact(string name, string[] fixtures, bool variable)
    {
        using var run = new ManagedHlsRun();
        var session = await run.StartAsync();
        var inputs = fixtures.Select(f => variable ? VariableFrameRate(FlvFile.ReadFixture(f)) : FlvFile.ReadFixture(f)).ToArray();
        var sent = await run.PublishAsync(session, inputs);
        await run.EndAsync(session);

        run.AssertReleased();
        Assert.DoesNotContain(run.EventIds(), static id => id is 10001 or 10003 or 10011 or 10012);
        var expected = ExpectedPresentationTimes(inputs);
        var delivered = ManagedVideoTestSupport.Decode(run.Downstream.Events(session, PublishedMessageKind.Video));
        ManagedVideoTestSupport.AssertCanonical(delivered, 160, 120);
        Assert.Equal(expected, delivered.Pictures.Select(static p => p.Timestamp));
        Assert.NotEmpty(run.Downstream.Events(session, PublishedMessageKind.Audio));

        var (playlist, periods) = run.DecodeEnded();
        var pictures = periods.SelectMany(static p => p.Pictures).ToList();
        Assert.Equal(delivered.Pictures.Select(static p => p.MeanLuma), pictures.Select(static p => p.MeanLuma));
        Assert.All(pictures, static p => Assert.Equal((160, 120, VideoColorInfo.Bt709), (p.Width, p.Height, p.Color)));
        var index = 0;
        foreach (var period in periods)
        {
            Assert.NotNull(period.AvcRecord);
            Assert.NotEmpty(period.Pictures);
            Assert.True(period.Pictures[0].IsKeyframe, $"Period {period.Initialization} starts with an IDR picture.");
            var source = expected.Skip(index).Take(period.Pictures.Count).ToList();
            Assert.Equal(source.Select(t => t - source[0]), period.Pictures.Select(p => p.Timestamp - period.Pictures[0].Timestamp));
            index += period.Pictures.Count;
        }

        var first = periods[0];
        var sourceAudio = sent.First(static t => t.Type == 8 && t.Body.Length > 2 && t.Body[1] == 1).Timestamp;
        double expectedOffset = Math.Max(0, sourceAudio - expected[0]);
        Assert.InRange(first.AudioStarts[0] - first.Pictures[0].Timestamp, expectedOffset - 1, expectedOffset + AlignmentToleranceMilliseconds);
        Dump(name, run.Hls, playlist, run.Downstream.Messages);
        await run.OracleAsync(playlist, video: true);
    }

    /// <summary>
    /// The presentation times the canonical output must hold: each fixture's pictures in presentation order, shifted
    /// as the run shifts them, without pictures at or before the previous fixture's last (the stage drops overlap).
    /// </summary>
    /// <param name="fixtures">The fixtures as published.</param>
    /// <returns>The times in milliseconds.</returns>
    private static List<long> ExpectedPresentationTimes(IReadOnlyList<FlvTag>[] fixtures)
    {
        var expected = new List<long>();
        uint shift = 0;
        foreach (var tags in fixtures)
        {
            foreach (var pts in ManagedVideoTestSupport.SourcePresentationTimes(tags))
            {
                if (expected.Count == 0 || pts + shift > expected[^1])
                {
                    expected.Add(pts + shift);
                }
            }

            shift += tags.Max(static t => t.Timestamp) + 40;
        }

        return expected;
    }

    /// <summary>
    /// Stretches the video timeline irregularly: every decode and presentation time t becomes t + 20 * floor(t / 300),
    /// which is strictly increasing, so frame intervals vary between 100 and 120 ms and composition offsets stay valid.
    /// Audio keeps its timing.
    /// </summary>
    /// <param name="tags">The fixture.</param>
    /// <returns>The retimed fixture.</returns>
    private static List<FlvTag> VariableFrameRate(IReadOnlyList<FlvTag> tags)
    {
        static long Warp(long t) => t + (20 * (t / 300));
        var result = new List<FlvTag>();
        foreach (var tag in tags)
        {
            if (tag.Type != 9 || tag.Body.Length < 5 || tag.Body[1] != 1)
            {
                result.Add(tag.Type == 9 ? tag with { Timestamp = (uint)Warp(tag.Timestamp) } : tag);
                continue;
            }

            var cto = ((tag.Body[2] << 24) | (tag.Body[3] << 16) | (tag.Body[4] << 8)) >> 8;
            var dts = Warp(tag.Timestamp);
            var warped = (int)(Warp(tag.Timestamp + cto) - dts);
            var body = (byte[])tag.Body.Clone();
            body[2] = (byte)(warped >> 16);
            body[3] = (byte)(warped >> 8);
            body[4] = (byte)warped;
            result.Add(new FlvTag(9, (uint)dts, body));
        }

        return result;
    }

    /// <summary>Writes the playlist, segments and delivered message digest when <see cref="DumpVariable"/> is set.</summary>
    /// <param name="name">The scenario.</param>
    /// <param name="hls">The harness.</param>
    /// <param name="playlist">The playlist.</param>
    /// <param name="messages">Every delivered message.</param>
    private static void Dump(string name, HlsHarness hls, HlsPlaylistSnapshot playlist, IReadOnlyList<ManagedHlsRun.Delivered> messages)
    {
        var root = Environment.GetEnvironmentVariable(DumpVariable);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "playlist.m3u8"), playlist.Text.Replace(hls.Store.Generation, "GENERATION", StringComparison.Ordinal));
        var names = playlist.Segments.Select(static s => s.InitializationName).Distinct(StringComparer.Ordinal).Concat(playlist.Segments.Select(static s => s.Name));
        foreach (var segment in names)
        {
            File.WriteAllBytes(Path.Combine(directory, segment.Replace('/', '_')), hls.Segment(segment));
        }

        var digest = new StringBuilder();
        foreach (var message in messages)
        {
            digest.Append(message.Kind).Append(' ').Append(message.Timestamp).Append(' ').Append(message.Payload.Length).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(message.Payload))).Append('\n');
        }

        File.WriteAllText(Path.Combine(directory, "messages.txt"), digest.ToString());
    }
}
