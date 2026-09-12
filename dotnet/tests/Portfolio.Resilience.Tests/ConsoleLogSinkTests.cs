// filepath: tests/Portfolio.Resilience.Tests/ConsoleLogSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies ConsoleLogSink writes valid JSON Lines, escapes correctly, and is thread-safe.
// RELATIONSHIPS
//   Tests      : ConsoleLogSink (Sinks/ConsoleLogSink.cs)
//   Depends on : xUnit, FluentAssertions, System.Text.Json
//   See also   : docs/logging.md

using System.Text.Json;
using FluentAssertions;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ConsoleLogSinkTests
{
    [Fact]
    public void Emit_ThrowsOnNullEvent()
    {
        var sink = new ConsoleLogSink();
        Action act = () => sink.Emit(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Emit_WritesSingleJsonLineToStdout()
    {
        var sink = new ConsoleLogSink();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallSucceeded,
            PolicyName = "test-policy",
            CorrelationId = "abc-123",
            DurationMs = 42.5,
            Attempt = 1
        };

        var captured = CaptureConsoleOutput(() => sink.Emit(evt));

        captured.Should().NotBeNullOrWhiteSpace();
        captured.Should().EndWith(Environment.NewLine);
        captured.Trim().Split('\n').Should().HaveCount(1);
    }
    [Fact]
    public void Emit_ProducesValidJson()
    {
        var sink = new ConsoleLogSink();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallSucceeded,
            PolicyName = "test-policy",
            CorrelationId = "abc-123",
            DurationMs = 42.5
        };

        var captured = CaptureConsoleOutput(() => sink.Emit(evt)).Trim();

        Action parse = () =>
        {
            using var doc = JsonDocument.Parse(captured);
            doc.RootElement.GetProperty("policy_name").GetString().Should().Be("test-policy");
        };

        parse.Should().NotThrow();
    }

    [Fact]
    public void Emit_UsesSnakeCaseFieldNames()
    {
        var sink = new ConsoleLogSink();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.RetryAttempted,
            PolicyName = "p",
            CorrelationId = "c",
            Attempt = 2
        };

        var captured = CaptureConsoleOutput(() => sink.Emit(evt)).Trim();

        captured.Should().Contain("\"event_type\"");
        captured.Should().Contain("\"policy_name\"");
        captured.Should().Contain("\"correlation_id\"");
        captured.Should().NotContain("\"EventType\"");
        captured.Should().NotContain("\"PolicyName\"");
    }

    [Fact]
    public void Emit_EscapesSpecialCharactersInStrings()
    {
        var sink = new ConsoleLogSink();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallFailed,
            PolicyName = "policy-with-\"quotes\"",
            ErrorMessage = "Line1\nLine2\tTabbed"
        };

        var captured = CaptureConsoleOutput(() => sink.Emit(evt)).Trim();

        Action parse = () => JsonDocument.Parse(captured);
        parse.Should().NotThrow();
    }
    [Fact]
    public void Emit_OmitsNullFields()
    {
        var sink = new ConsoleLogSink();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallStarted,
            PolicyName = "p"
        };

        var captured = CaptureConsoleOutput(() => sink.Emit(evt)).Trim();

        captured.Should().NotContain("\"correlation_id\"");
        captured.Should().NotContain("\"error_message\"");
        captured.Should().NotContain("\"duration_ms\"");
    }

    [Fact]
    public void Emit_IsThreadSafe_NoInterleavedLines()
    {
        var sink = new ConsoleLogSink();
        var lineCount = 200;

        var captured = CaptureConsoleOutput(() =>
        {
            Parallel.For(0, lineCount, i =>
            {
                sink.Emit(new ResilienceEvent
                {
                    EventType = ResilienceEventType.CallSucceeded,
                    PolicyName = $"policy-{i}"
                });
            });
        });

        var lines = captured.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(lineCount);

        foreach (var line in lines)
        {
            Action parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow();
        }
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return writer.ToString();
    }
}
