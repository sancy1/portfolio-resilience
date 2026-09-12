// filepath: src/Portfolio.Resilience/Implementation/ResilienceEventEmitter.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.3.0
// purpose: Builds structured ResilienceEvents from policy decisions and forwards them to a log sink.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (concrete service)
//   Depends on : ILogSink, ResilienceEvent, ResilienceErrorCategory, CorrelationContext
//   Used by    : ResilienceExecutor (Stage F)
//   See also   : docs/logging.md, SPEC.md §Events
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;
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
    // Convenience emitters — one per ResilienceEventType
    // ------------------------------------------------------------------------

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
}
