// filepath: src/Portfolio.Resilience/Extensions/ServiceCollectionExtensions.cs
// layer: Extensions | package: Portfolio.Resilience | since: v0.6.0
// purpose: One-line DI registration. Wires the whole library with sensible defaults, overridable.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IServiceCollection, ResilienceOptions, ResiliencePolicyRegistry,
//                all policy builders, all sinks and implementations
//   Used by    : every service's Program.cs
//   See also   : docs/executor.md, README.md, docs/rate-limiter.md, docs/bulkhead.md
// -----------------------------------------------------------------------------

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Portfolio.Resilience.Sinks;

namespace Portfolio.Resilience.Extensions;

/// <summary>
/// Registers the resilience library with the DI container. Call once during startup:
/// <code>
/// builder.Services.AddPortfolioResilience(r => r
///     .AddLogSink(new ConsoleLogSink())
///     .AddPolicy("auth-service", p => p.Timeout.TimeoutMs = 5000));
/// </code>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers all resilience services with the DI container.</summary>
    public static IServiceCollection AddPortfolioResilience(
        this IServiceCollection services,
        Action<ResilienceBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new ResilienceBuilder();
        configure?.Invoke(builder);

        // Always register the InMemoryMetricSink - it is the read-side data source for
        // ILatencyTracker and the /health/resilience endpoint. Any other metric sinks
        // (Prometheus, Datadog) are added as additional children of a CompositeMetricSink.
        var inMemoryMetricSink = new InMemoryMetricSink();
        services.AddSingleton(inMemoryMetricSink);

        // Compose the metric sink: InMemory + any user-registered sinks.
        services.AddSingleton<IMetricSink>(sp =>
        {
            var children = new List<IMetricSink> { inMemoryMetricSink };
            children.AddRange(builder.MetricSinks);
            return children.Count == 1
                ? inMemoryMetricSink
                : new CompositeMetricSink(children);
        });

        // Compose the log sink: user-registered or NullLogSink fallback.
        // When any policy enables ScrubSensitiveData, we always use a
        // CompositeLogSink (even for a single child) so the scrubber runs
        // once before any sink sees the event.
        services.AddSingleton<ILogSink>(_ =>
        {
            var children = builder.LogSinks.ToArray();
            var scrub = builder.Options.AnyPolicyScrubsSensitiveData();
            var scrubber = scrub ? new DefaultPciScrubber() : null;

            if (children.Length == 0)
            {
                return NullLogSink.Instance;
            }

            if (children.Length == 1 && !scrub)
            {
                return children[0];
            }

            return new CompositeLogSink(children, logger: null, scrubber: scrubber);
        });

        // Configuration. Policy validation warnings are routed through Trace -
        // see ResiliencePolicyRegistry for the once-per-policy behaviour.
        services.AddSingleton(builder.Options);
        services.AddSingleton<IResiliencePolicyRegistry>(sp =>
        {
            // Apply any pending post-configuration actions before the registry
            // is constructed. This allows later extensions (for example
            // AddStandardResilienceHandler) to inject policies into the options
            // object even though it was captured by reference at registration
            // time. IEnumerable<T> resolution picks up every registration made
            // before the first resolve, regardless of call order.
            foreach (var action in sp.GetServices<Action<ResilienceOptions>>())
            {
                action(builder.Options);
            }

            return new ResiliencePolicyRegistry(builder.Options, Warn);
        });

        // Correlation
        services.AddSingleton<ICorrelationAccessor, Correlation.AsyncLocalCorrelationAccessor>();

        // Event emitter
        services.AddSingleton<ResilienceEventEmitter>(sp =>
            new ResilienceEventEmitter(sp.GetRequiredService<ILogSink>()));

        // Policy pipeline
        services.AddSingleton<ErrorClassifier>(sp =>
            new ErrorClassifier(builder.ErrorClassification));
        services.AddSingleton<RetryPolicyBuilder>(sp =>
            new RetryPolicyBuilder(sp.GetRequiredService<ErrorClassifier>()));
        services.AddSingleton<TimeoutPolicyBuilder>();
        services.AddSingleton<CircuitPolicyBuilder>(sp =>
            new CircuitPolicyBuilder(sp.GetRequiredService<ErrorClassifier>()));
        services.AddSingleton<RateLimiterPolicyBuilder>(sp =>
            new RateLimiterPolicyBuilder(sp.GetRequiredService<ResilienceEventEmitter>()));
        services.AddSingleton<BulkheadPolicyBuilder>(sp =>
            new BulkheadPolicyBuilder(sp.GetRequiredService<ResilienceEventEmitter>()));
        services.AddSingleton<HedgingPolicyBuilder>(sp =>
            new HedgingPolicyBuilder(sp.GetRequiredService<ResilienceEventEmitter>()));
        services.AddSingleton<CompositePolicyBuilder>(sp =>
            new CompositePolicyBuilder(
                sp.GetRequiredService<RetryPolicyBuilder>(),
                sp.GetRequiredService<CircuitPolicyBuilder>(),
                sp.GetRequiredService<TimeoutPolicyBuilder>(),
                sp.GetRequiredService<RateLimiterPolicyBuilder>(),
                sp.GetRequiredService<BulkheadPolicyBuilder>(),
                sp.GetRequiredService<HedgingPolicyBuilder>()));

        // Executor - the main entry point
        services.AddSingleton<IResilienceExecutor>(sp =>
            new ResilienceExecutor(
                registry: sp.GetRequiredService<IResiliencePolicyRegistry>(),
                pipeline: sp.GetRequiredService<CompositePolicyBuilder>(),
                emitter: sp.GetRequiredService<ResilienceEventEmitter>(),
                metricSink: inMemoryMetricSink,
                classifier: sp.GetRequiredService<ErrorClassifier>()));

        // Read-side monitors
        services.AddSingleton<ILatencyTracker>(_ => new LatencyTracker(inMemoryMetricSink));
        services.AddSingleton<ICircuitBreakerMonitor>(sp =>
            new CircuitBreakerMonitor(new ICircuitBreakerMonitor[]
            {
                sp.GetRequiredService<CircuitPolicyBuilder>()
            }));

        return services;
    }

    // Trace.TraceWarning carries a [Conditional("TRACE")] attribute, which prevents
    // creating a delegate directly from it (CS1618). This wrapper is a plain method
    // with no conditional attribute, so it can be passed as Action<string>.
    private static void Warn(string message) => Trace.TraceWarning(message);
}
