// filepath: src/Portfolio.Resilience/Abstractions/IMetricSink.cs
namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// Receives latency and outcome samples for a policy.
/// Implementations may keep an in-memory rolling window (for /health/resilience)
/// or forward to Prometheus, OTel, Datadog, etc.
/// </summary>
public interface IMetricSink
{
    void RecordCall(string policyName, TimeSpan duration, bool success, int attempts);
}
