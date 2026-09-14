// filepath: src/Portfolio.Resilience/Sinks/CompositeLogSink.cs
// layer: Sinks | package: Portfolio.Resilience | since: v0.8.0
// purpose: Fans out each event to multiple log sinks, optionally scrubbing sensitive data first.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : ILogSink
//   Depends on : ILogSink, IEventScrubber, ResilienceEvent
//   Used by    : ServiceCollectionExtensions when multiple sinks are configured
//                or when sensitive-data scrubbing is enabled
//   See also   : docs/logging.md, docs/pci-scrubbing.md, SPEC.md section 19
// -----------------------------------------------------------------------------

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
/// <remarks>
/// When an <see cref="IEventScrubber"/> is provided, it runs <b>once</b> at the
/// top of <see cref="Emit"/>, before any child sees the event. Every child
/// receives the scrubbed copy - they never see the original.
/// </remarks>
public sealed class CompositeLogSink : ILogSink
{
    private readonly IReadOnlyList<ILogSink> _sinks;
    private readonly ILogger _logger;
    private readonly IEventScrubber? _scrubber;

    /// <summary>
    /// Creates a composite sink from the provided children.
    /// </summary>
    /// <param name="sinks">Child sinks. Must not be null. May be empty.</param>
    /// <param name="logger">Optional logger used to report child-sink failures.</param>
    /// <param name="scrubber">Optional scrubber run once before child fan-out.</param>
    public CompositeLogSink(
        IEnumerable<ILogSink> sinks,
        ILogger<CompositeLogSink>? logger = null,
        IEventScrubber? scrubber = null)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        _sinks = sinks.Where(s => s is not null).ToArray();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _scrubber = scrubber;
    }

    /// <summary>Number of child sinks this composite forwards to.</summary>
    public int Count => _sinks.Count;

    /// <summary>True when an <see cref="IEventScrubber"/> is configured.</summary>
    public bool HasScrubber => _scrubber is not null;

    /// <inheritdoc />
    public void Emit(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Scrub once, before any child sees the event.
        var effective = _scrubber is null ? evt : _scrubber.Scrub(evt);

        foreach (var sink in _sinks)
        {
            try
            {
                sink.Emit(effective);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Log sink {SinkType} failed to emit event {EventType} for policy {PolicyName}",
                    sink.GetType().Name,
                    effective.EventType,
                    effective.PolicyName);
            }
        }
    }
}
