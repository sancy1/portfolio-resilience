// filepath: src/Portfolio.Resilience/Configuration/HedgingOptions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.7.0
// purpose: Options for hedged requests - parallel attempts with a latency race.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : ResilienceErrorCategory
//   Used by    : PolicyDefinition, HedgingPolicyBuilder, ResiliencePolicyRegistry (validation)
//   See also   : docs/hedging.md, SPEC.md section 16
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Tuning for hedged requests. A hedging policy that is disabled (the default)
/// is a pass-through: only the primary attempt runs.
/// </summary>
/// <remarks>
/// <para>
/// Hedging fires parallel attempts of the same operation with a stagger delay
/// between them. The first attempt to succeed wins the race; the others are
/// cancelled (or allowed to complete, depending on
/// <see cref="CancelOnSuccess"/>). This is a latency optimization, not a
/// reliability one - use retry for transient failures.
/// </para>
/// <para>
/// <b>Not safe for non-idempotent operations.</b> A hedged <c>POST /charge</c>
/// can create two charges if both attempts reach the server and the server does
/// not deduplicate. Use hedging only on idempotent reads, or on writes with an
/// idempotency key. See <c>docs/hedging.md</c> for details.
/// </para>
/// </remarks>
public sealed class HedgingOptions
{
    /// <summary>When false (the default), the hedging layer is a pass-through.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// The total number of attempts including the primary. <c>1</c> means no
    /// hedging; <c>2</c> means the primary plus one hedged attempt.
    /// </summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>
    /// The base stagger delay in milliseconds between attempts. The primary
    /// starts immediately; each subsequent attempt waits at least this long.
    /// </summary>
    public int DelayMs { get; set; } = 100;

    /// <summary>
    /// When true, the stagger delay doubles for each subsequent attempt. This
    /// reduces the load on a struggling dependency during a partial outage.
    /// </summary>
    public bool ExponentialBackoff { get; set; } = false;

    /// <summary>
    /// Optional per-attempt ceiling in milliseconds. When <c>0</c> (the default),
    /// each attempt runs until the pipeline's own timeout fires.
    /// </summary>
    public int AttemptTimeoutMs { get; set; } = 0;

    /// <summary>
    /// When true (the default), remaining attempts are cancelled once one
    /// succeeds. Set to false only when the operation is idempotent and the
    /// side effects of the losers should complete.
    /// </summary>
    public bool CancelOnSuccess { get; set; } = true;

    /// <summary>
    /// When true (the default), emit <c>hedge_won</c>, <c>hedge_lost</c>, and
    /// <c>hedge_cancelled</c> events for each race.
    /// </summary>
    public bool EmitAttemptEvents { get; set; } = true;

    /// <summary>
    /// The category used when all hedged attempts fail. Defaults to
    /// <see cref="ResilienceErrorCategory.Transient"/>.
    /// </summary>
    public ResilienceErrorCategory RejectionCategory { get; set; } = ResilienceErrorCategory.Transient;

    /// <summary>
    /// Returns human-readable warnings for suspicious option combinations.
    /// Never throws. Called once per policy at startup.
    /// </summary>
    /// <param name="policyName">The policy name, used to prefix warning messages.</param>
    public IReadOnlyList<string> Validate(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var warnings = new List<string>();

        if (MaxAttempts <= 0)
        {
            warnings.Add($"Policy '{policyName}': Hedging.MaxAttempts must be greater than 0 (was {MaxAttempts}).");
        }

        if (MaxAttempts > 5)
        {
            warnings.Add($"Policy '{policyName}': Hedging.MaxAttempts = {MaxAttempts} is aggressive; consider 2-3 for most workloads.");
        }

        if (DelayMs < 0)
        {
            warnings.Add($"Policy '{policyName}': Hedging.DelayMs must not be negative (was {DelayMs}).");
        }

        if (AttemptTimeoutMs < 0)
        {
            warnings.Add($"Policy '{policyName}': Hedging.AttemptTimeoutMs must not be negative (was {AttemptTimeoutMs}).");
        }

        if (AttemptTimeoutMs > 0 && AttemptTimeoutMs < DelayMs)
        {
            warnings.Add($"Policy '{policyName}': Hedging.AttemptTimeoutMs ({AttemptTimeoutMs}ms) is shorter than Hedging.DelayMs ({DelayMs}ms); hedged attempts may never fire.");
        }

        return warnings;
    }
}
