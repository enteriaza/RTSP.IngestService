// Interoperability of RTP output with FFmpeg as the receiver, through the production host: FFmpeg publishes H.264 and
// AAC over RTMP, the service sends RTP to loopback ports, the management listener reports the output and serves its
// SDP, ffprobe identifies both streams from that SDP and FFmpeg receives and decodes them. The receiver decodes rather
// than stream-copies: RTP carries no decode timestamps, so FFmpeg cannot remux H.264 with B-frames into a container.
// Skipped when FFmpeg is not installed (unless RTSP_FFMPEG_REQUIRED is set).

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using RTSP.IngestService.Http;
using RTSP.IngestService.Tests.EndToEnd;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>FFmpeg receiver tests of RTP output.</summary>
public sealed class RtpFfmpegInteropTests
{
    /// <summary>
    /// Verifies the end-to-end path: the RTP inventory reports the active output, the SDP describes H.264 and AAC,
    /// ffprobe finds H.264 320x240 and AAC 48 kHz stereo with it, FFmpeg decodes 3 s of both at their frame rates, HLS
    /// is still produced for the same publication, and the session is gone after the publish ends.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task FfmpegReceivesUnicastRtpUsingTheServedSdp()
    {
        var tools = HlsFfmpegInteropTests.Tools();
        var ports = UdpReceiver.FreePortPairs(2);
        var (videoPort, audioPort) = (ports[0], ports[1]);

        var settings = EndToEndHost.Settings();
        settings["Rtp:Outputs:0:Name"] = "ffmpeg-rx";
        settings["Rtp:Outputs:0:Application"] = "live";
        settings["Rtp:Outputs:0:Stream"] = "camera1";
        settings["Rtp:Outputs:0:Destination"] = "127.0.0.1";
        settings["Rtp:Outputs:0:VideoPort"] = videoPort.ToString(CultureInfo.InvariantCulture);
        settings["Rtp:Outputs:0:AudioPort"] = audioPort.ToString(CultureInfo.InvariantCulture);
        var logs = new FakeLogCollector();
        using var host = EndToEndHost.Build(logs, settings);
        using var client = new HttpClient();
        var directory = Directory.CreateTempSubdirectory("rtp-ffmpeg-");
        await host.StartAsync(TestContext.Current.CancellationToken).WithinSafetyTimeout();
        try
        {
            var http = host.Services.GetRequiredService<HlsHttpOrigin>().LocalEndPoint!.Port;
            Uri Url(string path) => new($"http://127.0.0.1:{http.ToString(CultureInfo.InvariantCulture)}{path}");
            await using var publisher = new FfmpegPublisher(tools.Ffmpeg, EndToEndHost.RtmpPort(host), EndToEndHost.Key(1), seconds: 10);

            string? sdp = null;
            await Eventually.WaitUntilAsync(
                () =>
                {
                    using var response = client.GetAsync(Url("/health/rtp/ffmpeg-rx.sdp"), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                    var body = response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                    if (response.StatusCode == HttpStatusCode.OK && body.Contains("m=video", StringComparison.Ordinal) && body.Contains("m=audio", StringComparison.Ordinal) && body.Contains("a=ssrc", StringComparison.Ordinal))
                    {
                        Assert.Equal("application/sdp", response.Content.Headers.ContentType!.MediaType);
                        sdp = body;
                        return true;
                    }

                    return false;
                },
                "the SDP describes both tracks");
            Assert.Contains("a=rtpmap:96 H264/90000\r\n", sdp, StringComparison.Ordinal);
            Assert.Contains("a=rtpmap:97 MPEG4-GENERIC/48000/2\r\n", sdp, StringComparison.Ordinal);
            var sdpFile = Path.Combine(directory.FullName, "stream.sdp");
            await File.WriteAllTextAsync(sdpFile, sdp, TestContext.Current.CancellationToken);

            var probe = await HlsFfmpegInteropTests.RunAsync(
                tools.Probe,
                "-v", "error", "-protocol_whitelist", "file,udp,rtp",
                "-show_entries", "stream=codec_type,codec_name,width,height,sample_rate,channels",
                "-of", "json", sdpFile);
            Assert.True(probe.Code == 0, $"ffprobe exited with {probe.Code}: {probe.Errors}");
            using (var json = JsonDocument.Parse(probe.Output))
            {
                var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToDictionary(static s => s.GetProperty("codec_type").GetString()!);
                Assert.Equal(("h264", 320, 240), (streams["video"].GetProperty("codec_name").GetString(), streams["video"].GetProperty("width").GetInt32(), streams["video"].GetProperty("height").GetInt32()));
                Assert.Equal(("aac", "48000", 2), (streams["audio"].GetProperty("codec_name").GetString(), streams["audio"].GetProperty("sample_rate").GetString(), streams["audio"].GetProperty("channels").GetInt32()));
            }

            var frames = Path.Combine(directory.FullName, "frames.txt");
            var received = await HlsFfmpegInteropTests.RunAsync(
                tools.Ffmpeg,
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-protocol_whitelist", "file,udp,rtp",
                "-i", sdpFile,
                "-t", "3", "-f", "framemd5", "-y", frames);
            Assert.True(received.Code == 0, $"FFmpeg receiver exited with {received.Code}: {received.Errors}");
            var decoded = File.ReadLines(frames).Where(static l => !l.StartsWith('#')).GroupBy(static l => l[..l.IndexOf(',', StringComparison.Ordinal)]).ToDictionary(static g => g.Key, static g => g.Count());
            Assert.InRange(decoded["0"], 40, 100);
            Assert.InRange(decoded["1"], 100, 200);

            using (var inventory = await client.GetAsync(Url("/health/rtp"), TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
                using var json = JsonDocument.Parse(await inventory.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                var output = json.RootElement.GetProperty("outputs")[0];
                Assert.Equal(("ffmpeg-rx", "unicast", "127.0.0.1"), (output.GetProperty("name").GetString(), output.GetProperty("mode").GetString(), output.GetProperty("destination").GetString()));
                var session = output.GetProperty("session");
                Assert.Equal("active", session.GetProperty("state").GetString());
                Assert.True(session.GetProperty("packetsSent").GetInt64() > 100);
                Assert.Equal(0, session.GetProperty("sendErrors").GetInt64());
            }

            var (code, errors) = await publisher.WaitAsync();
            Assert.True(code == 0, $"FFmpeg publisher exited with {code}: {errors}");
            await Eventually.WaitUntilAsync(
                () =>
                {
                    using var response = client.GetAsync(Url("/health/rtp/ffmpeg-rx.sdp"), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                    return response.StatusCode == HttpStatusCode.ServiceUnavailable;
                },
                "the output has no session after the publish ends");
            using var playlist = await client.GetAsync(Url("/hls/live/camera1/index.m3u8"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
            Assert.Contains(logs.GetSnapshot(), static r => r.Id.Id == 9401);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            directory.Delete(recursive: true);
        }
    }
}
