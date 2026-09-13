// filepath: src/Portfolio.Resilience/Configuration/RateLimiterOptions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.6.0
// purpose: Options for the rate limiter policy, plus self-validation of suspicious combinations.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : RateLimitStrategy, ResilienceErrorCategory
//   Used by    : PolicyDefinition, RateLimiterPolicyBuilder, ResiliencePolicyRegistry (validation)
//   See also   : docs/rate-limiter.md, SPEC.md section 12
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Tuning for the rate limiter. A limiter with <see cref="Enabled"/> = false is
/// a pass-through: every call is permitted and no state is tracked.
/// </summary>
/// <remarks>
/// The meaning of <see cref="PermitLimit"/> and <see cref="WindowSeconds"/>
/// depends on <see cref="Strategy"/>. See the strategy-to-field matrix in
/// <c>docs/rate-limiter.md</c>:
/// <list type="bullet">
///   <item><see cref="RateLimitStrategy.TokenBucket"/>: PermitLimit = bucket capacity, WindowSeconds = refill period.</item>
///   <item><see cref="RateLimitStrategy.SlidingWindow"/>: PermitLimit = max calls in any rolling WindowSeconds window.</item>
///   <item><see cref="RateLimitStrategy.FixedWindow"/>: PermitLimit = max calls per fixed WindowSeconds bucket.</item>
///   <item><see cref="RateLimitStrategy.ConcurrencyLimit"/>: PermitLimit = max simultaneous calls; WindowSeconds is ignored.</item>
/// </list>
/// </remarks>
public sealed class RateLimiterOptions
{
    /// <summary>When false (the default), the rate limiter is a pass-through.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>The algorithm used to decide whether a call is permitted.</summary>
    public RateLimitStrategy Strategy { get; set; } = RateLimitStrategy.SlidingWindow;

    /// <summary>The number of permits allowed. Meaning varies by <see cref="Strategy"/>.</summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// The window in seconds. Ignored when <see cref="Strategy"/> is
    /// <see cref="RateLimitStrategy.ConcurrencyLimit"/>.
    /// </summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// How many calls may wait when no permit is available. Zero (the default)
    /// rejects immediately without queuing.
    /// </summary>
    public int QueueLimit { get; set; } = 0;

    /// <summary>
    /// How long a queued call may wait before it is rejected. Only consulted
    /// when <see cref="QueueLimit"/> is greater than zero.
    /// </summary>
    public int QueueTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// The category used when a call is rejected by the limiter. Defaults to
    /// <see cref="ResilienceErrorCategory.Transient"/>.
    /// </summary>
    public ResilienceErrorCategory RejectionCategory { get; set; } = ResilienceErrorCategory.Transient;

    /// <summary>
    /// Returns human-readable warnings for suspicious option combinations.
    /// Never throws. Called once per policy at startup by
    /// <c>ResiliencePolicyRegistry.Resolve</c>. An empty list means the options are fine.
    /// </summary>
    /// <param name="policyName">The policy name, used to prefix warning messages.</param>
    public IReadOnlyList<string> Validate(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var warnings = new List<string>();

        if (PermitLimit <= 0)
        {
            warnings.Add($"Policy '{policyName}': RateLimiter.PermitLimit must be greater than 0 (was {PermitLimit}).");
        }

        if (Strategy != RateLimitStrategy.ConcurrencyLimit && WindowSeconds <= 0)
        {
            warnings.Add($"Policy '{policyName}': RateLimiter.WindowSeconds must be greater than 0 for strategy {Strategy} (was {WindowSeconds}).");
        }

        if (Strategy == RateLimitStrategy.ConcurrencyLimit && WindowSeconds != 60)
        {
            warnings.Add($"Policy '{policyName}': RateLimiter.Strategy = ConcurrencyLimit ignores WindowSeconds (was {WindowSeconds}); remove it to silence this warning.");
        }

        if (QueueLimit < 0)
        {
            warnings.Add($"Policy '{policyName}': RateLimiter.QueueLimit must not be negative (was {QueueLimit}).");
        }

        if (QueueTimeoutMs < 0)
        {
            warnings.Add($"Policy '{policyName}': RateLimiter.QueueTimeoutMs must not be negative (was {QueueTimeoutMs}).");
        }

        return warnings;
    }
}
