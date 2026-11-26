// Tests of the RTP management routes: the output inventory with per-session statistics, the per-output SDP (200 when
// described, 503 before, 404 for unknown outputs and malformed paths), HEAD and method handling, loopback-only access,
// and that the routes do not exist without configured outputs.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Management;
using RTSP.IngestService.Tests.Infrastructure;
using RTSP.IngestService.Tests.Media;

namespace RTSP.IngestService.Tests.Rtp;

/// <summary>Tests for the RTP routes of <see cref="ManagementEndpoints"/>.</summary>
public sealed class RtpManagementTests
{
    /// <summary>Verifies the inventory and SDP before, during and after a publication.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Routes_FollowThePublication()
    {
        var socket = new FakeDatagramSocket();
        await using var harness = new RtpHarness((_, _) => socket, null, RtpTestData.Output("out", 5004, 5006), RtpTestData.Output("idle", 6000, null, "camera9"));
        var endpoints = Endpoints(harness);

        var (status, body, type) = await GetAsync(endpoints, "/health/rtp");
        Assert.Equal((200, "application/json; charset=utf-8"), (status, type));
        using (var json = JsonDocument.Parse(body))
        {
            var outputs = json.RootElement.GetProperty("outputs");
            Assert.Equal(2, outputs.GetArrayLength());
            Assert.Equal(("out", "live", "camera1", "unicast", "127.0.0.1", 5004, 5006), Describe(outputs[0]));
            Assert.False(outputs[0].TryGetProperty("session", out _));
            Assert.False(outputs[1].TryGetProperty("audioPort", out _));
        }

        var idle = await GetAsync(endpoints, "/health/rtp/out.sdp");
        Assert.Equal((503, "{\"status\":\"not_publishing\"}"), (idle.Status, idle.Body));

        var session = await harness.StartAsync();
        var undescribed = await GetAsync(endpoints, "/health/rtp/out.sdp");
        Assert.Equal((503, "{\"status\":\"not_described\"}"), (undescribed.Status, undescribed.Body));
        await harness.VideoAsync(session, 0, FlvPackets.AvcSequenceHeader());
        var (videoOnly, videoSdp, sdpType) = await GetAsync(endpoints, "/health/rtp/out.sdp");
        Assert.Equal((200, "application/sdp"), (videoOnly, sdpType));
        Assert.Contains("m=video 5004 RTP/AVP 96\r\n", videoSdp, StringComparison.Ordinal);
        Assert.DoesNotContain("m=audio", videoSdp, StringComparison.Ordinal);

        await harness.AudioAsync(session, 0, FlvPackets.AacSequenceHeader());
        await harness.FrameAsync(session, 0, keyframe: true, RtpTestData.Nal(5, 100));
        await Eventually.WaitUntilAsync(() => !socket.Sent.IsEmpty, "a packet is sent");
        var full = (await GetAsync(endpoints, "/health/rtp/out.sdp")).Body;
        Assert.StartsWith("v=0\r\no=- ", full, StringComparison.Ordinal);
        Assert.Contains("\r\ns=out\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\n", full, StringComparison.Ordinal);
        Assert.Contains("m=audio 5006 RTP/AVP 97\r\n", full, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", full, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", full.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        using (var json = JsonDocument.Parse((await GetAsync(endpoints, "/health/rtp")).Body))
        {
            var current = json.RootElement.GetProperty("outputs")[0].GetProperty("session");
            Assert.Equal("active", current.GetProperty("state").GetString());
            Assert.Matches("^[0-9a-f]{8}$", current.GetProperty("videoSsrc").GetString()!);
            Assert.True(current.GetProperty("packetsSent").GetInt64() >= 0);
            Assert.True(current.TryGetProperty("startedAt", out _));
            Assert.False(json.RootElement.GetProperty("outputs")[1].TryGetProperty("session", out _));
        }

        await harness.EndAsync(session);
        Assert.Equal(503, (await GetAsync(endpoints, "/health/rtp/out.sdp")).Status);
    }

    /// <summary>
    /// Verifies the inventory reports each output's resolved QoS policy, and each session the byte its socket reports;
    /// a disabled policy reports only that it is disabled.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Inventory_ReportsQos()
    {
        var socket = new FakeDatagramSocket { TrafficClass = 0x88 };
        var plain = RtpTestData.Output("plain", 6000, null);
        plain.Qos = new() { Enabled = false };
        await using var harness = new RtpHarness((_, _) => socket, null, new RtpTransportOptions { Qos = new() { Enabled = true, Dscp = 34 } }, RtpTestData.Output("marked", 5004, null), plain);
        var endpoints = Endpoints(harness);
        var session = await harness.StartAsync();

        using var json = JsonDocument.Parse((await GetAsync(endpoints, "/health/rtp")).Body);
        var outputs = json.RootElement.GetProperty("outputs");
        Assert.Equal("{\"enabled\":true,\"dscp\":34,\"ecn\":0,\"trafficClass\":136}", outputs[0].GetProperty("qos").GetRawText());
        Assert.Equal(136, outputs[0].GetProperty("session").GetProperty("trafficClass").GetInt32());
        Assert.Equal("{\"enabled\":false}", outputs[1].GetProperty("qos").GetRawText());

        await harness.EndAsync(session);
    }

    /// <summary>Verifies unknown outputs and malformed SDP paths are 404, HEAD has no body, and other methods are 405.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Routes_RejectUnknownPathsAndMethods()
    {
        await using var harness = new RtpHarness((_, _) => new FakeDatagramSocket(), null, RtpTestData.Output());
        var endpoints = Endpoints(harness);

        foreach (var path in new[] { "/health/rtp/missing.sdp", "/health/rtp/out", "/health/rtp/.sdp", "/health/rtp/a/out.sdp", "/health/rtp/", "/health/rtpx" })
        {
            Assert.Equal(404, (await GetAsync(endpoints, path)).Status);
        }

        var head = Context(IPAddress.Loopback, "HEAD", "/health/rtp");
        await endpoints.ServeAsync(head);
        Assert.Equal(200, head.Response.StatusCode);
        Assert.True(head.Response.ContentLength > 0);
        Assert.Equal(0, head.Response.Body.Length);

        var post = Context(IPAddress.Loopback, "POST", "/health/rtp/out.sdp");
        await endpoints.ServeAsync(post);
        Assert.Equal(405, post.Response.StatusCode);

        var remote = Context(IPAddress.Parse("203.0.113.9"), "GET", "/health/rtp/out.sdp");
        await endpoints.ServeAsync(remote);
        Assert.Equal((403, 0L), (remote.Response.StatusCode, remote.Response.Body.Length));
    }

    /// <summary>Verifies the RTP paths are unknown when no RTP output stage exists.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Routes_WithoutOutputs_Are404()
    {
        var endpoints = new ManagementEndpoints(new RunningLifecycle(), () => true, () => true, NullLogger<ManagementEndpoints>.Instance);

        Assert.Equal(404, (await GetAsync(endpoints, "/health/rtp")).Status);
        Assert.Equal(404, (await GetAsync(endpoints, "/health/rtp/out.sdp")).Status);
    }

    /// <summary>Creates endpoints over a harness's RTP stage.</summary>
    /// <param name="harness">The harness.</param>
    /// <returns>The endpoints.</returns>
    private static ManagementEndpoints Endpoints(RtpHarness harness) =>
        new(new RunningLifecycle(), () => true, () => true, NullLogger<ManagementEndpoints>.Instance, catalog: null, rtp: harness.Handler);

    /// <summary>Describes an output element.</summary>
    /// <param name="output">The element.</param>
    /// <returns>Its fields.</returns>
    private static (string?, string?, string?, string?, string?, int, int) Describe(JsonElement output) => (
        output.GetProperty("name").GetString(),
        output.GetProperty("application").GetString(),
        output.GetProperty("stream").GetString(),
        output.GetProperty("mode").GetString(),
        output.GetProperty("destination").GetString(),
        output.GetProperty("videoPort").GetInt32(),
        output.GetProperty("audioPort").GetInt32());

    /// <summary>Serves a loopback GET.</summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <param name="path">The path.</param>
    /// <returns>The status, body and media type.</returns>
    private static async Task<(int Status, string Body, string? Type)> GetAsync(ManagementEndpoints endpoints, string path)
    {
        var context = Context(IPAddress.Loopback, "GET", path);
        await endpoints.ServeAsync(context);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        return (context.Response.StatusCode, body, context.Response.ContentType);
    }

    /// <summary>Builds a request context.</summary>
    /// <param name="address">The remote address.</param>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    /// <returns>The context.</returns>
    private static DefaultHttpContext Context(IPAddress address, string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>A running lifecycle.</summary>
    private sealed class RunningLifecycle : IApplicationLifecycle
    {
        /// <inheritdoc />
        public ApplicationState State => ApplicationState.Running;

        /// <inheritdoc />
        public bool IsReady => true;

        /// <inheritdoc />
        public DateTimeOffset StateChangedAt => DateTimeOffset.UnixEpoch;

        /// <inheritdoc />
        public Exception? Failure => null;
    }
}
