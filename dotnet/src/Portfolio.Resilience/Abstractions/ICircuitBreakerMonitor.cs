// filepath: src/Portfolio.Resilience/Abstractions/ICircuitBreakerMonitor.cs
namespace Portfolio.Resilience.Abstractions;

/// <summary>Public circuit state, suitable for /health endpoints.</summary>
public enum CircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2
}

/// <summary>Snapshot of a single circuit's state.</summary>
public sealed record CircuitSnapshot(
    string PolicyName,
    CircuitState State,
    int ConsecutiveFailures,
    DateTime? OpenedAtUtc,
    DateTime? NextProbeAtUtc);

/// <summary>
/// Read-only access to circuit states. The health endpoint reads from this.
/// </summary>
public interface ICircuitBreakerMonitor
{
    IReadOnlyCollection<CircuitSnapshot> Snapshot();
    CircuitSnapshot? Get(string policyName);
}
