// filepath: src/Portfolio.Resilience/Sinks/CompositeLogSink.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: Fans out each event to multiple log sinks. Failures in one sink never block the others.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ILogSink
//   Depends on : ILogSink, ResilienceEvent
//   Used by    : ServiceCollectionExtensions when multiple sinks are configured
//   See also   : docs/logging.md, SPEC.md §Logging
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// Forwards every <see cref="ResilienceEvent"/> to a set of child sinks.
/// If a child sink throws, the exception is caught and logged (via the optional
/// <see cref="ILogger"/>) so the remaining sinks still receive the event.
/// </summary>
public sealed class CompositeLogSink : ILogSink
{
    private readonly IReadOnlyList<ILogSink> _sinks;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a composite sink from the provided children.
    /// </summary>
    /// <param name="sinks">Child sinks. Must not be null. May be empty.</param>
    /// <param name="logger">Optional logger used to report child-sink failures.</param>
    public CompositeLogSink(
        IEnumerable<ILogSink> sinks,
        ILogger<CompositeLogSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        _sinks = sinks.Where(s => s is not null).ToArray();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Number of child sinks this composite forwards to.</summary>
    public int Count => _sinks.Count;

    /// <inheritdoc />
    public void Emit(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (var sink in _sinks)
        {
            try
            {
                sink.Emit(evt);
            }
            catch (Exception ex)
            {
                // A failing sink must not take down the pipeline. Log and continue.
                _logger.LogWarning(
                    ex,
                    "Log sink {SinkType} failed to emit event {EventType} for policy {PolicyName}",
                    sink.GetType().Name,
                    evt.EventType,
                    evt.PolicyName);
            }
        }
    }
}
