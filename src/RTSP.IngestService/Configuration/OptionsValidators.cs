// Compile-time generated data-annotation validators for every options type. The Microsoft.Extensions.Options source
// generator implements these partial classes without reflection, which keeps validation Native AOT compatible.

using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>Validates <see cref="ApplicationOptions"/> against its data annotations.</summary>
[OptionsValidator]
public sealed partial class ApplicationOptionsValidator : IValidateOptions<ApplicationOptions>;

/// <summary>Validates <see cref="LifetimeOptions"/> against its data annotations.</summary>
[OptionsValidator]
public sealed partial class LifetimeOptionsValidator : IValidateOptions<LifetimeOptions>;

/// <summary>Validates <see cref="HeartbeatOptions"/> against its data annotations.</summary>
[OptionsValidator]
public sealed partial class HeartbeatOptionsValidator : IValidateOptions<HeartbeatOptions>;

/// <summary>Validates <see cref="FileLoggingOptions"/> against its data annotations.</summary>
[OptionsValidator]
public sealed partial class FileLoggingOptionsValidator : IValidateOptions<FileLoggingOptions>;

/// <summary>
/// Validates <see cref="RtmpOptions"/>, including the nested <see cref="RtmpAuthenticationOptions"/> and each
/// <see cref="RtmpPublisherCredential"/>, against their data annotations and <see cref="System.ComponentModel.DataAnnotations.IValidatableObject"/> rules.
/// </summary>
[OptionsValidator]
public sealed partial class RtmpOptionsValidator : IValidateOptions<RtmpOptions>;

/// <summary>Validates <see cref="MediaOptions"/> against its data annotations.</summary>
[OptionsValidator]
public sealed partial class MediaOptionsValidator : IValidateOptions<MediaOptions>;

/// <summary>Validates <see cref="HlsOptions"/> against its data annotations and cross-property rules.</summary>
[OptionsValidator]
public sealed partial class HlsOptionsValidator : IValidateOptions<HlsOptions>;

/// <summary>Validates <see cref="HttpOriginOptions"/> against its data annotations.</summary>
[OptionsValidator]
public sealed partial class HttpOriginOptionsValidator : IValidateOptions<HttpOriginOptions>;
