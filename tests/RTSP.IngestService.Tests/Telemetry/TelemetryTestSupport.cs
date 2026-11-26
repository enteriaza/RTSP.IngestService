// Shared support for the telemetry export tests: a collection that runs alone (the OpenTelemetry SDK subscribes to
// meters by name across the whole process, so a concurrently running host would add its measurements), a loopback fake
// OTLP/HTTP collector that either answers or never answers, an endpoint that refuses connections, and the fixed tag
// vocabulary of every application instrument.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RTSP.IngestService.Tests.Telemetry;

/// <summary>The non-parallel collection of telemetry export tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "Telemetry export";
}

/// <summary>A loopback TCP server that records OTLP/HTTP requests and answers them with 200, or never answers.</summary>
internal sealed class FakeCollector : IAsyncDisposable
{
    /// <summary>The listener.</summary>
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    /// <summary>Whether requests are answered.</summary>
    private readonly bool _respond;

    /// <summary>Stops the accept loop and releases held connections.</summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Received requests.</summary>
    private readonly ConcurrentQueue<CollectorRequest> _requests = new();

    /// <summary>Completed by the first complete request.</summary>
    private readonly TaskCompletionSource<CollectorRequest> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Connection handlers.</summary>
    private readonly ConcurrentBag<Task> _handlers = [];

    /// <summary>The accept loop.</summary>
    private readonly Task _accept;

    /// <summary>Accepted connections.</summary>
    private int _connections;

    /// <summary>Initializes a new instance of the <see cref="FakeCollector"/> class and starts listening.</summary>
    /// <param name="respond">Whether requests are answered; otherwise each connection is held open unanswered.</param>
    public FakeCollector(bool respond)
    {
        _respond = respond;
        _listener.Start();
        _accept = AcceptAsync();
    }

    /// <summary>Gets the collector's base endpoint.</summary>
    public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Gets the number of accepted connections.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Gets the received requests.</summary>
    public IReadOnlyCollection<CollectorRequest> Requests => _requests;

    /// <summary>Gets a task that completes with the first complete request.</summary>
    public Task<CollectorRequest> FirstRequest => _first.Task;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.WhenAll(_handlers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    /// <summary>Accepts connections until stopped.</summary>
    /// <returns>A task.</returns>
    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _handlers.Add(HandleAsync(client));
        }
    }

    /// <summary>Reads requests from one connection and answers or holds them.</summary>
    /// <param name="client">The connection.</param>
    /// <returns>A task.</returns>
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (await ReadRequestAsync(stream) is { } request)
                {
                    _requests.Enqueue(request);
                    _first.TrySetResult(request);
                    if (!_respond)
                    {
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }

                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Reads one HTTP/1.1 request with a Content-Length body.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <returns>The request, or <see langword="null"/> at end of stream.</returns>
    private async Task<CollectorRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                return null;
            }

            head.Add(one[0]);
        }

        var text = Encoding.ASCII.GetString([.. head]);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = lines.Skip(1).Select(static l => l.Split(':', 2)).ToDictionary(static p => p[0].Trim(), static p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var body = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture) : 0];
        await stream.ReadExactlyAsync(body, _stop.Token);
        return new CollectorRequest(lines[0], headers.GetValueOrDefault("Content-Type"), body);
    }
}

/// <summary>A request received by <see cref="FakeCollector"/>.</summary>
/// <param name="RequestLine">The request line, for example <c>POST /v1/metrics HTTP/1.1</c>.</param>
/// <param name="ContentType">The content type.</param>
/// <param name="Body">The body.</param>
internal sealed record CollectorRequest(string RequestLine, string? ContentType, byte[] Body);

/// <summary>Telemetry test helpers.</summary>
internal static class TelemetryTestSupport
{
    /// <summary>Every application instrument, by name, with each tag key it may carry and that key's fixed values.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> Vocabulary = Build();

    /// <summary>Returns a loopback endpoint whose port was just released, so connecting to it is refused.</summary>
    /// <returns>The endpoint.</returns>
    public static string RefusingEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    /// <summary>Returns configuration overrides that enable OTLP export.</summary>
    /// <param name="endpoint">The collector endpoint.</param>
    /// <param name="protocol">The protocol.</param>
    /// <param name="interval">The export interval.</param>
    /// <returns>The overrides.</returns>
    public static Dictionary<string, string?> Enabled(string endpoint, string protocol = "http/protobuf", string interval = "00:05:00") => new()
    {
        ["Telemetry:Otlp:Enabled"] = "true",
        ["Telemetry:Otlp:Endpoint"] = endpoint,
        ["Telemetry:Otlp:Protocol"] = protocol,
        ["Telemetry:Otlp:ExportInterval"] = interval,
    };

    /// <summary>Builds <see cref="Vocabulary"/> from the instruments and tag values the service defines.</summary>
    /// <returns>The vocabulary.</returns>
    private static Dictionary<string, IReadOnlyDictionary<string, string[]>> Build()
    {
        string[] track = ["video", "audio"];
        string[] mode = ["unicast", "multicast"];
        string[] resource = ["playlist", "initialization", "segment", "other"];
        string[] eventType = ["published", "media_described", "ended", "playlist_withdrawn", "drained"];
        static IReadOnlyDictionary<string, string[]> None() => new Dictionary<string, string[]>();
        static IReadOnlyDictionary<string, string[]> Tags(params (string Key, string[] Values)[] tags) => tags.ToDictionary(static t => t.Key, static t => t.Values);

        return new Dictionary<string, IReadOnlyDictionary<string, string[]>>
        {
            ["rtmp.connections.accepted"] = None(),
            ["rtmp.connections.rejected"] = Tags(("reason", ["connection_limit"])),
            ["rtmp.connections.active"] = None(),
            ["rtmp.connection.duration"] = Tags(("close_reason", ["client_disconnected", "rejected", "server_shutdown", "handshake_timeout", "handshake_failed", "idle_timeout", "protocol_error", "transport_error", "handler_failed", "internal_error"])),
            ["rtmp.handshakes"] = Tags(("result", ["success", "failure"])),
            ["rtmp.authentication.failures"] = None(),
            ["rtmp.authentication.attempts"] = Tags(("result", ["success", "invalid_credential", "disabled", "ambiguous", "application_mismatch", "malformed_key", "rate_limited"])),
            ["rtmp.authentication.reloads"] = Tags(("result", ["success", "failure"])),
            ["rtmp.protocol_errors"] = None(),
            ["rtmp.sessions.active"] = None(),
            ["rtmp.publisher.disconnects"] = Tags(("reason", ["unpublished", "stream_deleted", "client_disconnected", "connection_timeout", "protocol_error", "connection_error", "server_shutdown", "handler_failed"])),
            ["rtmp.bytes_received"] = None(),
            ["rtmp.messages_received"] = Tags(("type", ["audio", "video", "data", "command", "control", "other"])),
            ["media.samples"] = Tags(("track", track)),
            ["media.keyframes"] = None(),
            ["media.sample_bytes"] = Tags(("track", track)),
            ["media.configurations"] = Tags(("track", track)),
            ["media.errors"] = Tags(("track", track), ("kind", ["unsupported_codec", "malformed_packet", "invalid_configuration", "missing_configuration", "sample_too_large", "too_many_nal_units"])),
            ["media.timestamp_discontinuities"] = Tags(("track", track), ("kind", ["regression", "jump"])),
            ["media.sessions.active"] = None(),
            ["hls.sessions.active"] = None(),
            ["hls.initialization_segments"] = None(),
            ["hls.segments"] = Tags(("boundary", ["keyframe", "forced", "configuration", "discontinuity", "final"])),
            ["hls.segment_bytes"] = None(),
            ["hls.segment.duration"] = None(),
            ["hls.keyframe_wait"] = None(),
            ["hls.storage.bytes"] = None(),
            ["hls.storage.evictions"] = Tags(("reason", ["retired", "withdrawn", "unreferenced"])),
            ["hls.errors"] = Tags(("kind", ["unsupported_track", "invalid_track", "segment_too_large", "storage_full", "playlist_full"])),
            ["hls.discontinuities"] = Tags(("reason", ["timestamp", "configuration", "timeline_overlap", "dropped_media"])),
            ["hls.samples.dropped"] = Tags(("track", track), ("reason", ["before_sync_sample", "untracked", "too_large", "storage_full", "playlist_full", "timeline_overlap", "aborted"])),
            ["hls.pending.bytes"] = None(),
            ["hls.readers.bytes"] = None(),
            ["hls.readers.refused"] = None(),
            ["hls.segment_pool.bytes"] = Tags(("state", ["in_use", "free"])),
            ["rtp.sessions.active"] = Tags(("mode", mode)),
            ["rtp.packets.sent"] = Tags(("track", track), ("mode", mode)),
            ["rtp.bytes.sent"] = Tags(("track", track), ("mode", mode)),
            ["rtp.packets.dropped"] = Tags(("track", track), ("reason", ["queue_full", "awaiting_keyframe", "too_large", "send_failed", "output_unavailable"])),
            ["rtp.send.errors"] = Tags(("kind", ["transient", "terminal"])),
            ["rtp.rtcp.sent"] = None(),
            ["rtp.bitrate"] = None(),
            ["rtp.packet_rate"] = None(),
            ["http.origin.requests"] = Tags(("resource", resource), ("status_class", ["1xx", "2xx", "3xx", "4xx", "5xx"])),
            ["http.origin.requests.active"] = None(),
            ["http.origin.request.duration"] = Tags(("resource", resource)),
            ["http.origin.response_bytes"] = Tags(("resource", resource)),
            ["http.origin.cancellations"] = Tags(("resource", resource)),
            ["http.origin.missing"] = Tags(("resource", resource), ("reason", ["invalid_path", "unknown_stream", "playlist_withdrawn", "segment_unavailable"])),
            ["http.origin.failures"] = Tags(("resource", resource)),
            ["stream_events.published"] = Tags(("type", eventType)),
            ["stream_events.dropped"] = Tags(("type", eventType)),
            ["stream_events.rejected"] = Tags(("type", eventType)),
            ["stream_events.consumer_failures"] = Tags(("type", eventType)),
            ["stream_catalog.reconciliations"] = Tags(("result", ["success", "failure"])),
            ["stream_catalog.reconciliation.duration"] = None(),
            ["stream_catalog.reconciliation.requests"] = Tags(("reason", ["startup", "periodic", "event", "loss", "inconsistency"])),
            ["stream_catalog.reconciliation.coalesced"] = None(),
            ["stream_catalog.streams"] = None(),
            ["stream_catalog.reconciliation.age"] = None(),
        };
    }
}
