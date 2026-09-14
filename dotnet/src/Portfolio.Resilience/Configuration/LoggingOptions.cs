// filepath: src/Portfolio.Resilience/Configuration/LoggingOptions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.6.0
// purpose: Per-event-type toggles for which resilience events are emitted.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : n/a
//   Used by    : PolicyDefinition, ResilienceOptions
//   See also   : docs/logging.md, SPEC.md section 7
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Controls which event types a policy emits. The default is "all except the
/// verbose <see cref="EmitCallStarted"/>".
/// </summary>
/// <remarks>
/// <para>
/// <b>Current behaviour (v0.6.0):</b> the emitter fires every configured event
/// unconditionally. These toggles define the intended contract and are part of
/// the stable public API, but the wiring that makes them suppress emission is a
/// v0.7.0 item. Until then, setting a toggle to <c>false</c> does not prevent
/// the corresponding event from being emitted.
/// </para>
/// <para>
/// <b>Circuit events:</b> <see cref="EmitCircuitEvents"/> is a single toggle
/// covering all three circuit event types (<c>CircuitOpened</c>,
/// <c>CircuitClosed</c>, <c>CircuitHalfOpened</c>).
/// </para>
/// </remarks>
public sealed class LoggingOptions
{
    /// <summary>Emit <c>call_started</c> events. Default: false (verbose).</summary>
    public bool EmitCallStarted { get; set; } = false;

    /// <summary>Emit <c>retry_attempted</c> events. Default: true.</summary>
    public bool EmitRetryAttempted { get; set; } = true;

    /// <summary>Emit <c>call_succeeded</c> events. Default: true.</summary>
    public bool EmitCallSucceeded { get; set; } = true;

    /// <summary>Emit <c>call_failed</c> events. Default: true.</summary>
    public bool EmitCallFailed { get; set; } = true;

    /// <summary>
    /// Emit circuit events (<c>circuit_opened</c>, <c>circuit_closed</c>,
    /// <c>circuit_half_opened</c>). Default: true.
    /// </summary>
    public bool EmitCircuitEvents { get; set; } = true;

    /// <summary>Emit <c>fallback_used</c> events. Default: true.</summary>
    public bool EmitFallbackUsed { get; set; } = true;

    /// <summary>Emit <c>timeout_breached</c> events. Default: true.</summary>
    public bool EmitTimeoutBreached { get; set; } = true;

    /// <summary>Emit <c>rate_limited</c> events. Default: true.</summary>
    public bool EmitRateLimited { get; set; } = true;

    /// <summary>Emit <c>bulkhead_rejected</c> events. Default: true.</summary>
    public bool EmitBulkheadRejected { get; set; } = true;

    /// <summary>
    /// Emit hedge events (<c>hedge_won</c>, <c>hedge_lost</c>,
    /// <c>hedge_cancelled</c>). Default: true.
    /// </summary>
    public bool EmitHedgeEvents { get; set; } = true;

    /// <summary>
    /// When true, every event runs through an <see cref="Abstractions.IEventScrubber"/>
    /// (the default PCI scrubber unless a custom one is registered) before any
    /// sink sees it. Masks PAN, CVV, and SSN patterns in messages and metadata.
    /// Default: false - opt-in, so existing consumers see no change in log output.
    /// </summary>
    public bool ScrubSensitiveData { get; set; } = false;
}
