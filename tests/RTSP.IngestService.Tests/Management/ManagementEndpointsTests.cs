// Tests for the management endpoints served by the shared HTTP origin over real Kestrel on an ephemeral port:
// liveness and readiness for every lifecycle state and listener-bound combination, response headers and bodies,
// HEAD and unsupported methods, HLS and management on the same listener and connection, management disabled, and
// the loopback-only policy (IPv4, IPv6 and IPv4-mapped loopback accepted; non-loopback and unknown remote addresses
// rejected; forwarding headers ignored) applied identically to every management path.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Lifecycle;
using RTSP.IngestService.Management;
using RTSP.IngestService.Tests.Infrastructure;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RTSP.IngestService.Tests.Management;

/// <summary>Tests for <see cref="ManagementEndpoints"/> in the <see cref="HlsHttpApplication"/> pipeline.</summary>
public sealed class ManagementEndpointsTests : IAsyncDisposable
{
    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The controllable lifecycle.</summary>
    private readonly StubLifecycle _lifecycle = new();

    /// <summary>Started origins, stopped on dispose.</summary>
    private readonly List<HlsHttpOrigin> _origins = [];

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>The reported RTMP listener state.</summary>
    private bool _rtmpListening = true;

    /// <summary>Overrides the HTTP listener state; <see langword="null"/> reports the origin's real state.</summary>
    private bool? _httpListening;

    /// <summary>Gets every management path, including unknown ones, for policy tests.</summary>
    public static TheoryData<string> ManagementPaths => ["/health/live", "/health/ready", "/health/unknown", "/health/", "/health"];

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var origin in _origins)
        {
            await origin.StopAsync(CancellationToken.None);
            await origin.DisposeAsync();
        }

        _client.Dispose();
        await _services.DisposeAsync();
    }

    /// <summary>Verifies liveness for every lifecycle state.</summary>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="expected">The expected status code.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(ApplicationState.NotStarted, HttpStatusCode.OK)]
    [InlineData(ApplicationState.Starting, HttpStatusCode.OK)]
    [InlineData(ApplicationState.Running, HttpStatusCode.OK)]
    [InlineData(ApplicationState.Stopping, HttpStatusCode.OK)]
    [InlineData(ApplicationState.Stopped, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Failed, HttpStatusCode.ServiceUnavailable)]
    public async Task Live_FollowsLifecycle(ApplicationState state, HttpStatusCode expected)
    {
        var origin = await StartAsync();
        _lifecycle.State = state;

        using var response = await _client.GetAsync(Uri(origin, "/health/live"), Token);

        Assert.Equal(expected, response.StatusCode);
        AssertHealthHeaders(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(expected == HttpStatusCode.OK ? "live" : "not_live", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(state.ToString(), body.RootElement.GetProperty("state").GetString());
        Assert.False(body.RootElement.TryGetProperty("rtmpListener", out _));
    }

    /// <summary>
    /// Verifies readiness is 200 only while Running with both listeners bound, and otherwise 503 with a body that
    /// identifies the state and the unbound listener.
    /// </summary>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="rtmp">Whether the RTMP listener is bound.</param>
    /// <param name="http">Whether the HTTP listener is reported bound.</param>
    /// <param name="expected">The expected status code.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(ApplicationState.Running, true, true, HttpStatusCode.OK)]
    [InlineData(ApplicationState.Running, false, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Running, true, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.NotStarted, false, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Starting, false, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Starting, false, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Starting, true, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Stopping, true, true, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Stopped, false, false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ApplicationState.Failed, true, true, HttpStatusCode.ServiceUnavailable)]
    public async Task Ready_RequiresRunningAndBothListeners(ApplicationState state, bool rtmp, bool http, HttpStatusCode expected)
    {
        var origin = await StartAsync();
        _lifecycle.State = state;
        _rtmpListening = rtmp;
        _httpListening = http;

        using var response = await _client.GetAsync(Uri(origin, "/health/ready"), Token);

        Assert.Equal(expected, response.StatusCode);
        AssertHealthHeaders(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(expected == HttpStatusCode.OK ? "ready" : "not_ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(state.ToString(), body.RootElement.GetProperty("state").GetString());
        Assert.Equal(rtmp, body.RootElement.GetProperty("rtmpListener").GetBoolean());
        Assert.Equal(http, body.RootElement.GetProperty("httpListener").GetBoolean());
    }

    /// <summary>Verifies readiness reads the origin's real bound state: bound after start, unbound once stopping.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ready_UsesOriginBoundState()
    {
        var origin = CreateOrigin();
        var endpoints = CreateEndpoints(origin);
        _lifecycle.State = ApplicationState.Running;
        Assert.False(origin.IsListening);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, endpoints.Ready().Status);

        await origin.StartAsync(Token);
        _origins.Add(origin);
        Assert.True(origin.IsListening);
        using (var response = await _client.GetAsync(Uri(origin, "/health/ready"), Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await origin.StopAsync(Token);
        Assert.False(origin.IsListening);
        Assert.Equal((StatusCodes.Status503ServiceUnavailable, false), (endpoints.Ready().Status, endpoints.Ready().Body.HttpListener!.Value));
    }

    /// <summary>Verifies HEAD returns the GET status and headers without a body.</summary>
    /// <param name="path">The endpoint.</param>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="expected">The expected status code.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("/health/live", ApplicationState.Running, HttpStatusCode.OK)]
    [InlineData("/health/live", ApplicationState.Failed, HttpStatusCode.ServiceUnavailable)]
    [InlineData("/health/ready", ApplicationState.Running, HttpStatusCode.OK)]
    [InlineData("/health/ready", ApplicationState.Stopping, HttpStatusCode.ServiceUnavailable)]
    public async Task Head_MatchesGetWithoutBody(string path, ApplicationState state, HttpStatusCode expected)
    {
        var origin = await StartAsync();
        _lifecycle.State = state;
        using var get = await _client.GetAsync(Uri(origin, path), Token);
        var getLength = (await get.Content.ReadAsByteArrayAsync(Token)).Length;

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Uri(origin, path)), Token);

        Assert.Equal(expected, head.StatusCode);
        AssertHealthHeaders(head);
        Assert.Equal(getLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>Verifies unsupported methods are 405 and unknown management paths 404, both uncacheable and empty.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task UnsupportedMethodAndUnknownPath_AreRejected()
    {
        var origin = await StartAsync();
        _lifecycle.State = ApplicationState.Running;

        using var post = await _client.PostAsync(Uri(origin, "/health/live"), new ByteArrayContent([]), Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(["GET", "HEAD"], post.Content.Headers.Allow.Order(StringComparer.Ordinal));
        Assert.True(post.Headers.CacheControl!.NoStore);

        using var unknown = await _client.GetAsync(Uri(origin, "/health/other"), Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.True(unknown.Headers.CacheControl!.NoStore);
        Assert.Empty(await unknown.Content.ReadAsByteArrayAsync(Token));
    }

    /// <summary>
    /// Verifies HLS and management are served by the same listener: both answer on the origin's one port, in turn on
    /// a single TCP connection.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task HlsAndManagement_ShareOneListenerAndConnection()
    {
        var origin = await StartAsync();
        _lifecycle.State = ApplicationState.Running;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(origin.LocalEndPoint!, Token);
        var requests = "GET /health/live HTTP/1.1\r\nHost: test\r\n\r\n" +
            "GET /hls/live/cam/index.m3u8 HTTP/1.1\r\nHost: test\r\n\r\n" +
            "GET /health/ready HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n";
        await socket.SendAsync(Encoding.ASCII.GetBytes(requests), SocketFlags.None, Token);
        var text = await ReadToEndAsync(socket);

        var statuses = System.Text.RegularExpressions.Regex.Matches(text, @"HTTP/1\.1 \d{3} [^\r]*").Select(static m => m.Value);
        Assert.Equal(["HTTP/1.1 200 OK", "HTTP/1.1 404 Not Found", "HTTP/1.1 200 OK"], statuses);
        Assert.Contains("{\"status\":\"live\",", text, StringComparison.Ordinal);
        Assert.EndsWith("\"rtmpListener\":true,\"httpListener\":true}", text, StringComparison.Ordinal);
    }

    /// <summary>Verifies that with management disabled the health paths are ordinary unknown HLS paths and HLS is unaffected.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Disabled_HealthPathsAreUnknownAndHlsIsServed()
    {
        var origin = CreateOrigin(management: false);
        await origin.StartAsync(Token);
        _origins.Add(origin);
        _lifecycle.State = ApplicationState.Running;

        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var response = await _client.GetAsync(Uri(origin, path), Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
        }

        using var playlist = await _client.GetAsync(Uri(origin, "/hls/live/missing/index.m3u8"), Token);
        Assert.Equal(HttpStatusCode.NotFound, playlist.StatusCode);
    }

    /// <summary>Verifies IPv4 loopback clients other than 127.0.0.1 are accepted.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ipv4Loopback_IsAccepted()
    {
        var origin = await StartAsync();
        _lifecycle.State = ApplicationState.Running;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (!OperatingSystem.IsMacOS())
        {
            socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        }

        await socket.ConnectAsync(origin.LocalEndPoint!, Token);
        Assert.StartsWith("HTTP/1.1 200 ", await RequestAsync(socket, "/health/live"), StringComparison.Ordinal);
    }

    /// <summary>Verifies IPv6 loopback clients are accepted.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ipv6Loopback_IsAccepted()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "IPv6 is not available on this machine.");
        var origin = await StartAsync("::1");
        _lifecycle.State = ApplicationState.Running;

        using var response = await _client.GetAsync(new Uri($"http://[::1]:{origin.LocalEndPoint!.Port}/health/ready"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Verifies a dual-mode listener accepts IPv4 loopback clients, whose remote address Kestrel reports as
    /// IPv4-mapped IPv6.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Ipv4MappedLoopback_IsAccepted()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "IPv6 is not available on this machine.");
        var origin = await StartAsync("::");
        _lifecycle.State = ApplicationState.Running;

        using var response = await _client.GetAsync(new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/health/live"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Verifies a client connecting from a non-loopback address of this machine is refused with an empty 403 on every
    /// management path, even with forwarding headers claiming a loopback client, while HLS stays reachable.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task NonLoopbackClient_IsForbiddenOverRealSockets()
    {
        var local = NonLoopbackAddress();
        Assert.SkipWhen(local is null, "This machine has no non-loopback IPv4 address.");
        var origin = await StartAsync(local!.ToString());
        _lifecycle.State = ApplicationState.Running;
        var spoofs = new[]
        {
            string.Empty,
            "X-Forwarded-For: 127.0.0.1\r\n",
            "Forwarded: for=127.0.0.1\r\n",
            "Forwarded: for=\"[::1]\"\r\nX-Forwarded-For: ::1\r\nX-Real-IP: 127.0.0.1\r\nX-Forwarded-Host: localhost\r\n",
        };

        foreach (var path in ManagementPaths.Select(static row => row.Data))
        {
            foreach (var spoof in spoofs)
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(origin.LocalEndPoint!, Token);
                var response = await RequestAsync(socket, path, spoof);
                Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", response, StringComparison.Ordinal);
                Assert.Contains("\r\nContent-Length: 0\r\n", response, StringComparison.Ordinal);
                Assert.Contains("\r\nCache-Control: no-store\r\n", response, StringComparison.Ordinal);
                Assert.EndsWith("\r\n\r\n", response, StringComparison.Ordinal);
            }
        }

        using var hlsSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await hlsSocket.ConnectAsync(origin.LocalEndPoint!, Token);
        Assert.StartsWith("HTTP/1.1 404 ", await RequestAsync(hlsSocket, "/hls/live/cam/index.m3u8"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that a non-loopback or unknown remote address is refused identically on every management path and
    /// method, regardless of forwarding headers, with no body or endpoint-specific headers.
    /// </summary>
    /// <param name="path">The management path.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(ManagementPaths))]
    public async Task UntrustedRemoteAddress_IsForbiddenOnEveryPath(string path)
    {
        var endpoints = CreateEndpoints(origin: null);
        _lifecycle.State = ApplicationState.Running;
        IPAddress?[] addresses = [null, IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::7"), IPAddress.Parse("10.0.0.1"), IPAddress.Any, IPAddress.IPv6Any];

        foreach (var address in addresses)
        {
            foreach (var method in new[] { "GET", "HEAD", "POST", "DELETE" })
            {
                var context = Context(address, method, path);
                context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
                context.Request.Headers["Forwarded"] = "for=127.0.0.1";

                await endpoints.ServeAsync(context);

                Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
                Assert.Equal(0, context.Response.ContentLength);
                Assert.Equal(0, context.Response.Body.Length);
                Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
                Assert.False(context.Response.Headers.ContainsKey("Allow"));
                Assert.Null(context.Response.ContentType);
            }
        }
    }

    /// <summary>Verifies forwarding headers naming a remote client do not affect a loopback request.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ForwardingHeaders_DoNotAffectLoopbackClients()
    {
        var endpoints = CreateEndpoints(origin: null);
        _lifecycle.State = ApplicationState.Running;
        var context = Context(IPAddress.Loopback, "GET", "/health/live");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        context.Request.Headers["Forwarded"] = "for=203.0.113.7";

        await endpoints.ServeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Verifies loopback classification of transport addresses.</summary>
    /// <param name="address">The address, or empty for none.</param>
    /// <param name="expected">Whether it is accepted.</param>
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.0.9", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("::ffff:10.1.2.3", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2001:db8::1", false)]
    public void IsLoopback_ClassifiesAddresses(string address, bool expected) =>
        Assert.Equal(expected, ManagementEndpoints.IsLoopback(address.Length == 0 ? null : IPAddress.Parse(address)));

    /// <summary>Verifies a failure while evaluating a request is answered 500 and the listener keeps serving.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Failure_IsAnswered500AndListenerKeepsServing()
    {
        var fail = true;
        var endpoints = new ManagementEndpoints(_lifecycle, () => fail ? throw new InvalidOperationException("probe failed") : true, () => true, NullLogger<ManagementEndpoints>.Instance);
        var origin = CreateOrigin(endpoints);
        await origin.StartAsync(Token);
        _origins.Add(origin);
        _lifecycle.State = ApplicationState.Running;

        using (var failed = await _client.GetAsync(Uri(origin, "/health/ready"), Token))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.True(failed.Headers.CacheControl!.NoStore);
        }

        fail = false;
        using var recovered = await _client.GetAsync(Uri(origin, "/health/ready"), Token);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    /// <summary>Asserts the headers shared by every health response.</summary>
    /// <param name="response">The response.</param>
    private static void AssertHealthHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    /// <summary>Builds a request context with the given transport remote address.</summary>
    /// <param name="address">The remote address, or <see langword="null"/> for none.</param>
    /// <param name="method">The method.</param>
    /// <param name="path">The path.</param>
    /// <returns>The context, with a seekable response body.</returns>
    private static DefaultHttpContext Context(IPAddress? address, string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>Returns the URL of a path on the origin.</summary>
    /// <param name="origin">The started origin.</param>
    /// <param name="path">The absolute path.</param>
    /// <returns>The URL.</returns>
    private static Uri Uri(HlsHttpOrigin origin, string path) => new($"http://127.0.0.1:{origin.LocalEndPoint!.Port}{path}");

    /// <summary>Sends one request with <c>Connection: close</c> and reads the whole response.</summary>
    /// <param name="socket">A connected socket.</param>
    /// <param name="path">The path.</param>
    /// <param name="headers">Extra header lines, each ending in CRLF.</param>
    /// <returns>The response text.</returns>
    private static async Task<string> RequestAsync(Socket socket, string path, string headers = "")
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: test\r\n{headers}Connection: close\r\n\r\n"), SocketFlags.None, Token);
        return await ReadToEndAsync(socket);
    }

    /// <summary>Reads until the server closes the connection.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The received text.</returns>
    private static async Task<string> ReadToEndAsync(Socket socket)
    {
        var buffer = new byte[4096];
        using var received = new MemoryStream();
        int read;
        while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, Token).AsTask().WithinSafetyTimeout()) > 0)
        {
            received.Write(buffer, 0, read);
        }

        return Encoding.ASCII.GetString(received.ToArray());
    }

    /// <summary>Returns an operational non-loopback IPv4 address of this machine, or <see langword="null"/>.</summary>
    /// <returns>The address.</returns>
    private static IPAddress? NonLoopbackAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(static n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(static n => n.GetIPProperties().UnicastAddresses)
        .Select(static a => a.Address)
        .FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    /// <summary>Creates management endpoints over the stub lifecycle and listener flags.</summary>
    /// <param name="origin">The origin whose bound state is reported unless overridden, or <see langword="null"/>.</param>
    /// <returns>The endpoints.</returns>
    private ManagementEndpoints CreateEndpoints(HlsHttpOrigin? origin) => new(
        _lifecycle,
        () => _rtmpListening,
        () => _httpListening ?? origin?.IsListening ?? false,
        NullLogger<ManagementEndpoints>.Instance);

    /// <summary>Creates an origin, not started, whose pipeline serves management endpoints unless disabled.</summary>
    /// <param name="endpoints">The endpoints, or <see langword="null"/> to create them over this origin.</param>
    /// <param name="address">The listen address.</param>
    /// <param name="management">Whether the pipeline serves management endpoints.</param>
    /// <returns>The origin.</returns>
    private HlsHttpOrigin CreateOrigin(ManagementEndpoints? endpoints = null, string address = "127.0.0.1", bool management = true)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var hls = new HlsOptions();
        var meters = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var store = new InMemoryHlsSegmentStore(MsOptions.Create(hls), time, new HlsMetrics(meters));
        HlsHttpOrigin? origin = null;
        endpoints ??= new ManagementEndpoints(_lifecycle, () => _rtmpListening, () => _httpListening ?? origin!.IsListening, NullLogger<ManagementEndpoints>.Instance);
        var application = new HlsHttpApplication(store, MsOptions.Create(hls), new HttpOriginMetrics(meters), time, NullLogger<HlsHttpApplication>.Instance, management ? endpoints : null);
        origin = new HlsHttpOrigin(MsOptions.Create(new HttpOriginOptions { ListenAddress = address, Port = 0 }), application, _services, NullLoggerFactory.Instance);
        return origin;
    }

    /// <summary>Creates and starts an origin serving management endpoints.</summary>
    /// <param name="address">The listen address.</param>
    /// <returns>The started origin.</returns>
    private async Task<HlsHttpOrigin> StartAsync(string address = "127.0.0.1")
    {
        var origin = CreateOrigin(address: address);
        await origin.StartAsync(Token);
        _origins.Add(origin);
        return origin;
    }

    /// <summary>A lifecycle whose state is set by the test.</summary>
    private sealed class StubLifecycle : IApplicationLifecycle
    {
        /// <inheritdoc />
        public ApplicationState State { get; set; } = ApplicationState.NotStarted;

        /// <inheritdoc />
        public bool IsReady => State == ApplicationState.Running;

        /// <inheritdoc />
        public DateTimeOffset StateChangedAt => DateTimeOffset.UnixEpoch;

        /// <inheritdoc />
        public Exception? Failure => null;
    }
}
