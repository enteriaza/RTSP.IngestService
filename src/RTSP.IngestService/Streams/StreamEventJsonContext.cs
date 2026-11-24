// Source-generated JSON serialization for the stream event contract, so it serializes without reflection under
// Native AOT. Property names are camelCase and absent optional fields are omitted.

using System.Text.Json.Serialization;

namespace RTSP.IngestService.Streams;

/// <summary>JSON serialization metadata for <see cref="StreamEvent"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StreamEvent))]
public sealed partial class StreamEventJsonContext : JsonSerializerContext;
