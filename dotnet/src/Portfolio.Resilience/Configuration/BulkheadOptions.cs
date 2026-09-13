// filepath: src/Portfolio.Resilience/Configuration/BulkheadOptions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.6.0
// purpose: Options for bulkhead isolation, plus self-validation of suspicious combinations.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : ResilienceErrorCategory
//   Used by    : PolicyDefinition, BulkheadPolicyBuilder, ResiliencePolicyRegistry (validation)
//   See also   : docs/bulkhead.md, SPEC.md section 13
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Tuning for bulkhead isolation. A bulkhead with <see cref="Enabled"/> = false
/// is a pass-through: every call runs immediately and no concurrency is capped.
/// </summary>
/// <remarks>
/// A bulkhead caps the number of operations that may run concurrently against a
/// resource. Calls beyond the cap may wait in a bounded queue (up to
/// <see cref="MaxQueue"/>, for up to <see cref="QueueTimeoutMs"/>) before being
/// rejected. This protects the resource and the caller's thread pool from being
/// exhausted by a slow or failing dependency.
/// </remarks>
public sealed class BulkheadOptions
{
    /// <summary>When false (the default), the bulkhead is a pass-through.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>The maximum number of operations that may run concurrently.</summary>
    public int MaxConcurrency { get; set; } = 20;

    /// <summary>
    /// The maximum number of operations that may wait for a slot. Calls beyond
    /// this are rejected immediately.
    /// </summary>
    public int MaxQueue { get; set; } = 100;

    /// <summary>
    /// How long a queued call may wait for a slot before it is rejected.
    /// </summary>
    public int QueueTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// The category used when a call is rejected by the bulkhead. Defaults to
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

        if (MaxConcurrency <= 0)
        {
            warnings.Add($"Policy '{policyName}': Bulkhead.MaxConcurrency must be greater than 0 (was {MaxConcurrency}).");
        }

        if (MaxQueue < 0)
        {
            warnings.Add($"Policy '{policyName}': Bulkhead.MaxQueue must not be negative (was {MaxQueue}).");
        }

        if (QueueTimeoutMs < 0)
        {
            warnings.Add($"Policy '{policyName}': Bulkhead.QueueTimeoutMs must not be negative (was {QueueTimeoutMs}).");
        }

        return warnings;
    }
}
