// filepath: src/Portfolio.Resilience/Events/ResilienceEventType.cs
namespace Portfolio.Resilience.Events;

/// <summary>
/// Canonical event types emitted by the resilience pipeline.
/// Field names and semantics are defined in SPEC.md and shared by every language implementation.
/// </summary>
public enum ResilienceEventType
{
    CallStarted = 0,
    RetryAttempted = 1,
    CallSucceeded = 2,
    CallFailed = 3,
    CircuitOpened = 4,
    CircuitClosed = 5,
    CircuitHalfOpened = 6,
    FallbackUsed = 7,
    TimeoutBreached = 8
}
