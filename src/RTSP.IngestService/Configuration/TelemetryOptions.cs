// Telemetry export settings bound from the "Telemetry" configuration section. Only OTLP metrics export exists, under
// "Telemetry:Otlp"; it is disabled by default. The endpoint may not carry credentials, a query or a fragment, so the
// configured value contains nothing secret.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Settings for exporting the service's telemetry (<see cref="Telemetry.TelemetryServiceCollectionExtensions"/>).
/// </summary>
/// <remarks>The options are read once at startup; changing them requires a restart.</remarks>
public sealed class TelemetryOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>Gets or sets the OpenTelemetry Protocol (OTLP) metrics exporter settings.</summary>
    [ValidateObjectMembers]
    public OtlpOptions Otlp { get; set; } = new();
}

/// <summary>
/// Settings for the OpenTelemetry Protocol (OTLP) metrics exporter. Only metrics are exported; there is no trace or log
/// export.
/// </summary>
public sealed class OtlpOptions : IValidatableObject
{
    /// <summary>The OTLP/gRPC protocol value of <see cref="Protocol"/>.</summary>
    public const string Grpc = "grpc";

    /// <summary>The OTLP/HTTP binary protobuf protocol value of <see cref="Protocol"/>.</summary>
    public const string HttpProtobuf = "http/protobuf";

    /// <summary>
    /// Gets or sets a value indicating whether metrics are exported. Defaults to <see langword="false"/>: no exporter is
    /// created and no connection is made.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the collector's base endpoint, for example <c>http://otel-collector:4317</c> for
    /// <see cref="Grpc"/> or <c>http://otel-collector:4318</c> for <see cref="HttpProtobuf"/>, where
    /// <c>/v1/metrics</c> is appended to the path. Required when <see cref="Enabled"/>; when set it must be an absolute
    /// <c>http</c> or <c>https</c> URI without user information, query or fragment.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>Gets or sets the transport: <c>grpc</c> (the default) or <c>http/protobuf</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [AllowedValues(Grpc, HttpProtobuf)]
    public string Protocol { get; set; } = Grpc;

    /// <summary>
    /// Gets or sets how often metrics are collected and exported. Defaults to 15 seconds; valid range is one second to
    /// five minutes.
    /// </summary>
    [TimeSpanRange("00:00:01", "00:05:00")]
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Validates <see cref="Endpoint"/>.</summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>A failure when an enabled exporter has no endpoint or the endpoint is not acceptable.</returns>
    /// <remarks>The endpoint value is never included in a failure message.</remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            if (Enabled)
            {
                yield return new ValidationResult($"{nameof(Endpoint)} is required when {nameof(Enabled)} is true.", [nameof(Endpoint)]);
            }

            yield break;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri))
        {
            yield return new ValidationResult($"{nameof(Endpoint)} must be an absolute URI.", [nameof(Endpoint)]);
        }
        else if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            yield return new ValidationResult($"{nameof(Endpoint)} must use the http or https scheme.", [nameof(Endpoint)]);
        }
        else if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            yield return new ValidationResult($"{nameof(Endpoint)} must not contain user information, a query or a fragment.", [nameof(Endpoint)]);
        }
    }
}
