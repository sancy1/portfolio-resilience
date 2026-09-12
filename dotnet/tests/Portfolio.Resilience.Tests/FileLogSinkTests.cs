// filepath: tests/Portfolio.Resilience.Tests/FileLogSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies FileLogSink writes JSON Lines, rotates daily, releases file handles, and is thread-safe.
// RELATIONSHIPS
//   Tests      : FileLogSink (Sinks/FileLogSink.cs)
//   Depends on : xUnit, FluentAssertions, System.Text.Json
//   See also   : docs/logging.md
//
//   Note: On Windows, a FileLogSink holds an open write handle. Tests that
//   read the file back MUST dispose the sink first — otherwise File.ReadAllLines
//   fails with "file is being used by another process". We use explicit
//   `using { }` blocks so disposal happens before reading.

using System.Text.Json;
using FluentAssertions;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class FileLogSinkTests : IDisposable
{
    private readonly string _tempDir;

    public FileLogSinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "resilience-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
    [Fact]
    public void Constructor_CreatesDirectoryIfMissing()
    {
        var nested = Path.Combine(_tempDir, "a", "b", "c");
        using var _ = new FileLogSink(nested);
        Directory.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWhitespaceDirectory()
    {
        Action actNull = () => new FileLogSink(null!);
        Action actEmpty = () => new FileLogSink("");
        Action actWhitespace = () => new FileLogSink("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWhitespacePrefix()
    {
        Action actNull = () => new FileLogSink(_tempDir, null!);
        Action actEmpty = () => new FileLogSink(_tempDir, "");
        Action actWhitespace = () => new FileLogSink(_tempDir, "   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Emit_ThrowsOnNullEvent()
    {
        using var sink = new FileLogSink(_tempDir);
        Action act = () => sink.Emit(null!);
        act.Should().Throw<ArgumentNullException>();
    }
    [Fact]
    public void Emit_CreatesFileWithTodaysDateInName()
    {
        using (var sink = new FileLogSink(_tempDir, prefix: "test"))
        {
            sink.Emit(new ResilienceEvent
            {
                EventType = ResilienceEventType.CallSucceeded,
                PolicyName = "p"
            });
        }

        var expectedFileName = $"test-{DateTime.UtcNow:yyyy-MM-dd}.jsonl";
        File.Exists(Path.Combine(_tempDir, expectedFileName)).Should().BeTrue();
    }

    [Fact]
    public void Emit_WritesOneJsonLinePerEvent()
    {
        using (var sink = new FileLogSink(_tempDir))
        {
            sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallStarted, PolicyName = "p1" });
            sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallSucceeded, PolicyName = "p2" });
            sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallFailed, PolicyName = "p3" });
        }

        var file = Directory.GetFiles(_tempDir, "*.jsonl").Single();
        var lines = File.ReadAllLines(file);

        lines.Should().HaveCount(3);
        foreach (var line in lines)
        {
            Action parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow();
        }
    }

    [Fact]
    public void Emit_AppendsToExistingFile()
    {
        using (var sink1 = new FileLogSink(_tempDir))
        {
            sink1.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallStarted, PolicyName = "first" });
        }

        using (var sink2 = new FileLogSink(_tempDir))
        {
            sink2.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallSucceeded, PolicyName = "second" });
        }

        var file = Directory.GetFiles(_tempDir, "*.jsonl").Single();
        var lines = File.ReadAllLines(file);
        lines.Should().HaveCount(2);
    }
    [Fact]
    public void Emit_IsThreadSafe_NoInterleavedLines()
    {
        var lineCount = 200;

        using (var sink = new FileLogSink(_tempDir))
        {
            Parallel.For(0, lineCount, i =>
            {
                sink.Emit(new ResilienceEvent
                {
                    EventType = ResilienceEventType.CallSucceeded,
                    PolicyName = $"policy-{i}"
                });
            });
        }

        var file = Directory.GetFiles(_tempDir, "*.jsonl").Single();
        var lines = File.ReadAllLines(file);

        lines.Should().HaveCount(lineCount);
        foreach (var line in lines)
        {
            Action parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow();
        }
    }

    [Fact]
    public void Dispose_ReleasesFileHandle_AllowingDeletion()
    {
        var sink = new FileLogSink(_tempDir);
        sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallStarted, PolicyName = "p" });
        sink.Dispose();

        Action act = () =>
        {
            var file = Directory.GetFiles(_tempDir, "*.jsonl").Single();
            File.Delete(file);
        };

        act.Should().NotThrow();
    }
}
