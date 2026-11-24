// Management response bodies and their System.Text.Json source-generated serialization context, which keeps the
// management endpoints free of reflection-based serialization under Native AOT.

using System.Text.Json.Serialization;

namespace RTSP.IngestService.Management;

/// <summary>Body of a liveness or readiness response.</summary>
/// <param name="Status"><c>live</c>, <c>not_live</c>, <c>ready</c> or <c>not_ready</c>.</param>
/// <param name="State">The application lifecycle state name.</param>
/// <param name="RtmpListener">Whether the RTMP listener is bound; readiness only.</param>
/// <param name="HttpListener">Whether the HTTP listener is bound; readiness only.</param>
internal sealed record HealthReport(string Status, string State, bool? RtmpListener, bool? HttpListener);

/// <summary>Source-generated JSON serialization for management responses (camelCase, nulls omitted).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthReport))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext;
