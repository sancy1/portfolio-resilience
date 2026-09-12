// filepath: src/Portfolio.Resilience/Sinks/ConsoleLogSink.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: Writes structured resilience events to stdout as compact JSON lines.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ILogSink
//   Depends on : ResilienceEvent (Events/ResilienceEvent.cs)
//   Used by    : CompositeLogSink, ResilienceEventEmitter (Stage E), ServiceCollectionExtensions (Stage F)
//   See also   : docs/logging.md, SPEC.md §Logging
// ─────────────────────────────────────────────────────────────────────────────

using System.Text.Json;
using System.Text.Json.Serialization;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// Emits <see cref="ResilienceEvent"/> instances to <see cref="Console.Out"/> as
/// one compact JSON object per line. Safe for concurrent use.
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly object _gate = new();

    /// <inheritdoc />
    public void Emit(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        string line;
        try
        {
            line = JsonSerializer.Serialize(evt, JsonOptions);
        }
        catch (Exception ex)
        {
            // Serialization failures must never crash the caller — a logging sink
            // is a side-channel. Fall back to a minimal message.
            line = $"{{\"event_type\":\"serialization_failed\",\"error\":\"{ex.GetType().Name}\"}}";
        }

        // Console.Out is not thread-safe; guard with a monitor to keep lines intact.
        lock (_gate)
        {
            Console.Out.WriteLine(line);
        }
    }
}
