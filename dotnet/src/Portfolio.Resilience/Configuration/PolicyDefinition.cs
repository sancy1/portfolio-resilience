// filepath: src/Portfolio.Resilience/Configuration/PolicyDefinition.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.6.0
// purpose: Complete definition of a named policy — retry, circuit, timeout, fallback, logging, rate limiter, and bulkhead.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : RetryOptions, CircuitOptions, TimeoutOptions, FallbackOptions, LoggingOptions, RateLimiterOptions, BulkheadOptions
//   Used by    : ResilienceOptions, ResiliencePolicyRegistry, CompositePolicyBuilder
//   See also   : docs/executor.md, SPEC.md section 2, SPEC.md section 12, SPEC.md section 13
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Complete definition of a named policy.
/// Policy name follows the pattern: <c>domain.resource.action</c> or a simple service name.
/// </summary>
public sealed class PolicyDefinition
{
    /// <summary>The policy name. Used in events, metrics, and error messages.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Retry tuning. See <c>docs/retry.md</c>.</summary>
    public RetryOptions Retry { get; set; } = new();

    /// <summary>Circuit breaker tuning. See <c>docs/circuit-breaker.md</c>.</summary>
    public CircuitOptions Circuit { get; set; } = new();

    /// <summary>Timeout tuning. See <c>docs/timeout.md</c>.</summary>
    public TimeoutOptions Timeout { get; set; } = new();

    /// <summary>Fallback tuning. See <c>docs/executor.md</c>.</summary>
    public FallbackOptions Fallback { get; set; } = new();

    /// <summary>Per-event-type logging toggles. See <c>docs/logging.md</c>.</summary>
    public LoggingOptions Logging { get; set; } = new();

    /// <summary>Rate limiter tuning. See <c>docs/rate-limiter.md</c>.</summary>
    public RateLimiterOptions RateLimiter { get; set; } = new();

    /// <summary>Bulkhead isolation tuning. See <c>docs/bulkhead.md</c>.</summary>
    public BulkheadOptions Bulkhead { get; set; } = new();
}
