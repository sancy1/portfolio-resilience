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
    public ResilienceEventType EventType { get; init; }
    public string PolicyName { get; init; } = string.Empty;
    public string? CorrelationId { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public int? Attempt { get; init; }
    public double? DurationMs { get; init; }
    public ResilienceErrorCategory? ErrorCategory { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorType { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; }
        = new Dictionary<string, object?>();
}
