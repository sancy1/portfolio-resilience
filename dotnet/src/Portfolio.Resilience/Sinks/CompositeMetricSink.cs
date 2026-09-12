// filepath: src/Portfolio.Resilience/Sinks/CompositeMetricSink.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: Fans out each metric sample to multiple metric sinks. Failures in one sink never block the others.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : IMetricSink
//   Depends on : IMetricSink
//   Used by    : ServiceCollectionExtensions when multiple metric sinks are configured
//   See also   : docs/metrics.md, SPEC.md §Metrics
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// Forwards every metric sample to a set of child sinks.
/// If a child sink throws, the exception is caught and logged (via the optional
/// <see cref="ILogger"/>) so the remaining sinks still receive the sample.
/// </summary>
public sealed class CompositeMetricSink : IMetricSink
{
    private readonly IReadOnlyList<IMetricSink> _sinks;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a composite metric sink from the provided children.
    /// </summary>
    /// <param name="sinks">Child sinks. Must not be null. May be empty.</param>
    /// <param name="logger">Optional logger used to report child-sink failures.</param>
    public CompositeMetricSink(
        IEnumerable<IMetricSink> sinks,
        ILogger<CompositeMetricSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        _sinks = sinks.Where(s => s is not null).ToArray();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Number of child sinks this composite forwards to.</summary>
    public int Count => _sinks.Count;

    /// <inheritdoc />
    public void RecordCall(string policyName, TimeSpan duration, bool success, int attempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        foreach (var sink in _sinks)
        {
            try
            {
                sink.RecordCall(policyName, duration, success, attempts);
            }
            catch (Exception ex)
            {
                // A failing metric sink must not take down the pipeline. Log and continue.
                _logger.LogWarning(
                    ex,
                    "Metric sink {SinkType} failed to record call for policy {PolicyName}",
                    sink.GetType().Name,
                    policyName);
            }
        }
    }
}
