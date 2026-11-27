// The transcoding stage's host lifecycle. At startup it reports whether publishes will be transcoded by the managed
// pipeline or rejected because transcoding is disabled; at shutdown, after the RTMP listener has ended its sessions,
// it stops any pipeline still running without draining.

using Microsoft.Extensions.Options;
using RTSP.IngestService.Configuration;

namespace RTSP.IngestService.Transcoding;

/// <summary>Reports the transcoding configuration at startup and aborts remaining pipelines when the host stops.</summary>
internal sealed partial class TranscodingLifecycleService : IHostedService
{
    /// <summary>The transcoding options.</summary>
    private readonly TranscodingOptions _options;

    /// <summary>The transcoding stage.</summary>
    private readonly TranscodingPublishSessionHandler _handler;

    /// <summary>Logger.</summary>
    private readonly ILogger<TranscodingLifecycleService> _logger;

    /// <summary>Initializes a new instance of the <see cref="TranscodingLifecycleService"/> class.</summary>
    /// <param name="options">The transcoding options.</param>
    /// <param name="handler">The transcoding stage.</param>
    /// <param name="logger">Logger.</param>
    public TranscodingLifecycleService(IOptions<TranscodingOptions> options, TranscodingPublishSessionHandler handler, ILogger<TranscodingLifecycleService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            LogReady();
        }
        else
        {
            LogDisabled();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _handler.AbortAllAsync();

    /// <summary>Logs that transcoding is disabled.</summary>
    [LoggerMessage(EventId = 10016, Level = LogLevel.Warning, Message = "Transcoding is disabled (Transcoding:Enabled is false); every publish is rejected because only canonical media may reach HLS")]
    private partial void LogDisabled();

    /// <summary>Logs that publishes are transcoded by the managed pipeline.</summary>
    [LoggerMessage(EventId = 10015, Level = LogLevel.Information, Message = "Transcoding uses the managed pipeline: H.264 video and AAC-LC audio are re-encoded to canonical H.264 High and AAC-LC; other codecs end the publish")]
    private partial void LogReady();
}
