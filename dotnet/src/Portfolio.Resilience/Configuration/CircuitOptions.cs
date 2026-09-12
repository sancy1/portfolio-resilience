// filepath: src/Portfolio.Resilience/Configuration/CircuitOptions.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Circuit breaker tuning for a policy.
/// State machine: Closed -> Open (at FailureThreshold) -> HalfOpen (after OpenDuration) -> Closed/HalfOpen.
/// </summary>
public sealed class CircuitOptions
{
    /// <summary>Consecutive failures required to open the circuit.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>Duration the circuit stays open before moving to HalfOpen.</summary>
    public int OpenDurationSeconds { get; set; } = 30;

    /// <summary>Successful probes required in HalfOpen to close the circuit.</summary>
    public int SuccessThreshold { get; set; } = 1;

    /// <summary>When true, the circuit only counts "Transient" failures toward the threshold.</summary>
    public bool OnlyCountTransient { get; set; } = true;
}
