// filepath: src/Portfolio.Resilience/Configuration/PolicyDefinition.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Complete definition of a named policy.
/// Policy name follows the pattern: <c>domain.resource.action</c> or a simple service name.
/// </summary>
public sealed class PolicyDefinition
{
    public string Name { get; set; } = string.Empty;
    public RetryOptions Retry { get; set; } = new();
    public CircuitOptions Circuit { get; set; } = new();
    public TimeoutOptions Timeout { get; set; } = new();
    public FallbackOptions Fallback { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
}
