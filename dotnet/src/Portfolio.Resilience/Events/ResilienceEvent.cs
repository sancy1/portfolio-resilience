// filepath: src/Portfolio.Resilience/Events/ResilienceEvent.cs
using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Events;

/// <summary>
/// A single structured event emitted by the resilience pipeline.
/// Sinks receive instances of this and render them (JSON, OTel, Console, etc.).
/// Field names are part of the cross-language contract in SPEC.md.
/// </summary>
public sealed record ResilienceEvent
{
    /// <summary>
    /// The kind of pipeline decision this event represents. See
    /// <see cref="ResilienceEventType"/> for the full set of values.
    /// </summary>
    public ResilienceEventType EventType { get; init; }

    /// <summary>
    /// The name of the policy under which the operation ran. Matches the name
    /// passed to <c>ExecuteAsync</c> and the name registered with
    /// <c>AddPolicy</c>. Used to correlate events with a specific call site.
    /// </summary>
    public string PolicyName { get; init; } = string.Empty;

    /// <summary>
    /// The ambient correlation ID at the moment the event was emitted, or null
    /// if no correlation scope was active. Populated automatically by the
    /// executor from <c>CorrelationContext.CurrentId</c>. One correlation ID
    /// per logical request lets you reconstruct a full timeline across services.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// The UTC timestamp when the event was created. Defaults to
    /// <see cref="DateTime.UtcNow"/> at construction time. Use this for
    /// chronological ordering when event delivery order is not guaranteed.
    /// </summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The attempt number this event refers to, when applicable. For retry,
    /// hedge, and call-completion events, this is the 1-based attempt index.
    /// Null for events that do not relate to a specific attempt (for example
    /// <c>CallStarted</c> or <c>CircuitOpened</c>).
    /// </summary>
    public int? Attempt { get; init; }

    /// <summary>
    /// The wall-clock duration of the attempt or operation this event refers
    /// to, in milliseconds. Populated on <c>CallSucceeded</c>, <c>CallFailed</c>,
    /// and hedge-completion events. Null otherwise.
    /// </summary>
    public double? DurationMs { get; init; }

    /// <summary>
    /// The resilience error category for failure-related events. Populated on
    /// <c>CallFailed</c> and other failure events. See
    /// <see cref="ResilienceErrorCategory"/> for the possible values. Null on
    /// success events.
    /// </summary>
    public ResilienceErrorCategory? ErrorCategory { get; init; }

    /// <summary>
    /// The human-readable error message for failure-related events. Already
    /// scrubbed by the configured <c>IEventScrubber</c> when
    /// <c>LoggingOptions.ScrubSensitiveData</c> is enabled. Null on success
    /// events.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The fully qualified type name of the exception that triggered the
    /// failure. Null on success events. Useful for grouping failures by
    /// exception type without parsing the error message.
    /// </summary>
    public string? ErrorType { get; init; }

    /// <summary>
    /// Additional structured fields specific to the event type. Keys and values
    /// are part of the cross-language contract in SPEC.md - for example
    /// <c>delay_ms</c> on retry events, or <c>strategy</c> on rate-limit
    /// rejections. Never null; an empty dictionary is used when no metadata is
    /// present. Values are scrubbed by the configured <c>IEventScrubber</c>
    /// when <c>Logging.ScrubSensitiveData</c> is enabled.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
        = new Dictionary<string, object?>();
}