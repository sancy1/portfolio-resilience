// filepath: src/Portfolio.Resilience/Implementation/ResiliencePolicyRegistry.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.6.0
// purpose: Resolves PolicyDefinitions by name; validates new policies once and reports warnings.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : IResiliencePolicyRegistry
//   Depends on : ResilienceOptions, PolicyDefinition, RateLimiterOptions, BulkheadOptions
//   Used by    : ResilienceExecutor (Stage F)
//   See also   : docs/executor.md, SPEC.md section 2, docs/rate-limiter.md, docs/bulkhead.md
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Implementation;

/// <summary>
/// Looks up the effective <see cref="PolicyDefinition"/> for a name.
/// Unknown names resolve to <see cref="ResilienceOptions.DefaultPolicy"/> with the
/// Name overridden to the requested name.
/// </summary>
/// <remarks>
/// On the <b>first</b> resolve of any given policy name, the registry calls
/// <c>RateLimiter.Validate</c> and <c>Bulkhead.Validate</c> and forwards any
/// warnings to the optional <c>warn</c> delegate. Subsequent resolves of the
/// same policy skip validation. Validation never throws and never blocks
/// resolution — it is diagnostic only.
/// </remarks>
public sealed class ResiliencePolicyRegistry : IResiliencePolicyRegistry
{
    private readonly ResilienceOptions _options;
    private readonly Action<string>? _warn;

    // Tracks which policy names have already been validated. TryAdd returns
    // false if the name is already present, so validation runs exactly once
    // per name even under concurrent access.
    private readonly ConcurrentDictionary<string, bool> _validated =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a registry over the given options.
    /// </summary>
    /// <param name="options">The root options. A default instance is used if null.</param>
    /// <param name="warn">
    /// Optional callback for validation warnings. Called at most once per policy
    /// name, on first resolve. If null, warnings are silently discarded.
    /// </param>
    public ResiliencePolicyRegistry(
        ResilienceOptions? options = null,
        Action<string>? warn = null)
    {
        _options = options ?? new ResilienceOptions();
        _warn = warn;
    }

    /// <summary>
    /// Resolves the policy for <paramref name="policyName"/>.
    /// Returns a clone so callers cannot mutate shared state.
    /// </summary>
    public PolicyDefinition Resolve(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var definition = _options.Policies.TryGetValue(policyName, out var defined)
            ? Clone(defined)
            : BuildDefaultFor(policyName);

        ValidateOnce(policyName, definition);

        return definition;
    }

    /// <summary>Names of all explicitly-registered policies.</summary>
    public IReadOnlyCollection<string> KnownPolicies =>
        _options.Policies.Keys.ToArray();
    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private void ValidateOnce(string policyName, PolicyDefinition definition)
    {
        // If we have already validated this policy name, do nothing.
        if (!_validated.TryAdd(policyName, true))
        {
            return;
        }

        if (_warn is null)
        {
            return;
        }

        foreach (var warning in definition.RateLimiter.Validate(policyName))
        {
            _warn(warning);
        }

        foreach (var warning in definition.Bulkhead.Validate(policyName))
        {
            _warn(warning);
        }

        foreach (var warning in definition.Hedging.Validate(policyName))
        {
            _warn(warning);
        }
    }

    private PolicyDefinition BuildDefaultFor(string policyName)
    {
        // Unknown policy: use the default, but override Name so events/logs
        // reflect the caller's intent, not the placeholder default.
        var def = Clone(_options.DefaultPolicy);
        def.Name = policyName;
        return def;
    }

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
                EmitTimeoutBreached = source.Logging.EmitTimeoutBreached,
                EmitRateLimited = source.Logging.EmitRateLimited,
                EmitBulkheadRejected = source.Logging.EmitBulkheadRejected,
                EmitHedgeEvents = source.Logging.EmitHedgeEvents,
                ScrubSensitiveData = source.Logging.ScrubSensitiveData
            },
            RateLimiter = new RateLimiterOptions
            {
                Enabled = source.RateLimiter.Enabled,
                Strategy = source.RateLimiter.Strategy,
                PermitLimit = source.RateLimiter.PermitLimit,
                WindowSeconds = source.RateLimiter.WindowSeconds,
                QueueLimit = source.RateLimiter.QueueLimit,
                QueueTimeoutMs = source.RateLimiter.QueueTimeoutMs,
                RejectionCategory = source.RateLimiter.RejectionCategory
            },
            Bulkhead = new BulkheadOptions
            {
                Enabled = source.Bulkhead.Enabled,
                MaxConcurrency = source.Bulkhead.MaxConcurrency,
                MaxQueue = source.Bulkhead.MaxQueue,
                QueueTimeoutMs = source.Bulkhead.QueueTimeoutMs,
                RejectionCategory = source.Bulkhead.RejectionCategory
            },
            Hedging = new HedgingOptions
            {
                Enabled = source.Hedging.Enabled,
                MaxAttempts = source.Hedging.MaxAttempts,
                DelayMs = source.Hedging.DelayMs,
                ExponentialBackoff = source.Hedging.ExponentialBackoff,
                AttemptTimeoutMs = source.Hedging.AttemptTimeoutMs,
                CancelOnSuccess = source.Hedging.CancelOnSuccess,
                EmitAttemptEvents = source.Hedging.EmitAttemptEvents,
                RejectionCategory = source.Hedging.RejectionCategory
            }
        };
    }
}
