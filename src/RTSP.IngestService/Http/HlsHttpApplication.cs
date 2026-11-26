// Kestrel request pipeline of the HTTP HLS origin: routes /hls/{application}/{stream}/{file} to the segment store
// and writes playlists, initialization segments and media segments, and hands /health/* to the management endpoints
// when they are enabled, so both share the one listener. There is no middleware and no routing
// framework; the store is only read, never locked across a network write, and segment bytes are written straight
// from the store's immutable buffers.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using RTSP.IngestService.Configuration;
using RTSP.IngestService.Hls;
using RTSP.IngestService.Management;

namespace RTSP.IngestService.Http;

/// <summary>Serves HLS resources from an <see cref="IHlsSegmentStore"/>.</summary>
/// <remarks>
/// <para>
/// URLs: <c>/hls/{application}/{stream}/index.m3u8</c>, <c>.../{generation}/init-{n}.mp4</c> and
/// <c>.../{generation}/segment-{n}.m4s</c>. The stream is addressed by its non-secret identity, never by a stream key
/// or session identifier. The playlist URL is stable; the segment URLs it lists carry the store's
/// <see cref="IHlsSegmentStore.Generation"/>, and a segment URL of any other generation (an earlier process) is 404
/// even if the current process has a segment of the same name.
/// </para>
/// <para>
/// Only <c>GET</c> and <c>HEAD</c> are allowed (405 otherwise). Missing resources are 404 with
/// <c>Cache-Control: no-store</c>. Byte ranges are not supported (<c>Accept-Ranges: none</c>; a <c>Range</c> header
/// is ignored and the full resource returned). <c>If-None-Match</c> is honoured after the availability check, so a
/// withdrawn or expired resource is never 304. A stored segment no other response is sending is 503 with
/// <c>Retry-After</c> while the store's reader memory budget is exhausted by responses still in progress.
/// </para>
/// <para>
/// Bodies are written in chunks of <see cref="ChunkSize"/>, each awaited, so a slow viewer only holds back its own
/// request and at most one chunk is buffered per response beyond Kestrel's output limits. The written memory belongs
/// to the store's immutable segment, which stays valid after eviction, so expiry never breaks a response in progress.
/// </para>
/// </remarks>
internal sealed partial class HlsHttpApplication : IHttpApplication<HttpContext>
{
    /// <summary>Largest body write awaited at once.</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>Path prefix of every origin resource.</summary>
    public const string PathPrefix = "/hls/";

    /// <summary>Media type of playlists (RFC 8216, section 4).</summary>
    public const string PlaylistContentType = "application/vnd.apple.mpegurl";

    /// <summary>Media type of initialization segments.</summary>
    public const string InitializationContentType = "video/mp4";

    /// <summary>Media type of fragmented MP4 media segments.</summary>
    public const string SegmentContentType = "video/iso.segment";

    /// <summary>Cache policy of playlists: storable but revalidated on every use.</summary>
    public const string PlaylistCacheControl = "no-cache";

    /// <summary>Cache policy of errors.</summary>
    public const string ErrorCacheControl = "no-store";

    /// <summary>The <c>Allow</c> value of 405 responses.</summary>
    public const string AllowedMethods = "GET, HEAD";

    /// <summary>The <c>Retry-After</c> seconds of 503 responses refused by the reader memory budget.</summary>
    public const string ReaderMemoryRetryAfter = "1";

    /// <summary>The segment store.</summary>
    private readonly IHlsSegmentStore _store;

    /// <summary>Metrics.</summary>
    private readonly HttpOriginMetrics _metrics;

    /// <summary>Clock for request durations.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Cache policy of initialization and media segments.</summary>
    private readonly string _segmentCacheControl;

    /// <summary>Random per-process prefix of entity tags, so tags never match across restarts.</summary>
    private readonly string _epoch;

    /// <summary>Management routes served on the same listener, or <see langword="null"/> when management is disabled.</summary>
    private readonly ManagementEndpoints? _management;

    /// <summary>Initializes a new instance of the <see cref="HlsHttpApplication"/> class.</summary>
    /// <param name="store">The segment store.</param>
    /// <param name="hlsOptions">The HLS options, which bound segment cache lifetimes.</param>
    /// <param name="metrics">Metrics.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="management">
    /// Management routes to serve for paths under <see cref="ManagementEndpoints.PathPrefix"/>, or
    /// <see langword="null"/> to treat those paths as unknown HLS paths.
    /// </param>
    public HlsHttpApplication(IHlsSegmentStore store, IOptions<HlsOptions> hlsOptions, HttpOriginMetrics metrics, TimeProvider timeProvider, ILogger<HlsHttpApplication> logger, ManagementEndpoints? management = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hlsOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
        _segmentCacheControl = $"public, max-age={(long)SegmentCacheLifetime(hlsOptions.Value).TotalSeconds}";
        _epoch = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        _management = management;
    }

    /// <summary>
    /// Returns how long caches may keep a segment: an upper bound on how long the origin serves it, which is the time
    /// it is listed plus its RFC 8216 availability period, plus the ended-stream retention.
    /// </summary>
    /// <param name="options">The HLS options.</param>
    /// <returns>The ended-stream retention + maximum segment duration × (2 × max(window, 4) + 1).</returns>
    /// <remarks>
    /// A playlist lasts at most max(window, 4) maximum-length segments (the window, or the three target durations
    /// RFC 8216 requires plus the segment that reaches them). A segment is listed for at most one playlist duration and
    /// served after removal for its own duration plus the longest playlist. Segment URLs never name different media
    /// within a process, so the bound only limits how long a cache can outlive a restart.
    /// </remarks>
    internal static TimeSpan SegmentCacheLifetime(HlsOptions options) =>
        options.EndedStreamRetention + (options.MaxSegmentDuration * ((2 * Math.Max(options.PlaylistWindowSize, 4)) + 1));

    /// <inheritdoc />
    public HttpContext CreateContext(IFeatureCollection contextFeatures) => new DefaultHttpContext(contextFeatures);

    /// <inheritdoc />
    public void DisposeContext(HttpContext context, Exception? exception)
    {
    }

    /// <inheritdoc />
    public async Task ProcessRequestAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_management is not null && ManagementEndpoints.IsManagementPath(context.Request.Path.Value))
        {
            await _management.ServeAsync(context).ConfigureAwait(false);
            return;
        }

        var start = _timeProvider.GetTimestamp();
        _metrics.RequestStarted();
        var route = Route.Parse(context.Request.Path.Value);
        try
        {
            await ServeAsync(context, route).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            _metrics.Cancelled(route.Resource);
            context.Abort();
        }
        catch (Exception ex)
        {
            _metrics.Failed(route.Resource);
            LogRequestFailed(HttpOriginMetrics.ResourceTag(route.Resource), ex);
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
            else
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.Headers.CacheControl = ErrorCacheControl;
            }
        }
        finally
        {
            _metrics.RequestCompleted(route.Resource, context.Response.StatusCode, _timeProvider.GetElapsedTime(start));
        }
    }

    /// <summary>Answers a request.</summary>
    /// <param name="context">The request.</param>
    /// <param name="route">The parsed path.</param>
    /// <returns>A task that completes when the response is written.</returns>
    private async Task ServeAsync(HttpContext context, Route route)
    {
        var response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        if (route.Resource == HttpOriginResource.Other)
        {
            NotFound(response, route.Resource, HttpOriginMissReason.InvalidPath);
            return;
        }

        var method = context.Request.Method;
        var head = HttpMethods.IsHead(method);
        if (!head && !HttpMethods.IsGet(method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = AllowedMethods;
            response.Headers.CacheControl = ErrorCacheControl;
            return;
        }

        if (route.Resource == HttpOriginResource.Playlist)
        {
            var snapshot = _store.GetPlaylist(route.Application, route.Stream);
            if (snapshot is null)
            {
                NotFound(response, route.Resource, _store.ContainsStream(route.Application, route.Stream) ? HttpOriginMissReason.PlaylistWithdrawn : HttpOriginMissReason.UnknownStream);
                return;
            }

            response.ContentType = PlaylistContentType;
            response.Headers.CacheControl = PlaylistCacheControl;
            if (!NotModified(context, $"\"{_epoch}-p{snapshot.Version:x}\""))
            {
                await WriteBodyAsync(context, route.Resource, Encoding.UTF8.GetBytes(snapshot.Text), head).ConfigureAwait(false);
            }

            return;
        }

        HlsSegmentLease? segment = null;
        var access = string.Equals(route.Generation, _store.Generation, StringComparison.Ordinal)
            ? _store.TryAcquireSegment(route.Application, route.Stream, route.File, out segment)
            : HlsSegmentAccess.NotStored;
        using var lease = segment;
        if (access == HlsSegmentAccess.ReaderMemoryExhausted)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.Headers.RetryAfter = ReaderMemoryRetryAfter;
            response.Headers.CacheControl = ErrorCacheControl;
            return;
        }

        if (segment is null)
        {
            NotFound(response, route.Resource, _store.ContainsStream(route.Application, route.Stream) ? HttpOriginMissReason.SegmentUnavailable : HttpOriginMissReason.UnknownStream);
            return;
        }

        response.ContentType = route.Resource == HttpOriginResource.Initialization ? InitializationContentType : SegmentContentType;
        response.Headers.CacheControl = _segmentCacheControl;
        if (!NotModified(context, $"\"{_epoch}-{segment.Name}\""))
        {
            StartBody(response, segment.Length);
            for (var i = 0; !head && i < segment.PartCount; i++)
            {
                await WriteChunksAsync(context, route.Resource, segment.GetPart(i)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sets the entity tag and answers 304 if the request's <c>If-None-Match</c> matches it.</summary>
    /// <param name="context">The request.</param>
    /// <param name="entityTag">The quoted entity tag of the current representation.</param>
    /// <returns><see langword="true"/> if the response is 304.</returns>
    private static bool NotModified(HttpContext context, string entityTag)
    {
        context.Response.Headers.ETag = entityTag;
        var values = context.Request.Headers.IfNoneMatch;
        if (values.Count == 0 || !EntityTagHeaderValue.TryParseList(values, out var tags))
        {
            return false;
        }

        var current = new EntityTagHeaderValue(entityTag);
        foreach (var tag in tags)
        {
            if (tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return true;
            }
        }

        return false;
    }

    /// <summary>Answers 404.</summary>
    /// <param name="response">The response.</param>
    /// <param name="resource">The resource kind.</param>
    /// <param name="reason">Why it is missing.</param>
    private void NotFound(HttpResponse response, HttpOriginResource resource, HttpOriginMissReason reason)
    {
        response.StatusCode = StatusCodes.Status404NotFound;
        response.Headers.CacheControl = ErrorCacheControl;
        _metrics.Missing(resource, reason);
    }

    /// <summary>Writes a 200 response body in awaited chunks, honouring response backpressure and cancellation.</summary>
    /// <param name="context">The request.</param>
    /// <param name="resource">The resource kind.</param>
    /// <param name="data">The body; must stay valid and unchanged while it is written.</param>
    /// <param name="head">Whether only the headers are sent.</param>
    /// <returns>A task that completes when the body is written.</returns>
    private async Task WriteBodyAsync(HttpContext context, HttpOriginResource resource, ReadOnlyMemory<byte> data, bool head)
    {
        StartBody(context.Response, data.Length);
        if (!head)
        {
            await WriteChunksAsync(context, resource, data).ConfigureAwait(false);
        }
    }

    /// <summary>Sets the status and headers of a 200 response.</summary>
    /// <param name="response">The response.</param>
    /// <param name="length">The body length.</param>
    private static void StartBody(HttpResponse response, int length)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentLength = length;
        response.Headers.AcceptRanges = "none";
    }

    /// <summary>
    /// Writes body bytes in awaited chunks, honouring response backpressure and cancellation. Each write copies the
    /// chunk into the response pipe before it completes, so <paramref name="data"/> is not used after this returns.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="resource">The resource kind.</param>
    /// <param name="data">The bytes; must stay valid and unchanged until this completes.</param>
    /// <returns>A task that completes when the bytes are written.</returns>
    private async Task WriteChunksAsync(HttpContext context, HttpOriginResource resource, ReadOnlyMemory<byte> data)
    {
        var response = context.Response;
        var cancellationToken = context.RequestAborted;
        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            var chunk = data.Slice(offset, Math.Min(ChunkSize, data.Length - offset));
            var result = await response.BodyWriter.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);

            // After an abort Kestrel completes the output and discards writes; RequestAborted is signalled later.
            if (result.IsCompleted || result.IsCanceled)
            {
                throw new OperationCanceledException("The response output was closed.");
            }

            _metrics.BytesWritten(resource, chunk.Length);
        }
    }

    /// <summary>Matches a stream path segment (the validated application and identity name pattern).</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Matches a generation path segment.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GenerationPattern();

    /// <summary>Matches an initialization segment file name.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^init-[0-9]{1,10}\\.mp4$", RegexOptions.CultureInvariant)]
    private static partial Regex InitializationPattern();

    /// <summary>Matches a media segment file name.</summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex("^segment-[0-9]{1,19}\\.m4s$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    /// <summary>Logs an unexpected request failure, without request identifiers.</summary>
    /// <param name="resource">The resource kind tag.</param>
    /// <param name="exception">The failure.</param>
    [LoggerMessage(EventId = 8001, Level = LogLevel.Error, Message = "HTTP origin request for a {Resource} resource failed")]
    private partial void LogRequestFailed(string resource, Exception exception);

    /// <summary>A parsed request path.</summary>
    /// <param name="Resource">The resource kind; <see cref="HttpOriginResource.Other"/> if the path is not an origin URL.</param>
    /// <param name="Application">The application.</param>
    /// <param name="Stream">The stream identity.</param>
    /// <param name="Generation">The segment generation; empty for playlists.</param>
    /// <param name="File">The file name.</param>
    internal readonly record struct Route(HttpOriginResource Resource, string Application, string Stream, string Generation, string File)
    {
        /// <summary>Parses a request path.</summary>
        /// <param name="path">The decoded path.</param>
        /// <returns>The route; <see cref="HttpOriginResource.Other"/> if the path is not an origin URL.</returns>
        public static Route Parse(string? path)
        {
            var invalid = new Route(HttpOriginResource.Other, string.Empty, string.Empty, string.Empty, string.Empty);
            if (path is null || !path.StartsWith(PathPrefix, StringComparison.Ordinal))
            {
                return invalid;
            }

            var parts = path[PathPrefix.Length..].Split('/');
            if (parts.Length is < 3 or > 4 || !NamePattern().IsMatch(parts[0]) || !NamePattern().IsMatch(parts[1]))
            {
                return invalid;
            }

            var file = parts[^1];
            if (parts.Length == 3)
            {
                return file == "index.m3u8" ? new Route(HttpOriginResource.Playlist, parts[0], parts[1], string.Empty, file) : invalid;
            }

            var resource = !GenerationPattern().IsMatch(parts[2]) ? HttpOriginResource.Other
                : InitializationPattern().IsMatch(file) ? HttpOriginResource.Initialization
                : SegmentPattern().IsMatch(file) ? HttpOriginResource.Segment
                : HttpOriginResource.Other;
            return resource == HttpOriginResource.Other ? invalid : new Route(resource, parts[0], parts[1], parts[2], file);
        }
    }
}
