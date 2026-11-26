// Hosts the HTTP HLS origin: a Kestrel server owned by a hosted service in the service's existing Generic Host, so
// there is no second host or web application builder. StartAsync binds, so a bind failure fails host startup and
// readiness (ApplicationStarted) is never reported before the port is open. StopAsync stops accepting connections
// and lets in-flight requests finish until the host shutdown token fires, then aborts the rest.

using System.Net;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Http;

/// <summary>
/// Serves HLS playback over HTTP/1.1 on <see cref="HttpOriginOptions.ListenAddress"/>:<see cref="HttpOriginOptions.Port"/>.
/// </summary>
/// <remarks>
/// Kestrel enforces <see cref="HttpOriginOptions.MaxConnections"/>, the header and keep-alive timeouts, and its default
/// minimum response data rate, which disconnects a viewer that stops reading. Plain HTTP only; there is no
/// <c>Server</c> header.
/// </remarks>
public sealed partial class HlsHttpOrigin : IHostedService, IAsyncDisposable
{
    /// <summary>The HTTP options.</summary>
    private readonly IOptions<HttpOriginOptions> _options;

    /// <summary>The request pipeline.</summary>
    private readonly HlsHttpApplication _application;

    /// <summary>Services exposed to Kestrel.</summary>
    private readonly IServiceProvider _services;

    /// <summary>Logger factory for Kestrel.</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Logger.</summary>
    private readonly ILogger<HlsHttpOrigin> _logger;

    /// <summary>The running server, set by <see cref="StartAsync"/>.</summary>
    private KestrelServer? _server;

    /// <summary>Whether the listener is bound and accepting; see <see cref="IsListening"/>.</summary>
    private volatile bool _listening;

    /// <summary>Initializes a new instance of the <see cref="HlsHttpOrigin"/> class.</summary>
    /// <param name="options">The HTTP options.</param>
    /// <param name="application">The request pipeline.</param>
    /// <param name="services">Services exposed to Kestrel.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    internal HlsHttpOrigin(IOptions<HttpOriginOptions> options, HlsHttpApplication application, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _application = application;
        _services = services;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HlsHttpOrigin>();
    }

    /// <summary>Gets the bound local endpoint (with the actual port when <see cref="HttpOriginOptions.Port"/> is 0), or <see langword="null"/> before start.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the listener is bound and accepting connections: <see langword="true"/> from a
    /// successful <see cref="StartAsync"/> until <see cref="StopAsync"/> begins.
    /// </summary>
    public bool IsListening => _listening;

    /// <summary>Binds and starts serving.</summary>
    /// <param name="cancellationToken">The host startup token.</param>
    /// <returns>A task that completes once the listener is bound.</returns>
    /// <exception cref="InvalidOperationException">The listener could not be bound; the inner exception has the cause.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var address = IPAddress.Parse(options.ListenAddress);
        var kestrel = new KestrelServerOptions { ApplicationServices = _services, AddServerHeader = false };
        kestrel.Limits.MaxConcurrentConnections = options.MaxConnections;
        kestrel.Limits.MaxConcurrentUpgradedConnections = 0;
        kestrel.Limits.MaxRequestBodySize = 0;
        kestrel.Limits.RequestHeadersTimeout = options.RequestHeadersTimeout;
        kestrel.Limits.KeepAliveTimeout = options.KeepAliveTimeout;
        kestrel.Listen(address, options.Port, static listen => listen.Protocols = HttpProtocols.Http1);

        var transport = new SocketTransportFactory(Options.Create(new SocketTransportOptions()), _loggerFactory);
        var server = new KestrelServer(Options.Create(kestrel), transport, _loggerFactory);
        try
        {
            await server.StartAsync(_application, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            server.Dispose();
            throw new InvalidOperationException($"The HTTP origin could not listen on {options.ListenAddress}:{options.Port}.", ex);
        }

        _server = server;
        var bound = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        var port = bound is null ? options.Port : new Uri(bound).Port;
        LocalEndPoint = new IPEndPoint(address, port);
        LogListening(LocalEndPoint);
        LogHeaders(_application.Headers.CorsDescription, _application.Headers.ResourcePolicy);
        _listening = true;
    }

    /// <summary>Stops accepting connections and drains in-flight requests until <paramref name="cancellationToken"/> fires.</summary>
    /// <param name="cancellationToken">The host shutdown token.</param>
    /// <returns>A task that completes when the server has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _listening = false;
        if (_server is { } server)
        {
            await server.StopAsync(cancellationToken).ConfigureAwait(false);
            LogStopped();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _listening = false;
        _server?.Dispose();
        _server = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Logs that the origin is listening.</summary>
    /// <param name="endPoint">The bound endpoint.</param>
    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "HTTP HLS origin listening on {EndPoint}")]
    private partial void LogListening(IPEndPoint endPoint);

    /// <summary>Logs the CORS and resource policy of HLS responses.</summary>
    /// <param name="cors">The CORS policy.</param>
    /// <param name="resourcePolicy">The Cross-Origin-Resource-Policy value.</param>
    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "HTTP HLS origin CORS {Cors}; Cross-Origin-Resource-Policy {ResourcePolicy}")]
    private partial void LogHeaders(string cors, string resourcePolicy);

    /// <summary>Logs that the origin stopped.</summary>
    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "HTTP HLS origin stopped")]
    private partial void LogStopped();
}
