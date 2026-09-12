// filepath: src/Portfolio.Resilience/Configuration/TimeoutOptions.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Timeout tuning for a policy. A value of 0 disables the timeout.
/// </summary>
public sealed class TimeoutOptions
{
    public int TimeoutMs { get; set; } = 10_000;
}
