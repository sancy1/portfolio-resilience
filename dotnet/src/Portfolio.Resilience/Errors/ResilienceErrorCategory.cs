// filepath: src/Portfolio.Resilience/Errors/ResilienceErrorCategory.cs
namespace Portfolio.Resilience.Errors;

/// <summary>
/// Classification of a failed resilience-wrapped operation.
/// Shared across all language implementations per SPEC.md.
/// </summary>
public enum ResilienceErrorCategory
{
    /// <summary>Not yet classified.</summary>
    Unknown = 0,

    /// <summary>Transient failure — retry may succeed (network blip, 5xx, DB failover).</summary>
    Transient = 1,

    /// <summary>Permanent failure — retry will not help (4xx, validation, not-found).</summary>
    Permanent = 2,

    /// <summary>Circuit is open. The operation was rejected before execution.</summary>
    CircuitOpen = 3,

    /// <summary>Operation exceeded its timeout ceiling.</summary>
    Timeout = 4,

    /// <summary>Primary failed, but a fallback produced a result. Reported at informational level.</summary>
    FallbackUsed = 5
}
