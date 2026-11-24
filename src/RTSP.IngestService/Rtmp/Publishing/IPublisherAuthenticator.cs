// The publisher authentication boundary. The RTMP session asks the authenticator before any stream may enter the
// publishing state; the authenticator sees a strongly typed request built from the connect and publish commands and
// returns either a non-secret stream identity or a rejection. The protocol parser does not depend on it.

using System.Net;

namespace RTSP.IngestService.Rtmp.Publishing;

/// <summary>
/// Decides whether a publish request may proceed.
/// </summary>
/// <remarks>
/// Called once per <c>publish</c> command, from the connection's receive loop; implementations must be thread-safe
/// across connections. An exception is treated as a rejection and closes the connection.
/// </remarks>
public interface IPublisherAuthenticator
{
    /// <summary>Authenticates a publish request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancelled when the connection closes or the service stops.</param>
    /// <returns>The outcome.</returns>
    ValueTask<PublishAuthenticationResult> AuthenticateAsync(PublishAuthenticationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A publish request presented for authentication.
/// </summary>
/// <remarks>
/// <see cref="StreamName"/> and <see cref="StreamQuery"/> usually carry secrets (the encoder's stream key or a token)
/// and <see cref="TcUrl"/> may as well; none of them is logged by the server, and <see cref="ToString"/> omits them.
/// </remarks>
/// <param name="connectionId">The connection identifier.</param>
/// <param name="remoteEndPoint">The publisher's address.</param>
/// <param name="application">The application name from <c>connect</c>, without any query string.</param>
/// <param name="streamName">The stream name from <c>publish</c>, before any <c>?</c>.</param>
/// <param name="streamQuery">The part of the stream name after the first <c>?</c>, or <see langword="null"/>.</param>
/// <param name="tcUrl">The <c>tcUrl</c> from <c>connect</c>, or <see langword="null"/> if absent.</param>
/// <param name="publishType">The publishing type from <c>publish</c>.</param>
public sealed class PublishAuthenticationRequest(
    long connectionId,
    EndPoint? remoteEndPoint,
    string application,
    string streamName,
    string? streamQuery,
    string? tcUrl,
    string publishType)
{
    /// <summary>Gets the connection identifier.</summary>
    public long ConnectionId { get; } = connectionId;

    /// <summary>Gets the publisher's address, if known.</summary>
    public EndPoint? RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>Gets the application name.</summary>
    public string Application { get; } = application;

    /// <summary>Gets the stream name (for OBS and FFmpeg, the stream key). Secret.</summary>
    public string StreamName { get; } = streamName;

    /// <summary>Gets the stream name's query string, without the <c>?</c>. May be secret.</summary>
    public string? StreamQuery { get; } = streamQuery;

    /// <summary>Gets the <c>tcUrl</c> the client connected with. May contain secrets.</summary>
    public string? TcUrl { get; } = tcUrl;

    /// <summary>Gets the publishing type.</summary>
    public string PublishType { get; } = publishType;

    /// <summary>Describes the request without its secret-bearing members.</summary>
    /// <returns>The connection identifier, application and publishing type.</returns>
    public override string ToString() => $"Publish request on connection {ConnectionId} for application '{Application}' ({PublishType})";
}

/// <summary>
/// The outcome of <see cref="IPublisherAuthenticator.AuthenticateAsync"/>.
/// </summary>
public sealed class PublishAuthenticationResult
{
    /// <summary>Initializes a new instance of the <see cref="PublishAuthenticationResult"/> class.</summary>
    /// <param name="streamIdentity">The identity when authorized.</param>
    /// <param name="rejectionReason">The reason when rejected.</param>
    private PublishAuthenticationResult(string? streamIdentity, string? rejectionReason)
    {
        StreamIdentity = streamIdentity;
        RejectionReason = rejectionReason;
    }

    /// <summary>Gets a value indicating whether publishing is allowed.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(StreamIdentity))]
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(RejectionReason))]
    public bool IsAuthorized => StreamIdentity is not null;

    /// <summary>
    /// Gets the non-secret identity of the published stream when authorized. Sessions, logs and the registry use it in
    /// place of the stream name; at most one session per application and identity publishes at a time.
    /// </summary>
    public string? StreamIdentity { get; }

    /// <summary>Gets why the request was rejected. Logged, so it must not contain secrets.</summary>
    public string? RejectionReason { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="streamIdentity">The non-secret stream identity; 1 to 128 characters.</param>
    /// <returns>The result.</returns>
    public static PublishAuthenticationResult Authorize(string streamIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamIdentity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(streamIdentity.Length, 128, nameof(streamIdentity));
        return new PublishAuthenticationResult(streamIdentity, null);
    }

    /// <summary>Creates a rejection.</summary>
    /// <param name="reason">A non-secret reason, logged by the server.</param>
    /// <returns>The result.</returns>
    public static PublishAuthenticationResult Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new PublishAuthenticationResult(null, reason);
    }
}
