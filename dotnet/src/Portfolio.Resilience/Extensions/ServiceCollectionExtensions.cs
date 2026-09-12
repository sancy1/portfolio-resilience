// filepath: src/Portfolio.Resilience/Extensions/ServiceCollectionExtensions.cs
// layer: Extensions | package: Portfolio.Resilience | since: v0.3.0
// purpose: One-line DI registration. Wires the whole library with sensible defaults, overridable.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IServiceCollection, ResilienceOptions, all sinks and implementations
//   Used by    : every service's Program.cs
//   See also   : docs/executor.md, README.md
// ─────────────────────────────────────────────────────────────────────────────

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
///     .Logging.AddConsole()
///     .Metrics.AddInMemory()
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

        // Always register the InMemoryMetricSink — it's the read-side data source for
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
        services.AddSingleton<ILogSink>(_ =>
        {
            var children = builder.LogSinks.ToArray();
            return children.Length switch
            {
                0 => NullLogSink.Instance,
                1 => children[0],
                _ => new CompositeLogSink(children)
            };
        });

        // Configuration
        services.AddSingleton(builder.Options);
        services.AddSingleton<IResiliencePolicyRegistry>(_ =>
            new ResiliencePolicyRegistry(builder.Options));

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
        services.AddSingleton<CompositePolicyBuilder>(sp =>
            new CompositePolicyBuilder(
                sp.GetRequiredService<RetryPolicyBuilder>(),
                sp.GetRequiredService<CircuitPolicyBuilder>(),
                sp.GetRequiredService<TimeoutPolicyBuilder>()));

        // Executor — the main entry point
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
}
