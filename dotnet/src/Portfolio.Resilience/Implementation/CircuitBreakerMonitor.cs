// filepath: src/Portfolio.Resilience/Implementation/CircuitBreakerMonitor.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.3.0
// purpose: Aggregates circuit snapshots from multiple ICircuitBreakerMonitor sources into one view.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ICircuitBreakerMonitor
//   Depends on : ICircuitBreakerMonitor, CircuitSnapshot
//   Used by    : /health/resilience endpoint (Stage H)
//   See also   : docs/circuit-breaker.md, SPEC.md §CircuitBreaker
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.Implementation;

/// <summary>
/// Merges snapshots from zero or more <see cref="ICircuitBreakerMonitor"/> sources.
/// Useful when a service owns several circuit builders (one per subsystem) and
/// wants a single aggregated view for health reporting.
/// </summary>
/// <remarks>
/// Policy names must be unique across sources. If a name appears in more than
/// one source, the first one seen in iteration order wins.
/// </remarks>
public sealed class CircuitBreakerMonitor : ICircuitBreakerMonitor
{
    private readonly IReadOnlyList<ICircuitBreakerMonitor> _sources;

    public CircuitBreakerMonitor(IEnumerable<ICircuitBreakerMonitor> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _sources = sources.Where(s => s is not null).ToArray();
    }

    /// <summary>Number of underlying monitors being aggregated.</summary>
    public int SourceCount => _sources.Count;

    /// <inheritdoc />
    public IReadOnlyCollection<CircuitSnapshot> Snapshot()
    {
        // Policy name -> snapshot. First writer wins for duplicate names.
        var merged = new Dictionary<string, CircuitSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources)
        {
            foreach (var snapshot in source.Snapshot())
            {
                merged.TryAdd(snapshot.PolicyName, snapshot);
            }
        }

        return merged.Values.ToArray();
    }

    /// <inheritdoc />
    public CircuitSnapshot? Get(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        foreach (var source in _sources)
        {
            var snapshot = source.Get(policyName);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }
}
