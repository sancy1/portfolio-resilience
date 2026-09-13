// filepath: src/Portfolio.Resilience/Configuration/RateLimitStrategy.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.6.0
// purpose: Selects the algorithm used by RateLimiterPolicyBuilder.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (enum)
//   Depends on : n/a
//   Used by    : RateLimiterOptions, RateLimiterPolicyBuilder
//   See also   : docs/rate-limiter.md, SPEC.md section 12
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// The algorithm a rate limiter uses to decide whether a call is permitted.
/// </summary>
/// <remarks>
/// Each strategy interprets <c>RateLimiterOptions.PermitLimit</c> and
/// <c>RateLimiterOptions.WindowSeconds</c> differently. See
/// <c>docs/rate-limiter.md</c> for the strategy-to-field matrix.
/// </remarks>
public enum RateLimitStrategy
{
    /// <summary>
    /// Token bucket. <c>PermitLimit</c> is the bucket capacity;
    /// <c>WindowSeconds</c> is the refill period. Tokens refill continuously.
    /// Allows bursts up to capacity.
    /// </summary>
    TokenBucket = 0,

    /// <summary>
    /// Sliding window. <c>PermitLimit</c> is the maximum number of calls
    /// permitted in any rolling <c>WindowSeconds</c> window. No boundary effects.
    /// </summary>
    SlidingWindow = 1,

    /// <summary>
    /// Fixed window. <c>PermitLimit</c> is the maximum number of calls permitted
    /// per fixed <c>WindowSeconds</c> bucket. Cheapest to implement; allows up
    /// to 2x burst at window boundaries.
    /// </summary>
    FixedWindow = 2,

    /// <summary>
    /// Concurrency limit. <c>PermitLimit</c> is the maximum number of
    /// simultaneous calls. No window is used; <c>WindowSeconds</c> is ignored.
    /// </summary>
    ConcurrencyLimit = 3
}
