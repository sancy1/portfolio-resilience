// filepath: src/Portfolio.Resilience/Abstractions/IEventScrubber.cs
// layer: Abstractions | package: Portfolio.Resilience | since: v0.8.0
// purpose: Contract for redacting sensitive data from events before they reach any log sink.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (interface)
//   Depends on : ResilienceEvent
//   Used by    : CompositeLogSink, ServiceCollectionExtensions, DefaultPciScrubber
//   See also   : docs/pci-scrubbing.md, docs/logging.md, SPEC.md section 19
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// Redacts sensitive data from a <see cref="ResilienceEvent"/> before it is
/// delivered to any log sink. Runs once per event, before the composite
/// fan-out, so every child sink sees the scrubbed version.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must return a new event instance - never mutate the input.
/// <see cref="ResilienceEvent"/> is a record with init-only properties; use
/// <c>evt with { ... }</c> to produce a scrubbed copy.
/// </para>
/// <para>
/// Implementations must be thread-safe. The same instance is invoked
/// concurrently from every emitting thread.
/// </para>
/// </remarks>
public interface IEventScrubber
{
    /// <summary>
    /// Returns a scrubbed copy of <paramref name="evt"/>. The input is never
    /// mutated.
    /// </summary>
    /// <param name="evt">The event to scrub. Must not be null.</param>
    /// <returns>A new event with sensitive patterns redacted.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="evt"/> is null.</exception>
    ResilienceEvent Scrub(ResilienceEvent evt);
}
