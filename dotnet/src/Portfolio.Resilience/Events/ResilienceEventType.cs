// filepath: src/Portfolio.Resilience/Events/ResilienceEventType.cs
// layer: Events | package: Portfolio.Resilience | since: v0.6.0
// purpose: Canonical event types emitted by the resilience pipeline.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (enum)
//   Depends on : n/a
//   Used by    : ResilienceEvent, ResilienceEventEmitter, ILogSink implementations
//   See also   : docs/logging.md, SPEC.md section 7.1, docs/rate-limiter.md, docs/bulkhead.md
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Events;

/// <summary>
/// Canonical event types emitted by the resilience pipeline.
/// Field names and semantics are defined in SPEC.md and shared by every language implementation.
/// </summary>
public enum ResilienceEventType
{
    /// <summary>An operation is about to be attempted.</summary>
    CallStarted = 0,

    /// <summary>A retry is about to run after a delay.</summary>
    RetryAttempted = 1,

    /// <summary>The operation returned successfully.</summary>
    CallSucceeded = 2,

    /// <summary>The operation failed (all retries exhausted, or non-retryable).</summary>
    CallFailed = 3,

    /// <summary>Circuit transitioned Closed -&gt; Open.</summary>
    CircuitOpened = 4,

    /// <summary>Circuit transitioned HalfOpen -&gt; Closed.</summary>
    CircuitClosed = 5,

    /// <summary>Circuit transitioned Open -&gt; HalfOpen.</summary>
    CircuitHalfOpened = 6,

    /// <summary>The caller-provided fallback produced a value.</summary>
    FallbackUsed = 7,

    /// <summary>The timeout ceiling fired.</summary>
    TimeoutBreached = 8,

    /// <summary>A rate limiter rejected a call (no permit, or queue full/timeout).</summary>
    RateLimited = 9,

    /// <summary>A bulkhead rejected a call (no concurrency slot, or queue full/timeout).</summary>
    BulkheadRejected = 10
}
