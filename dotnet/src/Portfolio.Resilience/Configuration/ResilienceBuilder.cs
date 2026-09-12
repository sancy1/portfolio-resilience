// filepath: src/Portfolio.Resilience/Configuration/ResilienceBuilder.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.3.0
// purpose: Fluent builder used by AddPortfolioResilience to compose sinks, policies, and options.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (mutable builder)
//   Depends on : ILogSink, IMetricSink, ResilienceOptions, ErrorClassificationOptions
//   Used by    : ServiceCollectionExtensions.AddPortfolioResilience
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Mutable builder for the resilience library configuration. Passed to
/// <c>AddPortfolioResilience</c>'s configuration callback. All methods return
/// <c>this</c> so calls can be chained fluently.
/// </summary>
public sealed class ResilienceBuilder
{
    /// <summary>The root options object. Populated by <c>AddPolicy</c>.</summary>
    public ResilienceOptions Options { get; } = new();

    /// <summary>Error classification rules. Shared with the ErrorClassifier.</summary>
    public ErrorClassificationOptions ErrorClassification { get; } = new();

    /// <summary>Log sinks registered via <see cref="AddLogSink"/>.</summary>
    internal List<ILogSink> LogSinks { get; } = new();

    /// <summary>Extra metric sinks (in-memory is always present).</summary>
    internal List<IMetricSink> MetricSinks { get; } = new();

    /// <summary>Adds a log sink. Multiple calls register multiple sinks (composed at resolve time).</summary>
    public ResilienceBuilder AddLogSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        LogSinks.Add(sink);
        return this;
    }

    /// <summary>Adds an additional metric sink. The in-memory sink is always present.</summary>
    public ResilienceBuilder AddMetricSink(IMetricSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        MetricSinks.Add(sink);
        return this;
    }

    /// <summary>Registers a policy by name.</summary>
    public ResilienceBuilder AddPolicy(string name, Action<PolicyDefinition> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var policy = new PolicyDefinition { Name = name };
        configure(policy);
        Options.Policies[name] = policy;
        return this;
    }

    /// <summary>Sets the default policy used for unknown policy names.</summary>
    public ResilienceBuilder UseDefaultPolicy(Action<PolicyDefinition> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options.DefaultPolicy);
        return this;
    }
}
