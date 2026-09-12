// filepath: src/Portfolio.Resilience/Sinks/NullLogSink.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: No-op log sink. Discards every event. Useful for tests and as a safe default.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ILogSink
//   Depends on : ResilienceEvent (Events/ResilienceEvent.cs)
//   Used by    : Tests, as a safe default when no logging is configured
//   See also   : docs/logging.md
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// A sink that intentionally discards every event.
/// Use when a service has not yet configured logging, or in tests
/// where log side-effects are undesirable.
/// </summary>
public sealed class NullLogSink : ILogSink
{
    /// <summary>Singleton instance — no state, safe to share.</summary>
    public static readonly NullLogSink Instance = new();

    /// <inheritdoc />
    public void Emit(ResilienceEvent evt)
    {
        // Intentionally empty. The parameter is unused by design.
        _ = evt;
    }
}
