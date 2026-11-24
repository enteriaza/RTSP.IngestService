// The failure raised by HLS packaging when a live stream can never publish another segment without breaking HLS
// rules: its listed segments fill its storage limit, or its playlist is at the entry cap and cannot shrink. It ends
// the publish through the media handler's existing failure path instead of leaving a playlist that cannot advance.

namespace RTSP.IngestService.Hls;

/// <summary>
/// Packaging of a stream stopped because no further segment can be published without releasing segments early or
/// shortening the live playlist below its minimum duration.
/// </summary>
public sealed class HlsPackagingStoppedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HlsPackagingStoppedException"/> class.</summary>
    public HlsPackagingStoppedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HlsPackagingStoppedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public HlsPackagingStoppedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HlsPackagingStoppedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public HlsPackagingStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HlsPackagingStoppedException"/> class.</summary>
    /// <param name="reason">Why: <see cref="HlsAppendResult.StreamLimitExceeded"/> or <see cref="HlsAppendResult.PlaylistFull"/>.</param>
    /// <param name="message">The message.</param>
    public HlsPackagingStoppedException(HlsAppendResult reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Gets why packaging stopped.</summary>
    public HlsAppendResult? Reason { get; }
}
