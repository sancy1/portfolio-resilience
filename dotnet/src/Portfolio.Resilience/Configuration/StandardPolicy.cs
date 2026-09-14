// filepath: src/Portfolio.Resilience/Configuration/StandardPolicy.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.7.0
// purpose: The built-in "standard" policy used by AddStandardResilienceHandler().
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static factory)
//   Depends on : PolicyDefinition, RetryOptions, CircuitOptions, TimeoutOptions
//   Used by    : HttpClientBuilderExtensions.AddStandardResilienceHandler
//   See also   : docs/http-integration.md, SPEC.md section 3
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// The built-in "standard" policy used by
/// <c>AddStandardResilienceHandler()</c>. Provides safe defaults for retry,
/// circuit breaking, and timeout.
/// </summary>
/// <remarks>
/// <para>
/// The standard policy is deliberately conservative:
/// <list type="bullet">
///   <item><b>Retry enabled</b> - 3 attempts, 100ms base delay, 5s cap, 0.3 jitter.</item>
///   <item><b>Circuit breaker enabled</b> - opens after 5 consecutive transient failures, 30s open duration.</item>
///   <item><b>Timeout enabled</b> - 30 seconds per attempt.</item>
///   <item><b>Rate limiter, bulkhead, and hedging are NOT enabled by default.</b>
///     These features multiply load or duplicate requests and must be opted into
///     explicitly per policy.</item>
/// </list>
/// </para>
/// <para>
/// A user may override the standard policy by registering their own policy
/// named <see cref="Name"/> before calling <c>AddStandardResilienceHandler</c>.
/// In that case the user's policy wins and the extension's default is not applied.
/// </para>
/// </remarks>
public static class StandardPolicy
{
    /// <summary>The reserved policy name used by the standard handler.</summary>
    public const string Name = "standard";

    /// <summary>
    /// Creates the standard policy definition with default values.
    /// Callers may mutate the returned instance before registering it.
    /// </summary>
    /// <returns>A new <see cref="PolicyDefinition"/> named <see cref="Name"/>.</returns>
    public static PolicyDefinition Create() => new()
    {
        Name = Name,
        Retry = new RetryOptions
        {
            MaxAttempts = 3,
            BaseDelayMs = 100,
            MaxDelayMs = 5_000,
            JitterRatio = 0.3,
            RetryOnPermanent = false
        },
        Circuit = new CircuitOptions
        {
            FailureThreshold = 5,
            OpenDurationSeconds = 30,
            SuccessThreshold = 1,
            OnlyCountTransient = true
        },
        Timeout = new TimeoutOptions
        {
            TimeoutMs = 30_000
        },
        Fallback = new FallbackOptions { Enabled = false },
        Logging = new LoggingOptions(),
        RateLimiter = new RateLimiterOptions { Enabled = false },
        Bulkhead = new BulkheadOptions { Enabled = false },
        Hedging = new HedgingOptions { Enabled = false }
    };
}
