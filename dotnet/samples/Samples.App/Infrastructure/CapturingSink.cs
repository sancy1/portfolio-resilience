// filepath: dotnet/samples/Samples.App/Infrastructure/CapturingSink.cs
// layer: Infrastructure | package: Samples.App | since: n/a
// purpose: In-memory ILogSink that captures every emitted ResilienceEvent for scenario assertions
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : Portfolio.Resilience.Abstractions.ILogSink
//   Depends on : Portfolio.Resilience.Events.ResilienceEvent
//   Used by    : Program (registration), all Scenarios (assertions), Unit tests
//   See also   : docs/logging.md - sink contract
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Samples.App.Infrastructure;

/// <summary>
/// An <see cref="ILogSink"/> that records every emitted <see cref="ResilienceEvent"/>
/// in memory so scenarios and tests can assert on the event stream a policy produced.
/// </summary>
public sealed class CapturingSink : ILogSink
{
    private readonly List<ResilienceEvent> _events = new();
    private readonly object _gate = new();

    /// <summary>A snapshot of every event captured so far, in emission order.</summary>
    public IReadOnlyList<ResilienceEvent> Events
    {
        get { lock (_gate) { return _events.ToArray(); } }
    }

    /// <summary>Records a single event.</summary>
    /// <param name="evt">The event to record. Must not be null.</param>
    public void Emit(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_gate) { _events.Add(evt); }
    }

    /// <summary>Removes every captured event.</summary>
    public void Clear()
    {
        lock (_gate) { _events.Clear(); }
    }
}