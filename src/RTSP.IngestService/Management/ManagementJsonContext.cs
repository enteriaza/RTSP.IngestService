// Management response bodies and their System.Text.Json source-generated serialization context, which keeps the
// management endpoints free of reflection-based serialization under Native AOT.

using System.Text.Json.Serialization;
using RTSP.IngestService.Streams;

namespace RTSP.IngestService.Management;

/// <summary>Body of a liveness or readiness response.</summary>
/// <param name="Status"><c>live</c>, <c>not_live</c>, <c>ready</c> or <c>not_ready</c>.</param>
/// <param name="State">The application lifecycle state name.</param>
/// <param name="RtmpListener">Whether the RTMP listener is bound; readiness only.</param>
/// <param name="HttpListener">Whether the HTTP listener is bound; readiness only.</param>
internal sealed record HealthReport(string Status, string State, bool? RtmpListener, bool? HttpListener);

/// <summary>Body of a stream inventory response.</summary>
/// <param name="Version">The catalog version the response was serialized from.</param>
/// <param name="ReconciledAt">When the catalog was last reconciled (UTC).</param>
/// <param name="Streams">The stream incarnations.</param>
internal sealed record StreamInventoryReport(long Version, DateTimeOffset? ReconciledAt, IReadOnlyList<StreamCatalogEntry> Streams);

/// <summary>Body of a single-stream response.</summary>
/// <param name="Version">The catalog version the response was serialized from.</param>
/// <param name="ReconciledAt">When the catalog was last reconciled (UTC).</param>
/// <param name="Stream">The stream's current incarnation.</param>
internal sealed record StreamDetailReport(long Version, DateTimeOffset? ReconciledAt, StreamCatalogEntry Stream);

/// <summary>Body of a stream inventory response while the catalog is not initialized.</summary>
/// <param name="Status"><c>initializing</c>.</param>
internal sealed record StreamInventoryStatus(string Status);

/// <summary>Body of the RTP output inventory.</summary>
/// <param name="Outputs">The configured outputs in configuration order.</param>
internal sealed record RtpOutputsReport(IReadOnlyList<RtpOutputReport> Outputs);

/// <summary>One configured RTP output.</summary>
/// <param name="Name">The output name.</param>
/// <param name="Application">The source application.</param>
/// <param name="Stream">The source stream identity.</param>
/// <param name="Mode"><c>unicast</c> or <c>multicast</c>.</param>
/// <param name="Destination">The destination address or group.</param>
/// <param name="VideoPort">The video RTP port, if video is sent.</param>
/// <param name="AudioPort">The audio RTP port, if audio is sent.</param>
/// <param name="Qos">The output's configured QoS marking.</param>
/// <param name="Session">The session sending the current publication, if the stream is published.</param>
internal sealed record RtpOutputReport(string Name, string Application, string Stream, string Mode, string Destination, int? VideoPort, int? AudioPort, RtpQosReport Qos, RtpSessionReport? Session);

/// <summary>The configured QoS marking of an RTP output; the values are omitted when marking is off.</summary>
/// <param name="Enabled">Whether the socket's ToS / Traffic Class is set.</param>
/// <param name="Dscp">The DSCP, when enabled.</param>
/// <param name="Ecn">The ECN codepoint, when enabled.</param>
/// <param name="TrafficClass">The resulting ToS / Traffic Class byte, when enabled.</param>
internal sealed record RtpQosReport(bool Enabled, int? Dscp, int? Ecn, int? TrafficClass);

/// <summary>The current session of an RTP output.</summary>
/// <param name="State">The session state, for example <c>active</c> or <c>failed</c>.</param>
/// <param name="StartedAt">When the session started (UTC).</param>
/// <param name="VideoSsrc">The video SSRC (hex), if video is sent.</param>
/// <param name="AudioSsrc">The audio SSRC (hex), once audio is configured.</param>
/// <param name="PacketsSent">RTP packets sent.</param>
/// <param name="BytesSent">RTP bytes sent.</param>
/// <param name="PacketsDropped">RTP packets not sent.</param>
/// <param name="SendErrors">Failed sends.</param>
/// <param name="RtcpPacketsSent">RTCP compound packets sent.</param>
/// <param name="Bitrate">Current send rate in bit/s.</param>
/// <param name="PacketRate">Current packet rate in packets/s.</param>
/// <param name="TrafficClass">The ToS / Traffic Class the session's socket reported after QoS marking was applied; absent when marking is off or the socket did not open.</param>
internal sealed record RtpSessionReport(string State, DateTimeOffset StartedAt, string? VideoSsrc, string? AudioSsrc, long PacketsSent, long BytesSent, long PacketsDropped, long SendErrors, long RtcpPacketsSent, double Bitrate, double PacketRate, int? TrafficClass);

/// <summary>Body of an RTP response that has no content to return.</summary>
/// <param name="Status"><c>not_publishing</c> or <c>not_described</c>.</param>
internal sealed record RtpStatus(string Status);

/// <summary>Source-generated JSON serialization for management responses (camelCase, nulls omitted).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RtpOutputsReport))]
[JsonSerializable(typeof(RtpStatus))]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(StreamInventoryReport))]
[JsonSerializable(typeof(StreamDetailReport))]
[JsonSerializable(typeof(StreamInventoryStatus))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext;
