// filepath: src/Portfolio.Resilience/Implementation/ResilienceEventEmitter.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.6.0
// purpose: Builds structured ResilienceEvents from policy decisions and forwards them to a log sink.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (concrete service)
//   Depends on : ILogSink, ResilienceEvent, ResilienceErrorCategory, RateLimitStrategy, CorrelationContext
//   Used by    : ResilienceExecutor, RateLimiterPolicyBuilder, BulkheadPolicyBuilder
//   See also   : docs/logging.md, SPEC.md section 7.1, docs/rate-limiter.md, docs/bulkhead.md
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Implementation;

/// <summary>
/// Centralizes construction of <see cref="ResilienceEvent"/> objects. Attaches the
/// current correlation ID automatically, so every event emitted anywhere in the
/// pipeline is traceable back to its originating request.
/// </summary>
public sealed class ResilienceEventEmitter
{
    private readonly ILogSink _sink;

    public ResilienceEventEmitter(ILogSink? sink = null)
    {
        _sink = sink ?? new Sinks.NullLogSink();
    }

    /// <summary>Emits a fully-formed event through the sink.</summary>
    public void Emit(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _sink.Emit(evt);
    }

    // ------------------------------------------------------------------------
    // Convenience emitters - one per ResilienceEventType
    // ------------------------------------------------------------------------

    /// <summary>Emits <see cref="ResilienceEventType.CallStarted"/>.</summary>
    public void EmitCallStarted(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CallStarted,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.RetryAttempted"/>.</summary>
    public void EmitRetryAttempted(
        string policyName,
        int attempt,
        Exception? error,
        double? delayMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.RetryAttempted,
            PolicyName = policyName,
            Attempt = attempt,
            ErrorCategory = error is null ? null : ResilienceErrorCategory.Transient,
            ErrorMessage = error?.Message,
            ErrorType = error?.GetType().FullName,
            CorrelationId = CorrelationContext.CurrentId,
            Metadata = delayMs.HasValue
                ? new Dictionary<string, object?> { ["delay_ms"] = delayMs.Value }
                : new Dictionary<string, object?>()
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.CallSucceeded"/>.</summary>
    public void EmitCallSucceeded(string policyName, double durationMs, int attempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CallSucceeded,
            PolicyName = policyName,
            Attempt = attempts,
            DurationMs = durationMs,
            CorrelationId = CorrelationContext.CurrentId
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.CallFailed"/>.</summary>
    public void EmitCallFailed(
        string policyName,
        Exception error,
        int attempts,
        TimeSpan totalDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(error);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CallFailed,
            PolicyName = policyName,
            Attempt = attempts,
            DurationMs = totalDuration.TotalMilliseconds,
            ErrorCategory = error is ResilienceException rex
                ? rex.Category
                : ResilienceErrorCategory.Unknown,
            ErrorMessage = error.Message,
            ErrorType = error.GetType().FullName,
            CorrelationId = CorrelationContext.CurrentId
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.CircuitOpened"/>.</summary>
    public void EmitCircuitOpened(string policyName, int failures, DateTime openedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CircuitOpened,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId,
            Metadata = new Dictionary<string, object?>
            {
                ["consecutive_failures"] = failures,
                ["opened_at_utc"] = openedAtUtc
            }
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.CircuitClosed"/>.</summary>
    public void EmitCircuitClosed(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CircuitClosed,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.CircuitHalfOpened"/>.</summary>
    public void EmitCircuitHalfOpened(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CircuitHalfOpened,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.FallbackUsed"/>.</summary>
    public void EmitFallbackUsed(string policyName, string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.FallbackUsed,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId,
            Metadata = reason is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["reason"] = reason }
        });
    }

    /// <summary>Emits <see cref="ResilienceEventType.TimeoutBreached"/>.</summary>
    public void EmitTimeoutBreached(string policyName, int timeoutMs, double elapsedMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.TimeoutBreached,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId,
            Metadata = new Dictionary<string, object?>
            {
                ["timeout_ms"] = timeoutMs,
                ["elapsed_ms"] = elapsedMs
            }
        });
    }

    /// <summary>
    /// Emits <see cref="ResilienceEventType.RateLimited"/> when a rate limiter
    /// rejects a call (no permit available, or queue full/timeout).
    /// </summary>
    /// <param name="policyName">The policy that rejected the call.</param>
    /// <param name="strategy">The strategy in effect.</param>
    /// <param name="permitLimit">The configured permit limit.</param>
    /// <param name="windowSeconds">The configured window, or null for ConcurrencyLimit.</param>
    /// <param name="queueLimit">The configured queue limit.</param>
    /// <param name="queueDepth">The queue depth at the moment of rejection (0 when the strategy does not queue).</param>
    public void EmitRateLimited(
        string policyName,
        RateLimitStrategy strategy,
        int permitLimit,
        int? windowSeconds,
        int queueLimit,
        int queueDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var metadata = new Dictionary<string, object?>
        {
            ["strategy"] = strategy.ToString(),
            ["permit_limit"] = permitLimit,
            ["queue_limit"] = queueLimit,
            ["queue_depth"] = queueDepth
        };

        if (windowSeconds.HasValue)
        {
            metadata["window_seconds"] = windowSeconds.Value;
        }

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.RateLimited,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId,
            Metadata = metadata
        });
    }

    /// <summary>
    /// Emits <see cref="ResilienceEventType.BulkheadRejected"/> when a bulkhead
    /// rejects a call (no concurrency slot, queue full, or queue timeout).
    /// </summary>
    /// <param name="policyName">The policy that rejected the call.</param>
    /// <param name="maxConcurrency">The configured concurrency cap.</param>
    /// <param name="maxQueue">The configured queue cap.</param>
    /// <param name="queueDepth">The queue depth at the moment of rejection.</param>
    /// <param name="reason">One of "queue_full", "queue_timeout", or "rejected_immediately".</param>
    public void EmitBulkheadRejected(
        string policyName,
        int maxConcurrency,
        int maxQueue,
        int queueDepth,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.BulkheadRejected,
            PolicyName = policyName,
            CorrelationId = CorrelationContext.CurrentId,
            Metadata = new Dictionary<string, object?>
            {
                ["max_concurrency"] = maxConcurrency,
                ["max_queue"] = maxQueue,
                ["queue_depth"] = queueDepth,
                ["reason"] = reason
            }
        });
    }
}
