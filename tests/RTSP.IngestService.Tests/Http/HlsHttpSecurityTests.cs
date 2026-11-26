// Tests of the HTTP HLS origin's security and CORS headers over real HTTP on an ephemeral loopback port. They cover:
// - Http:Cors and Http:CrossOriginResourcePolicy binding and validation, including every rejected origin form;
// - security headers on every HLS response, and the absence of HSTS, CSP, cookies and CORS headers by default;
// - allowlist, any-origin and credential modes on GET, HEAD, ranges, 304, 404, 405 and 500;
// - preflights answered without touching the segment store, granted only for GET/HEAD and Range;
// - unchanged media types, cache policies and range handling;
// - management routes untouched, and the startup log, in the real host.

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Http;
using RTSP.IngestService.Tests.Hls;
using RTSP.IngestService.Tests.Infrastructure;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace RTSP.IngestService.Tests.Http;

/// <summary>Tests for <see cref="HlsHttpHeaders"/> in <see cref="HlsHttpApplication"/>, and for <see cref="HttpCorsOptions"/>.</summary>
public sealed class HlsHttpSecurityTests : IAsyncDisposable
{
    /// <summary>The front-end origin allowed in allowlist tests.</summary>
    private const string Player = "https://player.example.com";

    /// <summary>A second allowed origin, on a non-default port.</summary>
    private const string LocalPlayer = "http://localhost:3000";

    /// <summary>An origin that is never allowed.</summary>
    private const string Other = "https://attacker.example";

    /// <summary>Headers no HLS response may carry while the origin is plain HTTP.</summary>
    private static readonly string[] ForbiddenHeaders = ["Strict-Transport-Security", "Content-Security-Policy", "Content-Security-Policy-Report-Only", "Set-Cookie", "Cross-Origin-Embedder-Policy", "Cross-Origin-Opener-Policy"];

    /// <summary>Every CORS response header.</summary>
    private static readonly string[] CorsHeaders = ["Access-Control-Allow-Origin", "Access-Control-Expose-Headers", "Access-Control-Allow-Credentials", "Access-Control-Allow-Methods", "Access-Control-Allow-Headers", "Access-Control-Max-Age"];

    /// <summary>Owns the meter factory.</summary>
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    /// <summary>The store clock.</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    /// <summary>The HLS options.</summary>
    private readonly HlsOptions _hlsOptions = new();

    /// <summary>The store.</summary>
    private readonly InMemoryHlsSegmentStore _store;

    /// <summary>Started origins, stopped on dispose.</summary>
    private readonly List<HlsHttpOrigin> _origins = [];

    /// <summary>The client.</summary>
    private readonly HttpClient _client = new();

    /// <summary>Initializes a new instance of the <see cref="HlsHttpSecurityTests"/> class.</summary>
    public HlsHttpSecurityTests()
    {
        _store = new InMemoryHlsSegmentStore(MsOptions.Create(_hlsOptions), _time, new HlsMetrics(_services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()));
        var session = Guid.NewGuid();
        Assert.True(_store.TryOpenStream(session, "live", "cam", 4, out _));
        _store.AddInitialization(session, new HlsSegment(HlsSegmentKind.Initialization, "init-0.mp4", [1, 2, 3, 4, 5]));
        Assert.Equal(HlsAppendResult.Published, _store.AppendSegment(session, new HlsSegment(HlsSegmentKind.Media, "segment-0.m4s", new byte[1000]), HlsPlaylistTests.Entry(0, 1.0)));
    }

    /// <summary>Gets every kind of HLS response: path, method and expected status.</summary>
    public static TheoryData<string, string, HttpStatusCode> Responses => new()
    {
        { "hls/live/cam/index.m3u8", "GET", HttpStatusCode.OK },
        { "hls/live/cam/index.m3u8", "HEAD", HttpStatusCode.OK },
        { "hls/live/cam/{generation}/init-0.mp4", "GET", HttpStatusCode.OK },
        { "hls/live/cam/{generation}/segment-0.m4s", "GET", HttpStatusCode.OK },
        { "hls/live/cam/{generation}/segment-0.m4s", "HEAD", HttpStatusCode.OK },
        { "hls/live/missing/index.m3u8", "GET", HttpStatusCode.NotFound },
        { "hls/live/cam/{generation}/segment-9.m4s", "GET", HttpStatusCode.NotFound },
        { "not-hls", "GET", HttpStatusCode.NotFound },
        { "hls/live/cam/index.m3u8", "POST", HttpStatusCode.MethodNotAllowed },
        { "hls/live/cam/index.m3u8", "OPTIONS", HttpStatusCode.MethodNotAllowed },
    };

    /// <summary>Gets origin values that startup rejects, with text the error must contain.</summary>
    public static TheoryData<string, string> InvalidOrigins => new()
    {
        { "*", "set AllowAnyOrigin to true" },
        { "https://*.example.com", "wildcard hosts are not supported" },
        { "null", "opaque origin" },
        { "player.example.com", "not an absolute URL" },
        { "ftp://player.example.com", "http or https" },
        { "https://player.example.com/", "use 'https://player.example.com'" },
        { "https://player.example.com/app", "use 'https://player.example.com'" },
        { "https://PLAYER.example.com", "use 'https://player.example.com'" },
        { "HTTPS://player.example.com", "use 'https://player.example.com'" },
        { "https://player.example.com:443", "use 'https://player.example.com'" },
        { "http://player.example.com:80", "use 'http://player.example.com'" },
        { "https://user@player.example.com", "use 'https://player.example.com'" },
        { "https://player.example.com?x=1", "use 'https://player.example.com'" },
        { "https://player.example.com#top", "use 'https://player.example.com'" },
        { " https://player.example.com", "use 'https://player.example.com'" },
        { "https://bücher.example", "use 'https://xn--bcher-kva.example'" },
    };

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

    /// <summary>Verifies the defaults: CORS off, nothing listed, credentials off, ten-minute preflight cache, cross-origin resources.</summary>
    [Fact]
    public void Defaults_AreSecureAndBackwardCompatible()
    {
        var options = Bind([]);

        Assert.False(options.Cors.Enabled);
        Assert.Empty(options.Cors.AllowedOrigins);
        Assert.False(options.Cors.AllowAnyOrigin);
        Assert.False(options.Cors.AllowCredentials);
        Assert.Equal(600, options.Cors.MaxAgeSeconds);
        Assert.Equal("cross-origin", options.CrossOriginResourcePolicy);
        Assert.Equal("disabled", new HlsHttpHeaders(options).CorsDescription);
    }

    /// <summary>Verifies every valid setting binds, including IPv6, IP and punycode origins and the range limits.</summary>
    [Fact]
    public void ValidSettings_Bind()
    {
        var options = Bind(new()
        {
            ["Http:Cors:Enabled"] = "true",
            ["Http:Cors:AllowedOrigins:0"] = Player,
            ["Http:Cors:AllowedOrigins:1"] = LocalPlayer,
            ["Http:Cors:AllowedOrigins:2"] = "http://[::1]:8081",
            ["Http:Cors:AllowedOrigins:3"] = "http://192.0.2.7:8080",
            ["Http:Cors:AllowedOrigins:4"] = "https://xn--bcher-kva.example",
            ["Http:Cors:AllowCredentials"] = "true",
            ["Http:Cors:MaxAgeSeconds"] = "86400",
            ["Http:CrossOriginResourcePolicy"] = "same-site",
        });

        Assert.Equal([Player, LocalPlayer, "http://[::1]:8081", "http://192.0.2.7:8080", "https://xn--bcher-kva.example"], options.Cors.AllowedOrigins);
        Assert.Equal((true, 86400, "same-site"), (options.Cors.AllowCredentials, options.Cors.MaxAgeSeconds, options.CrossOriginResourcePolicy));
        Assert.Equal(0, Bind(new() { ["Http:Cors:MaxAgeSeconds"] = "0" }).Cors.MaxAgeSeconds);
        Assert.True(Bind(new() { ["Http:Cors:Enabled"] = "true", ["Http:Cors:AllowAnyOrigin"] = "true" }).Cors.AllowAnyOrigin);
        Assert.Equal("same-origin", Bind(new() { ["Http:CrossOriginResourcePolicy"] = "same-origin" }).CrossOriginResourcePolicy);
    }

    /// <summary>Verifies each malformed or non-serialised origin fails startup with its index and the form to use.</summary>
    /// <param name="origin">The configured value.</param>
    /// <param name="expected">Text the failure must contain.</param>
    [Theory]
    [MemberData(nameof(InvalidOrigins))]
    public void InvalidOrigins_FailValidation(string origin, string expected)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Bind(new() { ["Http:Cors:Enabled"] = "true", ["Http:Cors:AllowedOrigins:0"] = origin }));

        Assert.Contains($"AllowedOrigins:0 '{origin}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies the rejected combinations and limits, each with an actionable message.</summary>
    /// <param name="settings">Settings as alternating keys and values under <c>Http:</c>.</param>
    /// <param name="expected">Text the failure must contain.</param>
    [Theory]
    [InlineData(new[] { "Cors:Enabled", "true" }, "grants no origin")]
    [InlineData(new[] { "Cors:Enabled", "true", "Cors:AllowAnyOrigin", "true", "Cors:AllowCredentials", "true" }, "cannot be combined with AllowCredentials")]
    [InlineData(new[] { "Cors:AllowAnyOrigin", "true", "Cors:AllowCredentials", "true" }, "cannot be combined with AllowCredentials")]
    [InlineData(new[] { "Cors:Enabled", "true", "Cors:AllowAnyOrigin", "true", "Cors:AllowedOrigins:0", Player }, "would have no effect")]
    [InlineData(new[] { "Cors:Enabled", "true", "Cors:AllowedOrigins:0", Player, "Cors:AllowedOrigins:1", Player }, "AllowedOrigins:1 'https://player.example.com' is listed more than once")]
    [InlineData(new[] { "Cors:AllowedOrigins:0", "*" }, "set AllowAnyOrigin")]
    [InlineData(new[] { "Cors:MaxAgeSeconds", "-1" }, "MaxAgeSeconds")]
    [InlineData(new[] { "Cors:MaxAgeSeconds", "86401" }, "MaxAgeSeconds")]
    [InlineData(new[] { "CrossOriginResourcePolicy", "none" }, "CrossOriginResourcePolicy")]
    [InlineData(new[] { "CrossOriginResourcePolicy", "" }, "CrossOriginResourcePolicy")]
    public void InvalidCombinations_FailValidation(string[] settings, string expected)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < settings.Length; i += 2)
        {
            values["Http:" + settings[i]] = settings[i + 1];
        }

        var ex = Assert.Throws<OptionsValidationException>(() => Bind(values));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies the origin count limit, unknown keys and unconvertible values.</summary>
    [Fact]
    public void LimitsAndStrictBinding_FailValidation()
    {
        var many = new Dictionary<string, string?> { ["Http:Cors:Enabled"] = "true" };
        for (var i = 0; i <= HttpCorsOptions.MaxAllowedOrigins; i++)
        {
            many[$"Http:Cors:AllowedOrigins:{i}"] = $"https://player{i}.example.com";
        }

        Assert.Contains("At most 100 origins", Assert.Throws<OptionsValidationException>(() => Bind(many)).Message, StringComparison.Ordinal);
        Assert.Throws<OptionsValidationException>(() => Bind(new() { ["Http:Cors:AllowedMethods:0"] = "PUT" }));
        Assert.Throws<OptionsValidationException>(() => Bind(new() { ["Http:Cors:Enabled"] = "yes" }));
        Assert.Throws<OptionsValidationException>(() => Bind(new() { ["Http:Cors:MaxAgeSeconds"] = "10m" }));
    }

    /// <summary>
    /// Verifies every kind of HLS response carries the security headers, and, with default options, no CORS header,
    /// HSTS, CSP, cookie or cross-origin isolation header, even when the request names an origin.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="method">The method.</param>
    /// <param name="status">The expected status.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(Responses))]
    public async Task EveryResponse_HasSecurityHeaders_AndNoCorsByDefault(string path, string method, HttpStatusCode status)
    {
        var baseUri = await StartAsync(new HttpOriginOptions());

        using var response = await SendAsync(new HttpMethod(method), Uri(baseUri, path), origin: Player);

        Assert.Equal(status, response.StatusCode);
        AssertSecurityHeaders(response, "cross-origin");
        AssertNoCors(response);
        Assert.False(response.Headers.Contains("Vary"));
    }

    /// <summary>Verifies the configured resource policy is sent.</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("same-site")]
    [InlineData("same-origin")]
    public async Task ResourcePolicy_IsConfigurable(string policy)
    {
        var baseUri = await StartAsync(new HttpOriginOptions { CrossOriginResourcePolicy = policy });

        using var response = await SendAsync(HttpMethod.Get, Uri(baseUri, "hls/live/cam/{generation}/segment-0.m4s"));

        AssertSecurityHeaders(response, policy);
    }

    /// <summary>
    /// Verifies the allowlist on every kind of response: a listed origin gets exactly its own origin and the exposed
    /// headers; an unlisted origin, a differently cased one and a missing Origin get no grant; every response varies by Origin.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="method">The method.</param>
    /// <param name="status">The expected status.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Allowlist_GrantsOnlyListedOrigins_OnEveryResponse(string path, string method, HttpStatusCode status)
    {
        var baseUri = await StartAsync(Allowlist());
        var uri = Uri(baseUri, path);

        foreach (var origin in new[] { Player, LocalPlayer })
        {
            using var allowed = await SendAsync(new HttpMethod(method), uri, origin);
            Assert.Equal(status, allowed.StatusCode);
            Assert.Equal([origin], allowed.Headers.GetValues("Access-Control-Allow-Origin"));
            Assert.Equal(["Content-Length, Content-Range, Accept-Ranges, ETag, Last-Modified"], allowed.Headers.GetValues("Access-Control-Expose-Headers"));
            Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
            Assert.Equal(["Origin"], allowed.Headers.Vary);
            AssertSecurityHeaders(allowed, "cross-origin");
        }

        foreach (var origin in new[] { Other, "https://PLAYER.example.com", Player + "/", "https://player.example.com.attacker.example", "null", null })
        {
            using var denied = await SendAsync(new HttpMethod(method), uri, origin);
            Assert.Equal(status, denied.StatusCode);
            AssertNoCors(denied);
            Assert.Equal(["Origin"], denied.Headers.Vary);
            AssertSecurityHeaders(denied, "cross-origin");
        }
    }

    /// <summary>Verifies a request with two Origin values is not granted.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Allowlist_RejectsRepeatedOrigin()
    {
        var baseUri = await StartAsync(Allowlist());
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri(baseUri, "hls/live/cam/index.m3u8"));
        request.Headers.TryAddWithoutValidation("Origin", [Player, Player]);

        using var response = await _client.SendAsync(request, Token);

        AssertNoCors(response);
    }

    /// <summary>
    /// Verifies a cross-origin range request (hls.js and Shaka may send Range): the origin still ignores the range and
    /// returns the full segment with <c>Accept-Ranges: none</c>, and the response is readable by the listed origin.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task RangeRequest_IsPermitted_AndStillServedWhole()
    {
        var baseUri = await StartAsync(Allowlist());
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri(baseUri, "hls/live/cam/{generation}/segment-0.m4s"));
        request.Headers.Range = new RangeHeaderValue(0, 99);
        request.Headers.Add("Origin", Player);

        using var response = await _client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1000, (await response.Content.ReadAsByteArrayAsync(Token)).Length);
        Assert.Null(response.Content.Headers.ContentRange);
        Assert.Equal(["none"], response.Headers.AcceptRanges);
        Assert.Equal([Player], response.Headers.GetValues("Access-Control-Allow-Origin"));
    }

    /// <summary>Verifies a conditional request answered 304 keeps the grant, so the browser can use its cached copy.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task NotModified_KeepsTheGrant()
    {
        var baseUri = await StartAsync(Allowlist());
        var uri = Uri(baseUri, "hls/live/cam/index.m3u8");
        using var first = await SendAsync(HttpMethod.Get, uri, Player);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Origin", Player);
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        using var response = await _client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal([Player], response.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Equal(first.Headers.ETag, response.Headers.ETag);
        AssertSecurityHeaders(response, "cross-origin");
    }

    /// <summary>
    /// Verifies a preflight from a listed origin for GET or HEAD, with no header or with Range, is answered 204 with
    /// exactly the granted methods, header and lifetime, and the security headers, whether or not the path exists.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="method">The requested method.</param>
    /// <param name="headers">The requested headers, or <see langword="null"/>.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData("hls/live/cam/index.m3u8", "GET", null)]
    [InlineData("hls/live/cam/index.m3u8", "GET", "range")]
    [InlineData("hls/live/cam/{generation}/segment-0.m4s", "GET", "Range")]
    [InlineData("hls/live/cam/{generation}/segment-0.m4s", "HEAD", "range, Range")]
    [InlineData("hls/live/missing/index.m3u8", "GET", "range")]
    public async Task Preflight_FromListedOrigin_IsGranted(string path, string method, string? headers)
    {
        var baseUri = await StartAsync(Allowlist());

        using var response = await PreflightAsync(Uri(baseUri, path), Player, method, headers);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal([Player], response.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Equal(["GET, HEAD"], response.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Equal(["Range"], response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Equal(["600"], response.Headers.GetValues("Access-Control-Max-Age"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Equal(["Origin", "Access-Control-Request-Method", "Access-Control-Request-Headers"], response.Headers.Vary);
        AssertSecurityHeaders(response, "cross-origin");
    }

    /// <summary>
    /// Verifies preflights that ask for more than GET/HEAD and Range, or come from an unlisted origin, are answered 204
    /// without any grant, so the browser blocks the actual request.
    /// </summary>
    /// <param name="origin">The origin.</param>
    /// <param name="method">The requested method.</param>
    /// <param name="headers">The requested headers, or <see langword="null"/>.</param>
    /// <returns>A task that completes when the test is finished.</returns>
    [Theory]
    [InlineData(Other, "GET", null)]
    [InlineData("https://PLAYER.example.com", "GET", null)]
    [InlineData(Player, "POST", null)]
    [InlineData(Player, "PUT", null)]
    [InlineData(Player, "DELETE", null)]
    [InlineData(Player, "OPTIONS", null)]
    [InlineData(Player, "get", null)]
    [InlineData(Player, "GET", "authorization")]
    [InlineData(Player, "GET", "range, x-custom")]
    [InlineData(Player, "GET", "content-type")]
    public async Task Preflight_BeyondTheGrant_IsDenied(string origin, string method, string? headers)
    {
        var baseUri = await StartAsync(Allowlist());

        using var response = await PreflightAsync(Uri(baseUri, "hls/live/cam/index.m3u8"), origin, method, headers);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertNoCors(response);
        AssertSecurityHeaders(response, "cross-origin");
    }

    /// <summary>
    /// Verifies preflights never reach the segment store: with a store that fails every call, a preflight is still
    /// answered 204 with its grant, while an actual request is 500 and keeps the security and CORS headers.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Preflight_DoesNotTouchTheStore_AndFailuresKeepHeaders()
    {
        var store = new FailingStore();
        var baseUri = await StartAsync(Allowlist(), store);

        using var preflight = await PreflightAsync(Uri(baseUri, "hls/live/cam/index.m3u8"), Player, "GET", "range");
        Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        Assert.Equal([Player], preflight.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Equal(0, store.Calls);

        using var failed = await SendAsync(HttpMethod.Get, new Uri(baseUri, "hls/live/cam/index.m3u8"), Player);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(store.Calls > 0);
        Assert.True(failed.Headers.CacheControl!.NoStore);
        Assert.Equal([Player], failed.Headers.GetValues("Access-Control-Allow-Origin"));
        AssertSecurityHeaders(failed, "cross-origin");
    }

    /// <summary>Verifies an OPTIONS request that is not a preflight stays 405 with CORS enabled.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task NonPreflightOptions_Stays405()
    {
        var baseUri = await StartAsync(Allowlist());

        using var response = await SendAsync(HttpMethod.Options, Uri(baseUri, "hls/live/cam/index.m3u8"), Player);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow.OrderBy(static m => m, StringComparer.Ordinal));
        Assert.Equal([Player], response.Headers.GetValues("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// Verifies any-origin mode: every request, with any or no Origin, gets <c>*</c> and the exposed headers, never
    /// credentials and no <c>Vary</c>; preflights are granted with <c>*</c>.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task AnyOrigin_GrantsWildcardWithoutCredentials()
    {
        var baseUri = await StartAsync(new HttpOriginOptions { Cors = new() { Enabled = true, AllowAnyOrigin = true } });

        foreach (var origin in new[] { Player, Other, "null", null })
        {
            using var response = await SendAsync(HttpMethod.Get, Uri(baseUri, "hls/live/cam/{generation}/segment-0.m4s"), origin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["*"], response.Headers.GetValues("Access-Control-Allow-Origin"));
            Assert.Equal(["Content-Length, Content-Range, Accept-Ranges, ETag, Last-Modified"], response.Headers.GetValues("Access-Control-Expose-Headers"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            Assert.False(response.Headers.Contains("Vary"));
        }

        using var preflight = await PreflightAsync(Uri(baseUri, "hls/live/cam/index.m3u8"), Other, "GET", "range");
        Assert.Equal(["*"], preflight.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Equal(["GET, HEAD"], preflight.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Credentials"));
        using var denied = await PreflightAsync(Uri(baseUri, "hls/live/cam/index.m3u8"), Other, "PUT", null);
        AssertNoCors(denied);
    }

    /// <summary>Verifies credential mode grants credentials, on responses and preflights, to listed origins only.</summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Credentials_AreGrantedOnlyToListedOrigins()
    {
        var options = Allowlist();
        options.Cors.AllowCredentials = true;
        options.Cors.MaxAgeSeconds = 0;
        var baseUri = await StartAsync(options);
        var uri = Uri(baseUri, "hls/live/cam/index.m3u8");

        using var allowed = await SendAsync(HttpMethod.Get, uri, Player);
        Assert.Equal(["true"], allowed.Headers.GetValues("Access-Control-Allow-Credentials"));
        Assert.Equal([Player], allowed.Headers.GetValues("Access-Control-Allow-Origin"));
        using var preflight = await PreflightAsync(uri, Player, "GET", "range");
        Assert.Equal(["true"], preflight.Headers.GetValues("Access-Control-Allow-Credentials"));
        Assert.Equal(["0"], preflight.Headers.GetValues("Access-Control-Max-Age"));
        using var denied = await SendAsync(HttpMethod.Get, uri, Other);
        AssertNoCors(denied);
    }

    /// <summary>
    /// Verifies applying the headers, and answering a preflight, allocates nothing once a response's header storage
    /// exists: the values are preformatted constants and the allowlist lookup returns the configured instance.
    /// </summary>
    [Fact]
    public void Headers_DoNotAllocatePerRequest()
    {
        var headers = new HlsHttpHeaders(Allowlist());
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Headers.Origin = Player;
        context.Request.Headers.AccessControlRequestMethod = "GET";
        context.Request.Headers.AccessControlRequestHeaders = "range";
        headers.Apply(context);
        headers.AnswerPreflight(context);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            headers.Apply(context);
            headers.AnswerPreflight(context);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(Player, context.Response.Headers.AccessControlAllowOrigin);
    }

    /// <summary>Verifies the startup description of each mode.</summary>
    [Fact]
    public void CorsDescription_NamesTheMode()
    {
        Assert.Equal("any origin", new HlsHttpHeaders(new HttpOriginOptions { Cors = new() { Enabled = true, AllowAnyOrigin = true } }).CorsDescription);
        var options = Allowlist();
        options.Cors.AllowCredentials = true;
        Assert.Equal("2 allowed origins (http://localhost:3000, https://player.example.com) with credentials", new HlsHttpHeaders(options).CorsDescription);
    }

    /// <summary>
    /// Verifies media types and cache policies are unchanged with CORS on: playlists are revalidated on every use,
    /// segments are cacheable for their bounded lifetime, errors are never stored, and nothing is sniffable.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task ContentTypesAndCaching_AreUnchanged()
    {
        var baseUri = await StartAsync(Allowlist());
        var lifetime = HlsHttpApplication.SegmentCacheLifetime(_hlsOptions);

        using var playlist = await SendAsync(HttpMethod.Get, Uri(baseUri, "hls/live/cam/index.m3u8"), Player);
        Assert.Equal("application/vnd.apple.mpegurl", playlist.Content.Headers.ContentType!.MediaType);
        Assert.True(playlist.Headers.CacheControl!.NoCache);
        Assert.Null(playlist.Headers.CacheControl.MaxAge);

        using var init = await SendAsync(HttpMethod.Get, Uri(baseUri, "hls/live/cam/{generation}/init-0.mp4"), Player);
        Assert.Equal("video/mp4", init.Content.Headers.ContentType!.MediaType);
        Assert.Equal((true, lifetime), (init.Headers.CacheControl!.Public, init.Headers.CacheControl.MaxAge));

        using var segment = await SendAsync(HttpMethod.Get, Uri(baseUri, "hls/live/cam/{generation}/segment-0.m4s"), Player);
        Assert.Equal("video/iso.segment", segment.Content.Headers.ContentType!.MediaType);
        Assert.Equal((true, lifetime), (segment.Headers.CacheControl!.Public, segment.Headers.CacheControl.MaxAge));

        using var missing = await SendAsync(HttpMethod.Get, Uri(baseUri, "hls/live/cam/{generation}/segment-9.m4s"), Player);
        Assert.True(missing.Headers.CacheControl!.NoStore);
        Assert.Null(missing.Content.Headers.ContentType);
        Assert.All(new[] { playlist, init, segment, missing }, static r => Assert.Equal(["nosniff"], r.Headers.GetValues("X-Content-Type-Options")));
    }

    /// <summary>
    /// Verifies, in the real host with CORS configured, that HLS responses carry the grant while management routes on
    /// the same listener keep their own headers, and that the policy is logged at startup.
    /// </summary>
    /// <returns>A task that completes when the test is finished.</returns>
    [Fact]
    public async Task Host_AppliesPolicyToHlsOnly_AndLogsIt()
    {
        var logs = new FakeLogCollector();
        using var host = TestHosts.CreateBuilder(logs, overrides: new Dictionary<string, string?>
        {
            ["Http:Cors:Enabled"] = "true",
            ["Http:Cors:AllowedOrigins:0"] = Player,
        }).Build();
        await host.StartAsync(Token).WithinSafetyTimeout();
        try
        {
            var origin = host.Services.GetRequiredService<HlsHttpOrigin>();
            var baseUri = new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/");

            using var hls = await SendAsync(HttpMethod.Get, new Uri(baseUri, "hls/live/none/index.m3u8"), Player);
            Assert.Equal(HttpStatusCode.NotFound, hls.StatusCode);
            Assert.Equal([Player], hls.Headers.GetValues("Access-Control-Allow-Origin"));

            using var live = await SendAsync(HttpMethod.Get, new Uri(baseUri, "health/live"), Player);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            AssertNoCors(live);
            Assert.False(live.Headers.Contains("Cross-Origin-Resource-Policy"));
            using var preflight = await PreflightAsync(new Uri(baseUri, "health/live"), Player, "GET", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, preflight.StatusCode);
            AssertNoCors(preflight);

            Assert.Contains(logs.GetSnapshot(), static r => r.Id.Id == 8003 && r.Message == "HTTP HLS origin CORS 1 allowed origins (https://player.example.com); Cross-Origin-Resource-Policy cross-origin");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Gets the test cancellation token.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Returns options allowing <see cref="Player"/> and <see cref="LocalPlayer"/>.</summary>
    /// <returns>The options.</returns>
    private static HttpOriginOptions Allowlist() => new() { Cors = new() { Enabled = true, AllowedOrigins = [Player, LocalPlayer] } };

    /// <summary>Asserts the security headers.</summary>
    /// <param name="response">The response.</param>
    /// <param name="resourcePolicy">The expected <c>Cross-Origin-Resource-Policy</c>.</param>
    private static void AssertSecurityHeaders(HttpResponseMessage response, string resourcePolicy)
    {
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(["no-referrer"], response.Headers.GetValues("Referrer-Policy"));
        Assert.Equal(["DENY"], response.Headers.GetValues("X-Frame-Options"));
        Assert.Equal([resourcePolicy], response.Headers.GetValues("Cross-Origin-Resource-Policy"));
        Assert.All(ForbiddenHeaders, name => Assert.False(response.Headers.Contains(name), name));
    }

    /// <summary>Asserts a response grants nothing to any origin.</summary>
    /// <param name="response">The response.</param>
    private static void AssertNoCors(HttpResponseMessage response) =>
        Assert.All(CorsHeaders, name => Assert.False(response.Headers.Contains(name), name));

    /// <summary>Binds and validates the HTTP origin options from configuration.</summary>
    /// <param name="values">Settings in addition to the application name.</param>
    /// <returns>The options.</returns>
    private static HttpOriginOptions Bind(Dictionary<string, string?> values)
    {
        values["Application:Name"] = "TestService";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var provider = new ServiceCollection().AddIngestServiceOptions(configuration).BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HttpOriginOptions>>().Value;
    }

    /// <summary>Resolves a test path, substituting the store's generation.</summary>
    /// <param name="baseUri">The origin.</param>
    /// <param name="path">The path.</param>
    /// <returns>The URL.</returns>
    private Uri Uri(Uri baseUri, string path) => new(baseUri, path.Replace("{generation}", _store.Generation, StringComparison.Ordinal));

    /// <summary>Starts an origin with header options on an ephemeral port.</summary>
    /// <param name="http">The HTTP origin options.</param>
    /// <param name="store">The store; the test's store by default.</param>
    /// <returns>Its base URI.</returns>
    private async Task<Uri> StartAsync(HttpOriginOptions http, IHlsSegmentStore? store = null)
    {
        http.ListenAddress = "127.0.0.1";
        http.Port = 0;
        var meters = _services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
        var application = new HlsHttpApplication(store ?? _store, MsOptions.Create(_hlsOptions), new HttpOriginMetrics(meters), _time, NullLogger<HlsHttpApplication>.Instance, headers: new HlsHttpHeaders(http));
        var origin = new HlsHttpOrigin(MsOptions.Create(http), application, _services, NullLoggerFactory.Instance);
        await origin.StartAsync(Token);
        _origins.Add(origin);
        return new Uri($"http://127.0.0.1:{origin.LocalEndPoint!.Port}/");
    }

    /// <summary>Sends a request without a body, with an optional Origin.</summary>
    /// <param name="method">The method.</param>
    /// <param name="uri">The URL.</param>
    /// <param name="origin">The Origin value, or <see langword="null"/> for none.</param>
    /// <returns>The response, with its body read.</returns>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        var response = await _client.SendAsync(request, Token);
        await response.Content.LoadIntoBufferAsync(Token);
        return response;
    }

    /// <summary>Sends a CORS preflight.</summary>
    /// <param name="uri">The URL.</param>
    /// <param name="origin">The Origin.</param>
    /// <param name="method">The requested method.</param>
    /// <param name="headers">The requested headers, or <see langword="null"/>.</param>
    /// <returns>The response, with its body read.</returns>
    private async Task<HttpResponseMessage> PreflightAsync(Uri uri, string origin, string method, string? headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, uri);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
        if (headers is not null)
        {
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", headers);
        }

        var response = await _client.SendAsync(request, Token);
        await response.Content.LoadIntoBufferAsync(Token);
        return response;
    }

    /// <summary>A store that fails every call and counts them.</summary>
    private sealed class FailingStore : IHlsSegmentStore
    {
        /// <summary>The number of calls.</summary>
        private int _calls;

        /// <summary>Gets the number of calls.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <inheritdoc />
        public string Generation => Fail<string>();

        /// <inheritdoc />
        public bool TryOpenStream(Guid sessionId, string application, string streamIdentity, int targetDurationSeconds, out HlsStreamNumbering numbering, string? incarnationId = null, long generation = 0)
        {
            numbering = default;
            return Fail<bool>();
        }

        /// <inheritdoc />
        public HlsAppendResult AddInitialization(Guid sessionId, HlsSegment segment) => Fail<HlsAppendResult>();

        /// <inheritdoc />
        public HlsAppendResult AppendSegment(Guid sessionId, HlsSegment segment, HlsSegmentEntry entry) => Fail<HlsAppendResult>();

        /// <inheritdoc />
        public void CloseStream(Guid sessionId, bool completed) => Fail<bool>();

        /// <inheritdoc />
        public HlsPlaylistSnapshot? GetPlaylist(string application, string streamIdentity) => Fail<HlsPlaylistSnapshot?>();

        /// <inheritdoc />
        public HlsSegment? GetSegment(string application, string streamIdentity, string name) => Fail<HlsSegment?>();

        /// <inheritdoc />
        public HlsSegmentLease? AcquireSegment(string application, string streamIdentity, string name) => Fail<HlsSegmentLease?>();

        /// <inheritdoc />
        public HlsSegmentAccess TryAcquireSegment(string application, string streamIdentity, string name, out HlsSegmentLease? lease)
        {
            lease = null;
            return Fail<HlsSegmentAccess>();
        }

        /// <inheritdoc />
        public bool ContainsStream(string application, string streamIdentity) => Fail<bool>();

        /// <summary>Counts a call and fails it.</summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <returns>Never returns.</returns>
        private T Fail<T>()
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The store must not be used.");
        }
    }
}
