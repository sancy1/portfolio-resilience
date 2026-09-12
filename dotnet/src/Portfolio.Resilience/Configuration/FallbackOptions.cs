// filepath: src/Portfolio.Resilience/Configuration/FallbackOptions.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Optional static fallback for a policy.
/// Dynamic fallbacks (Func) are supplied per-call and take precedence over this.
/// </summary>
public sealed class FallbackOptions
{
    /// <summary>When true, the policy resolves a fallback (static or per-call).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Human-readable reason for the fallback (for logging).</summary>
    public string? Reason { get; set; }
}
