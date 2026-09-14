// filepath: src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryLogSink.cs
// layer: OpenTelemetry | package: Portfolio.Resilience.OpenTelemetry | since: v0.7.0
// purpose: Forwards ResilienceEvent instances to OpenTelemetry as structured log records.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : ILogSink
//   Depends on : ILoggerFactory, ResilienceEvent, ResilienceEventType
//   Used by    : ServiceCollectionExtensions (user opt-in via AddOpenTelemetrySinks)
//   See also   : docs/opentelemetry.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.OpenTelemetry;

/// <summary>
/// An <see cref="ILogSink"/> that maps every <see cref="ResilienceEvent"/> to
/// one OpenTelemetry log record. Event fields become structured attributes
/// prefixed <c>resilience.</c> so they appear as searchable dimensions in any
/// OTel-compatible backend (Grafana, Datadog, Jaeger, Application Insights).
/// </summary>
/// <remarks>
/// <para>
/// This sink does not configure a logging provider. It uses the
/// <see cref="ILoggerFactory"/> you provide at construction. In a typical
/// ASP.NET Core app, that factory is the DI-registered one, already wired to
/// your configured OTel exporter. In a console or worker service, wire the
/// factory yourself.
/// </para>
/// <para>
/// Every event maps to a log record with a <see cref="LogLevel"/> derived from
/// its <see cref="ResilienceEventType"/>. See <c>docs/opentelemetry.md</c> for
/// the mapping table.
/// </para>
/// <para>
/// The sink is thread-safe. Emit may be called concurrently from any pipeline
/// layer.
/// </para>
/// </remarks>
public sealed class OpenTelemetryLogSink : ILogSink
{
    private const string LoggerCategory = "Portfolio.Resilience";

    private readonly ILogger _logger;

    /// <summary>
    /// Creates a sink that writes to loggers produced by
    /// <paramref name="loggerFactory"/>.
    /// </summary>
    /// <param name="loggerFactory">The logger factory to obtain loggers from.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="loggerFactory"/> is null.</exception>
    public OpenTelemetryLogSink(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(LoggerCategory);
    }

    /// <inheritdoc />
    public void Emit(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // We deliberately do not short-circuit on ILogger.IsEnabled here.
        // The sink's job is to emit what it is given; filtering is the logger's
        // responsibility. Filtering here would silently drop events when the
        // logger's minimum level is stricter than the event's level.
        EmitStructured(evt);
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private void EmitStructured(ResilienceEvent evt)
    {
        // Common fields present on every event.
        var attributes = new List<KeyValuePair<string, object?>>
        {
            new("resilience.event_type", EventTypeName(evt.EventType)),
            new("resilience.policy_name", evt.PolicyName),
            new("resilience.timestamp_utc", evt.TimestampUtc)
        };

        if (!string.IsNullOrEmpty(evt.CorrelationId))
        {
            attributes.Add(new("resilience.correlation_id", evt.CorrelationId));
        }

        if (evt.Attempt.HasValue)
        {
            attributes.Add(new("resilience.attempt", evt.Attempt.Value));
        }

        if (evt.DurationMs.HasValue)
        {
            attributes.Add(new("resilience.duration_ms", evt.DurationMs.Value));
        }

        if (evt.ErrorCategory.HasValue)
        {
            attributes.Add(new("resilience.error_category", evt.ErrorCategory.Value.ToString()));
        }

        if (!string.IsNullOrEmpty(evt.ErrorMessage))
        {
            attributes.Add(new("resilience.error_message", evt.ErrorMessage));
        }

        if (!string.IsNullOrEmpty(evt.ErrorType))
        {
            attributes.Add(new("resilience.error_type", evt.ErrorType));
        }

        // Metadata keys are flattened as resilience.metadata.<key>.
        if (evt.Metadata is not null)
        {
            foreach (var kv in evt.Metadata)
            {
                attributes.Add(new($"resilience.metadata.{kv.Key}", kv.Value));
            }
        }

        // Log with structured key-value pairs. OTel's logging bridge picks
        // these up as attributes on the resulting LogRecord.
        var state = new StructuredState(attributes);
        _logger.Log(
            logLevel: LevelFor(evt.EventType),
            eventId: default,
            state: state,
            exception: null,
            formatter: static (s, _) => s.Message);
    }

    /// <summary>
    /// Maps a resilience event type to a log level. Uses PascalCase names
    /// matching <see cref="ResilienceEventType"/>.
    /// </summary>
    private static LogLevel LevelFor(ResilienceEventType type) => type switch
    {
        ResilienceEventType.CallStarted       => LogLevel.Debug,
        ResilienceEventType.CallSucceeded     => LogLevel.Information,
        ResilienceEventType.CallFailed        => LogLevel.Error,
        ResilienceEventType.RetryAttempted    => LogLevel.Warning,
        ResilienceEventType.CircuitOpened     => LogLevel.Warning,
        ResilienceEventType.CircuitClosed     => LogLevel.Information,
        ResilienceEventType.CircuitHalfOpened => LogLevel.Information,
        ResilienceEventType.FallbackUsed      => LogLevel.Information,
        ResilienceEventType.TimeoutBreached   => LogLevel.Warning,
        ResilienceEventType.RateLimited       => LogLevel.Warning,
        ResilienceEventType.BulkheadRejected  => LogLevel.Warning,
        _                                     => LogLevel.Information
    };

    /// <summary>
    /// Converts a <see cref="ResilienceEventType"/> to the snake_case name used
    /// in SPEC section 7.1 (<c>call_started</c>, <c>rate_limited</c>, ...).
    /// </summary>
    private static string EventTypeName(ResilienceEventType type) => type switch
    {
        ResilienceEventType.CallStarted       => "call_started",
        ResilienceEventType.CallSucceeded     => "call_succeeded",
        ResilienceEventType.CallFailed        => "call_failed",
        ResilienceEventType.RetryAttempted    => "retry_attempted",
        ResilienceEventType.CircuitOpened     => "circuit_opened",
        ResilienceEventType.CircuitClosed     => "circuit_closed",
        ResilienceEventType.CircuitHalfOpened => "circuit_half_opened",
        ResilienceEventType.FallbackUsed      => "fallback_used",
        ResilienceEventType.TimeoutBreached   => "timeout_breached",
        ResilienceEventType.RateLimited       => "rate_limited",
        ResilienceEventType.BulkheadRejected  => "bulkhead_rejected",
        _                                     => type.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Minimal IReadOnlyList-based log state that exposes the attributes as
    /// structured key-value pairs.
    /// </summary>
    private sealed class StructuredState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly IReadOnlyList<KeyValuePair<string, object?>> _items;

        public StructuredState(IReadOnlyList<KeyValuePair<string, object?>> items) => _items = items;

        public KeyValuePair<string, object?> this[int index] => _items[index];

        public int Count => _items.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

        /// <summary>The formatted message for this log record.</summary>
        public string Message => "Resilience event";
    }
}
