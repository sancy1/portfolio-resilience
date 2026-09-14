// filepath: src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryBuilderExtensions.cs
// layer: OpenTelemetry | package: Portfolio.Resilience.OpenTelemetry | since: v0.7.0
// purpose: One-line opt-in for the OpenTelemetry sinks, from either IServiceCollection or ResilienceBuilder.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IServiceCollection, ResilienceBuilder, ILoggerFactory, Meter,
//                ILogSink, IMetricSink, CompositeLogSink, CompositeMetricSink
//   Used by    : services that want OTel-backed resilience observability
//   See also   : docs/opentelemetry.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Sinks;

namespace Portfolio.Resilience.OpenTelemetry;

/// <summary>
/// Extension methods that wire <see cref="OpenTelemetryLogSink"/> and
/// <see cref="OpenTelemetryMetricSink"/> into the resilience pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Two entry points exist:
/// </para>
/// <list type="number">
///   <item>
///     <see cref="AddPortfolioResilienceOpenTelemetry"/> - called on
///     <see cref="IServiceCollection"/> after <c>AddPortfolioResilience</c>.
///     Resolves <see cref="ILoggerFactory"/> from the container and composes
///     the OTel sinks with the existing ones. This is the recommended path for
///     ASP.NET Core applications.
///   </item>
///   <item>
///     <see cref="AddOpenTelemetrySinks"/> - called on
///     <see cref="ResilienceBuilder"/> with explicit dependencies. This is the
///     advanced path for tests, console applications, or callers who already
///     have a specific <see cref="ILoggerFactory"/> or <see cref="Meter"/>.
///   </item>
/// </list>
/// <para>
/// The <see cref="IServiceCollection"/> path is <b>order-independent</b>. It can
/// be called before or after <c>AddPortfolioResilience</c>; the resulting
/// <see cref="ILogSink"/> and <see cref="IMetricSink"/> compose the OTel sinks
/// with whatever the resilience library registered.
/// </para>
/// </remarks>
public static class OpenTelemetryBuilderExtensions
{
    /// <summary>
    /// Registers both OpenTelemetry sinks and composes them with the resilience
    /// library's existing log and metric sinks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works regardless of call order relative to <c>AddPortfolioResilience</c>.
    /// The composition happens lazily when the container first resolves
    /// <see cref="ILogSink"/> or <see cref="IMetricSink"/>.
    /// </para>
    /// <para>
    /// The container must have <see cref="ILoggerFactory"/> registered. ASP.NET
    /// Core registers it by default via <c>WebApplication.CreateBuilder</c>. For
    /// console applications, register a logger factory explicitly
    /// (for example, <c>services.AddLogging()</c>).
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="meterName">
    /// Optional meter name. Defaults to
    /// <see cref="OpenTelemetryMetricSink.DefaultMeterName"/>.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="services"/> is null.</exception>
    public static IServiceCollection AddPortfolioResilienceOpenTelemetry(
        this IServiceCollection services,
        string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A Meter is required by the metric sink. Reuse one already registered
        // in the container if present; otherwise create a default and register
        // it as a singleton so its instruments have a stable lifetime.
        if (!services.Any(d => d.ServiceType == typeof(Meter)))
        {
            services.AddSingleton<Meter>(_ =>
                new Meter(meterName ?? OpenTelemetryMetricSink.DefaultMeterName));
        }

        // Register the OTel sinks themselves as singletons. They are resolved
        // lazily by the composite factories below.
        services.TryAddSingleton(sp =>
            new OpenTelemetryLogSink(sp.GetRequiredService<ILoggerFactory>()));

        services.TryAddSingleton(sp =>
            new OpenTelemetryMetricSink(sp.GetRequiredService<Meter>()));

        ComposeLogSink(services);
        ComposeMetricSink(services);

        return services;
    }

    /// <summary>
    /// Adds the OpenTelemetry sinks to the resilience pipeline directly, from
    /// inside a <c>AddPortfolioResilience</c> configuration callback.
    /// </summary>
    /// <param name="builder">The resilience builder.</param>
    /// <param name="loggerFactory">
    /// The logger factory to construct <see cref="OpenTelemetryLogSink"/>.
    /// </param>
    /// <param name="meter">
    /// Optional meter to construct <see cref="OpenTelemetryMetricSink"/>. When
    /// null, a new <see cref="Meter"/> is created with the default name.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="builder"/> or <paramref name="loggerFactory"/> is null.</exception>
    public static ResilienceBuilder AddOpenTelemetrySinks(
        this ResilienceBuilder builder,
        ILoggerFactory loggerFactory,
        Meter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var effectiveMeter = meter ?? new Meter(OpenTelemetryMetricSink.DefaultMeterName);

        builder.AddLogSink(new OpenTelemetryLogSink(loggerFactory));
        builder.AddMetricSink(new OpenTelemetryMetricSink(effectiveMeter));

        return builder;
    }

    // ------------------------------------------------------------------------
    // Internals - compose the OTel sinks with whatever the library registered
    // ------------------------------------------------------------------------

    private static void ComposeLogSink(IServiceCollection services)
    {
        // Capture the existing ILogSink factory (or its absence) before we
        // replace it. Then re-register ILogSink so it composes the OTel sink
        // with the original.

        var existingDescriptors = services
            .Where(d => d.ServiceType == typeof(ILogSink))
            .ToArray();

        // Nothing was registered for ILogSink; register a new one that wraps the
        // OTel sink directly. This is the pre-AddPortfolioResilience case.
        if (existingDescriptors.Length == 0)
        {
            services.AddSingleton<ILogSink>(sp =>
                sp.GetRequiredService<OpenTelemetryLogSink>());
            return;
        }

        services.RemoveAll<ILogSink>();
        services.AddSingleton<ILogSink>(sp =>
        {
            var otel = sp.GetRequiredService<OpenTelemetryLogSink>();

            // Rebuild the original sink by executing its factory (if it has one)
            // or reusing the instance. We keep it simple: if the original was a
            // factory, we call it; otherwise we use the instance.
            var original = ResolveOriginal<ILogSink>(sp, existingDescriptors);

            return original is null
                ? otel
                : new CompositeLogSink(new ILogSink[] { original, otel });
        });
    }

    private static void ComposeMetricSink(IServiceCollection services)
    {
        var existingDescriptors = services
            .Where(d => d.ServiceType == typeof(IMetricSink))
            .ToArray();

        if (existingDescriptors.Length == 0)
        {
            services.AddSingleton<IMetricSink>(sp =>
                sp.GetRequiredService<OpenTelemetryMetricSink>());
            return;
        }

        services.RemoveAll<IMetricSink>();
        services.AddSingleton<IMetricSink>(sp =>
        {
            var otel = sp.GetRequiredService<OpenTelemetryMetricSink>();
            var original = ResolveOriginal<IMetricSink>(sp, existingDescriptors);

            return original is null
                ? otel
                : new CompositeMetricSink(new IMetricSink[] { original, otel });
        });
    }

    /// <summary>
    /// Executes the last-registered descriptor's factory to obtain the original
    /// sink instance. The descriptor list is passed in because the caller has
    /// already captured it before replacement.
    /// </summary>
    private static T? ResolveOriginal<T>(
        IServiceProvider sp,
        ServiceDescriptor[] descriptors)
        where T : class
    {
        for (var i = descriptors.Length - 1; i >= 0; i--)
        {
            var descriptor = descriptors[i];

            if (descriptor.ImplementationInstance is T instance)
            {
                return instance;
            }

            if (descriptor.ImplementationFactory is not null)
            {
                return descriptor.ImplementationFactory(sp) as T;
            }

            if (descriptor.ImplementationType is not null)
            {
                return ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType) as T;
            }
        }

        return null;
    }
}
