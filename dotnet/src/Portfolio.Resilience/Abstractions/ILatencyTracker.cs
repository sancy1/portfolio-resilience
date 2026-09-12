// filepath: src/Portfolio.Resilience/Abstractions/ILatencyTracker.cs
namespace Portfolio.Resilience.Abstractions;

/// <summary>Latency statistics for a policy, over a rolling window.</summary>
public sealed record LatencySnapshot(
    string PolicyName,
    long TotalCalls,
    long FailedCalls,
    double ErrorRate,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double AvgMs,
    int InFlight);

/// <summary>
/// Read-only access to latency statistics. The health endpoint reads from this.
/// </summary>
public interface ILatencyTracker
{
    IReadOnlyCollection<LatencySnapshot> Snapshot();
    LatencySnapshot? Get(string policyName);
}
