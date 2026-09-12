// filepath: src/Portfolio.Resilience/Implementation/LatencyTracker.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.3.0
// purpose: Read-side view of latency statistics backed by an InMemoryMetricSink.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ILatencyTracker
//   Depends on : InMemoryMetricSink, LatencySnapshot
//   Used by    : /health/resilience endpoint (Stage H)
//   See also   : docs/metrics.md, SPEC.md §Metrics
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Sinks;

namespace Portfolio.Resilience.Implementation;

/// <summary>
/// Exposes read-only latency snapshots. Delegates to <see cref="InMemoryMetricSink"/>
/// for the actual windowed statistics.
/// </summary>
/// <remarks>
/// Only an <see cref="InMemoryMetricSink"/> can be read from — the abstract
/// <see cref="IMetricSink"/> contract is write-only by design (a Prometheus or
/// Datadog exporter receives samples but does not answer queries). If a service
/// uses a different primary sink, it should still register an in-memory sink as a
/// secondary destination via <c>CompositeMetricSink</c>.
/// </remarks>
public sealed class LatencyTracker : ILatencyTracker
{
    private readonly InMemoryMetricSink _sink;

    public LatencyTracker(InMemoryMetricSink? metricSink = null)
    {
        _sink = metricSink ?? new InMemoryMetricSink();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<LatencySnapshot> Snapshot() => _sink.Snapshot();

    /// <inheritdoc />
    public LatencySnapshot? Get(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        return _sink.Get(policyName);
    }
}
