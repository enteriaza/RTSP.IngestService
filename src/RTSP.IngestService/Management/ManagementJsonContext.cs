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

/// <summary>Source-generated JSON serialization for management responses (camelCase, nulls omitted).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(StreamInventoryReport))]
[JsonSerializable(typeof(StreamDetailReport))]
[JsonSerializable(typeof(StreamInventoryStatus))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext;
