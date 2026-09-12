// filepath: src/Portfolio.Resilience/Implementation/ResiliencePolicyRegistry.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.3.0
// purpose: Resolves PolicyDefinitions by name; falls back to a default policy for unknown names.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : IResiliencePolicyRegistry
//   Depends on : ResilienceOptions, PolicyDefinition
//   Used by    : ResilienceExecutor (Stage F)
//   See also   : docs/executor.md, SPEC.md §PolicyNaming
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Implementation;

/// <summary>
/// Looks up the effective <see cref="PolicyDefinition"/> for a name.
/// Unknown names resolve to <see cref="ResilienceOptions.DefaultPolicy"/>.
/// </summary>
public sealed class ResiliencePolicyRegistry : IResiliencePolicyRegistry
{
    private readonly ResilienceOptions _options;

    public ResiliencePolicyRegistry(ResilienceOptions? options = null)
    {
        _options = options ?? new ResilienceOptions();
    }

    /// <summary>
    /// Resolves the policy for <paramref name="policyName"/>.
    /// Returns a clone so callers cannot mutate shared state.
    /// </summary>
    public PolicyDefinition Resolve(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        if (_options.Policies.TryGetValue(policyName, out var defined))
        {
            return Clone(defined);
        }

        // Unknown policy: use the default, but override Name so events/logs
        // reflect the caller's intent, not the placeholder default.
        var def = Clone(_options.DefaultPolicy);
        def.Name = policyName;
        return def;
    }

    /// <summary>Names of all explicitly-registered policies.</summary>
    public IReadOnlyCollection<string> KnownPolicies =>
        _options.Policies.Keys.ToArray();
    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private static PolicyDefinition Clone(PolicyDefinition source)
    {
        return new PolicyDefinition
        {
            Name = source.Name,
            Retry = new RetryOptions
            {
                MaxAttempts = source.Retry.MaxAttempts,
                BaseDelayMs = source.Retry.BaseDelayMs,
                MaxDelayMs = source.Retry.MaxDelayMs,
                JitterRatio = source.Retry.JitterRatio,
                RetryOnPermanent = source.Retry.RetryOnPermanent
            },
            Circuit = new CircuitOptions
            {
                FailureThreshold = source.Circuit.FailureThreshold,
                OpenDurationSeconds = source.Circuit.OpenDurationSeconds,
                SuccessThreshold = source.Circuit.SuccessThreshold,
                OnlyCountTransient = source.Circuit.OnlyCountTransient
            },
            Timeout = new TimeoutOptions
            {
                TimeoutMs = source.Timeout.TimeoutMs
            },
            Fallback = new FallbackOptions
            {
                Enabled = source.Fallback.Enabled,
                Reason = source.Fallback.Reason
            },
            Logging = new LoggingOptions
            {
                EmitCallStarted = source.Logging.EmitCallStarted,
                EmitRetryAttempted = source.Logging.EmitRetryAttempted,
                EmitCallSucceeded = source.Logging.EmitCallSucceeded,
                EmitCallFailed = source.Logging.EmitCallFailed,
                EmitCircuitEvents = source.Logging.EmitCircuitEvents,
                EmitFallbackUsed = source.Logging.EmitFallbackUsed,
                EmitTimeoutBreached = source.Logging.EmitTimeoutBreached
            }
        };
    }
}
