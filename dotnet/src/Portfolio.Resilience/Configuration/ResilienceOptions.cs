// filepath: src/Portfolio.Resilience/Configuration/ResilienceOptions.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Root options for the resilience package. Bound from configuration
/// (environment variables prefixed with RESILIENCE_ or appsettings sections).
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>Default policies applied when a policy name has no explicit entry.</summary>
    public Dictionary<string, PolicyDefinition> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback policy used when a call references a policy name that does not exist.</summary>
    public PolicyDefinition DefaultPolicy { get; set; } = new() { Name = "default" };
}
