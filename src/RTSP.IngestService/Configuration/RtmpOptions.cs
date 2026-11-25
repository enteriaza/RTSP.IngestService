// RTMP ingest listener and protocol limits bound from the "Rtmp" configuration section. Every value is validated at
// startup, before the listener binds, and invalid values are rejected rather than clamped.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for the RTMP ingest listener (<see cref="Rtmp.RtmpListener"/>) and the per-connection protocol limits.
/// </summary>
/// <remarks>
/// The options are read once when the listener starts and changing them requires a restart, except
/// <see cref="RtmpAuthenticationOptions.Publishers"/>, which is reloaded when the configuration changes (see
/// <see cref="Rtmp.Publishing.PublisherCredentialStore"/>). A reload is validated as a whole, so an invalid value
/// anywhere in the section keeps the previous credentials.
/// </remarks>
public sealed class RtmpOptions : IValidatableObject
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Rtmp";

    /// <summary>
    /// Gets or sets the literal IP address the listener binds to. Defaults to <c>0.0.0.0</c> (all IPv4 interfaces).
    /// <c>::</c> binds all IPv6 interfaces and, where the operating system supports dual-mode sockets, IPv4 as well.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [IPAddress]
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Gets or sets the TCP port. Defaults to 1935, the registered RTMP port; valid range is 0 to 65535, where 0 lets
    /// the operating system choose a free port (used by tests).
    /// </summary>
    [Range(0, 65535)]
    public int Port { get; set; } = 1935;

    /// <summary>
    /// Gets or sets the maximum number of concurrent TCP connections, including connections still handshaking.
    /// Connections beyond the limit are closed immediately after they are accepted. Defaults to 100; valid range is
    /// 1 to 10000.
    /// </summary>
    [Range(1, 10000)]
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum time from accepting a connection to completing the RTMP handshake. Defaults to
    /// 10 seconds; valid range is one second to five minutes.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:05:00")]
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the inactivity limit after the handshake: the connection is closed when no data arrives from the
    /// peer within this time, or when a write to the peer does not complete within it. Time spent waiting for the
    /// publish session handler (backpressure) does not count. Defaults to 30 seconds; valid range is one second to
    /// one hour.
    /// </summary>
    [TimeSpanRange("00:00:01", "01:00:00")]
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the largest RTMP message, in bytes, the server accepts. A chunk header declaring a longer message
    /// closes the connection before any memory is reserved for it. Defaults to 8 MiB; valid range is 128 bytes to
    /// 16777215 bytes (the largest length an RTMP message header can express).
    /// </summary>
    [Range(128, 16777215)]
    public int MaxMessageSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the chunk size assumed for data from the peer until it sends Set Chunk Size. The RTMP
    /// specification fixes this at 128, which standard publishers rely on; change it only for peers that are known
    /// to use a different initial size. Valid range is 128 to 65536.
    /// </summary>
    [Range(128, 65536)]
    public int InboundChunkSize { get; set; } = 128;

    /// <summary>
    /// Gets or sets the chunk size the server announces with Set Chunk Size during <c>connect</c> and then uses for
    /// everything it sends. Defaults to 4096; valid range is 128 to 65536.
    /// </summary>
    [Range(128, 65536)]
    public int OutboundChunkSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the maximum number of bytes a single connection may hold in partially reassembled messages across
    /// all of its chunk streams. Exceeding it closes the connection. Must be at least <see cref="MaxMessageSize"/>.
    /// Defaults to 16 MiB; valid range is 64 KiB to 256 MiB.
    /// </summary>
    [Range(65536, 268435456)]
    public int MaxBufferedBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the publisher credentials and failure rate limit of the configuration-based publisher authenticator.
    /// </summary>
    [ValidateObjectMembers]
    public RtmpAuthenticationOptions Authentication { get; set; } = new();

    /// <summary>Validates constraints that involve more than one property.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>A failure when <see cref="MaxBufferedBytes"/> is less than <see cref="MaxMessageSize"/>.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxBufferedBytes < MaxMessageSize)
        {
            yield return new ValidationResult(
                $"{nameof(MaxBufferedBytes)} ({MaxBufferedBytes}) must be at least {nameof(MaxMessageSize)} ({MaxMessageSize}).",
                [nameof(MaxBufferedBytes), nameof(MaxMessageSize)]);
        }
    }
}
