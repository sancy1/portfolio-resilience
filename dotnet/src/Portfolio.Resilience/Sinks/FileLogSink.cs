// filepath: src/Portfolio.Resilience/Sinks/FileLogSink.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: Writes resilience events as JSON Lines to a daily rotating file.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ILogSink, IDisposable
//   Depends on : ResilienceEvent (Events/ResilienceEvent.cs)
//   Used by    : CompositeLogSink, ServiceCollectionExtensions (opt-in)
//   See also   : docs/logging.md
// ─────────────────────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// Appends each <see cref="ResilienceEvent"/> to a daily rotating file as one
/// compact JSON object per line (JSON Lines format). The file is rotated at
/// midnight (UTC) and named <c>resilience-YYYY-MM-DD.jsonl</c>.
/// Safe for concurrent use.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;

    private StreamWriter? _writer;
    private DateOnly _writerDate;

    /// <summary>
    /// Creates a file sink that writes to <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">Directory where log files are written. Created if missing.</param>
    /// <param name="prefix">File name prefix. Default: <c>resilience</c>.</param>
    public FileLogSink(string directory, string prefix = "resilience")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        _directory = directory;
        _prefix = prefix;

        Directory.CreateDirectory(_directory);
    }

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
            line = $"{{\"event_type\":\"serialization_failed\",\"error\":\"{ex.GetType().Name}\"}}";
        }

        // File I/O and rotation must be atomic with respect to other writers.
        lock (_gate)
        {
            EnsureWriterForToday();
            _writer!.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <summary>Ensures the underlying writer points at the file for today's UTC date.</summary>
    private void EnsureWriterForToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        // Rotation boundary — close the previous day's file and open today's.
        _writer?.Dispose();
        _writer = null;

        var fileName = $"{_prefix}-{today:yyyy-MM-dd}.jsonl";
        var fullPath = Path.Combine(_directory, fileName);

        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

        _writerDate = today;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
