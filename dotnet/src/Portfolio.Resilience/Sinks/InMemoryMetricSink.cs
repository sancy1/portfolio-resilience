// filepath: src/Portfolio.Resilience/Sinks/InMemoryMetricSink.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: Records call durations and outcomes per policy, computes p50/p95/p99 and error rate.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : IMetricSink
//   Depends on : LatencySnapshot (Abstractions/ILatencyTracker.cs)
//   Used by    : CompositeMetricSink, /health/resilience endpoint (Stage H)
//   See also   : docs/metrics.md, SPEC.md §Metrics
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// Keeps a rolling window of latency samples per policy and computes
/// p50/p95/p99, error rate, and in-flight counts. Thread-safe.
/// <para>
/// Window size is bounded (default 1000 samples). Older samples are discarded
/// as new ones arrive. This is intentionally a fixed-size reservoir, not a
/// time-windowed histogram — cheap and adequate for health endpoints.
/// </para>
/// </summary>
public sealed class InMemoryMetricSink : IMetricSink
{
    private const int DefaultWindowSize = 1000;

    private readonly int _windowSize;
    private readonly Dictionary<string, PolicyMetrics> _byPolicy = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public InMemoryMetricSink(int windowSize = DefaultWindowSize)
    {
        if (windowSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");

        _windowSize = windowSize;
    }

    /// <inheritdoc />
    public void RecordCall(string policyName, TimeSpan duration, bool success, int attempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var durationMs = duration.TotalMilliseconds;

        lock (_gate)
        {
            if (!_byPolicy.TryGetValue(policyName, out var metrics))
            {
                metrics = new PolicyMetrics(_windowSize);
                _byPolicy[policyName] = metrics;
            }

            metrics.Record(durationMs, success, attempts);
        }
    }

    /// <summary>Returns snapshots for every policy that has recorded at least one call.</summary>
    public IReadOnlyCollection<LatencySnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _byPolicy
                .Select(kv => kv.Value.ToSnapshot(kv.Key))
                .ToArray();
        }
    }

    /// <summary>Returns a snapshot for a single policy, or null if none exists.</summary>

    /// <summary>
    /// Marks a call as in-flight for the given policy. Paired with <see cref="EndInFlight"/>.
    /// Called by the executor to track currently-executing operations.
    /// </summary>
    public void BeginInFlight(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        lock (_gate)
        {
            if (!_byPolicy.TryGetValue(policyName, out var metrics))
            {
                metrics = new PolicyMetrics(_windowSize);
                _byPolicy[policyName] = metrics;
            }
            metrics.IncrementInFlight();
        }
    }

    /// <summary>Marks a call as no longer in-flight for the given policy.</summary>
    public void EndInFlight(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        lock (_gate)
        {
            if (_byPolicy.TryGetValue(policyName, out var metrics))
            {
                metrics.DecrementInFlight();
            }
        }
    }
    public LatencySnapshot? Get(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        lock (_gate)
        {
            return _byPolicy.TryGetValue(policyName, out var m)
                ? m.ToSnapshot(policyName)
                : null;
        }
    }

    // -------------------------------------------------------------------------
    // Internal per-policy accumulator
    // -------------------------------------------------------------------------

    private sealed class PolicyMetrics
    {
        private readonly int _windowSize;
        private readonly Queue<double> _durations;

        private long _totalCalls;
        private long _failedCalls;
        private int _inFlight;

        public PolicyMetrics(int windowSize)
        {
            _windowSize = windowSize;
            _durations = new Queue<double>(windowSize + 1);
        }

        public void IncrementInFlight() => _inFlight++;
        public void DecrementInFlight() => _inFlight = Math.Max(0, _inFlight - 1);

        public void Record(double durationMs, bool success, int attempts)
        {
            _totalCalls++;
            if (!success) _failedCalls++;

            _durations.Enqueue(durationMs);
            while (_durations.Count > _windowSize)
            {
                _durations.Dequeue();
            }
        }

        public LatencySnapshot ToSnapshot(string policyName)
        {
            var samples = _durations.ToArray();
            Array.Sort(samples);

            return new LatencySnapshot(
                PolicyName: policyName,
                TotalCalls: _totalCalls,
                FailedCalls: _failedCalls,
                ErrorRate: _totalCalls == 0 ? 0.0 : (double)_failedCalls / _totalCalls,
                P50Ms: Percentile(samples, 0.50),
                P95Ms: Percentile(samples, 0.95),
                P99Ms: Percentile(samples, 0.99),
                AvgMs: samples.Length == 0 ? 0.0 : samples.Average(),
                InFlight: _inFlight);
        }

        // Linear interpolation percentile — good enough for health metrics.
        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0.0;
            if (sorted.Length == 1) return sorted[0];

            var rank = p * (sorted.Length - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);

            if (lower == upper) return sorted[lower];

            var weight = rank - lower;
            return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
        }
    }
}
