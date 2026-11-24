// Registers the strongly typed options with strict configuration binding, generated data-annotation validation and
// startup validation. Binding failures (unknown keys, unconvertible values) are surfaced as OptionsValidationException
// so that every configuration error is classified and reported the same way by the service runner.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace RTSP.IngestService.Configuration;

/// <summary>
/// Dependency-injection registration for the service's configuration options.
/// </summary>
public static class IngestServiceOptionsExtensions
{
    /// <summary>
    /// Binder settings shared by all sections: keys that do not map to a property are rejected so that typing errors
    /// in configuration files fail startup instead of being silently ignored.
    /// </summary>
    private static readonly Action<BinderOptions> StrictBinding = static binder => binder.ErrorOnUnknownConfiguration = true;

    /// <summary>
    /// Registers <see cref="ApplicationOptions"/>, <see cref="LifetimeOptions"/>,
    /// <see cref="FileLoggingOptions"/>, <see cref="RtmpOptions"/>, <see cref="MediaOptions"/>, <see cref="HlsOptions"/>, <see cref="HttpOriginOptions"/>, <see cref="ManagementOptions"/> and <see cref="StreamEventOptions"/>, each bound from its configuration section,
    /// validated when first resolved and validated eagerly when the host starts.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="configuration">The application configuration containing the option sections.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Each section is registered as a configuration change token source, so <see cref="IOptionsMonitor{TOptions}"/>
    /// consumers observe reloads. A reload that fails validation makes <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>
    /// throw <see cref="OptionsValidationException"/> until the configuration is corrected.
    /// </remarks>
    public static IServiceCollection AddIngestServiceOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Each bind delegate calls ConfigurationBinder.Bind with a concrete type so that the configuration binding
        // source generator can intercept it; a generic call site would fall back to reflection.
        AddValidatedOptions<ApplicationOptions, ApplicationOptionsValidator>(
            services, configuration, ApplicationOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<LifetimeOptions, LifetimeOptionsValidator>(
            services, configuration, LifetimeOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<FileLoggingOptions, FileLoggingOptionsValidator>(
            services, configuration, FileLoggingOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<RtmpOptions, RtmpOptionsValidator>(
            services, configuration, RtmpOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<MediaOptions, MediaOptionsValidator>(
            services, configuration, MediaOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<HlsOptions, HlsOptionsValidator>(
            services, configuration, HlsOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<HttpOriginOptions, HttpOriginOptionsValidator>(
            services, configuration, HttpOriginOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<ManagementOptions, ManagementOptionsValidator>(
            services, configuration, ManagementOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));
        AddValidatedOptions<StreamEventOptions, StreamEventOptionsValidator>(
            services, configuration, StreamEventOptions.SectionName, static (section, options) => section.Bind(options, StrictBinding));

        return services;
    }

    /// <summary>
    /// Registers one options type with binding, change tracking, its generated validator and startup validation.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <typeparam name="TValidator">The generated validator for <typeparamref name="TOptions"/>.</typeparam>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="sectionName">The configuration section bound to <typeparamref name="TOptions"/>.</param>
    /// <param name="bind">Binds a configuration section onto an options instance.</param>
    private static void AddValidatedOptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>(
        IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        Action<IConfiguration, TOptions> bind)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        var section = configuration.GetSection(sectionName);

        services.AddOptions<TOptions>()
            .Configure(options => BindStrictly(section, sectionName, options, bind))
            .ValidateOnStart();

        services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(new ConfigurationChangeTokenSource<TOptions>(section));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
    }

    /// <summary>
    /// Binds <paramref name="section"/> onto <paramref name="options"/>, converting binder failures into
    /// <see cref="OptionsValidationException"/>.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <param name="section">The configuration section to bind.</param>
    /// <param name="sectionName">The section name, used in the failure message.</param>
    /// <param name="options">The options instance being configured.</param>
    /// <param name="bind">The concrete binding delegate.</param>
    /// <exception cref="OptionsValidationException">
    /// The section contains a key that is not a property of <typeparamref name="TOptions"/> or a value that cannot
    /// be converted to the property type. The binder's message, which names the offending key, is included.
    /// </exception>
    private static void BindStrictly<TOptions>(IConfiguration section, string sectionName, TOptions options, Action<IConfiguration, TOptions> bind)
        where TOptions : class
    {
        try
        {
            bind(section, options);
        }
        catch (InvalidOperationException ex)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(TOptions),
                [$"Configuration section '{sectionName}' could not be bound: {ex.Message}"]);
        }
    }
}
