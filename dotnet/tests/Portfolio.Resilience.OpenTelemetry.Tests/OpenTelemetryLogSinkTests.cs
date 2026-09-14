// filepath: tests/Portfolio.Resilience.OpenTelemetry.Tests/OpenTelemetryLogSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.OpenTelemetry.Tests | since: v0.7.0
// purpose: Verifies the OTel log sink maps ResilienceEvents to the expected log records.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : OpenTelemetryLogSink (OpenTelemetry/)
//   Depends on : ILoggerFactory, ResilienceEvent, ResilienceEventType, xUnit, FluentAssertions
//   See also   : docs/opentelemetry.md, SPEC.md section 7.1
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.OpenTelemetry;
using Xunit;

namespace Portfolio.Resilience.OpenTelemetry.Tests;

public sealed class OpenTelemetryLogSinkTests
{
    // ------------------------------------------------------------------------
    // Test double: captures log records from ILoggerFactory
    // ------------------------------------------------------------------------

    private sealed class CapturedRecord
    {
        public LogLevel Level { get; init; }
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<KeyValuePair<string, object?>> Attributes { get; init; }
            = Array.Empty<KeyValuePair<string, object?>>();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<CapturedRecord> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _provider;

            public CapturingLogger(CapturingLoggerProvider provider) => _provider = provider;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var pairs = state is IEnumerable<KeyValuePair<string, object?>> kvps
                    ? kvps.ToArray()
                    : Array.Empty<KeyValuePair<string, object?>>();

                _provider.Records.Add(new CapturedRecord
                {
                    Level = logLevel,
                    Message = formatter(state, exception),
                    Attributes = pairs
                });
            }
        }
    }

    private static (OpenTelemetryLogSink sink, CapturingLoggerProvider capture) Build()
    {
        var capture = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });
        return (new OpenTelemetryLogSink(factory), capture);
    }

    private static object? Attr(CapturedRecord record, string key)
        => record.Attributes.FirstOrDefault(kv => kv.Key == key).Value;

    // ------------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsOnNullLoggerFactory()
    {
        Action act = () => new OpenTelemetryLogSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Emit_ThrowsOnNullEvent()
    {
        var (sink, _) = Build();
        Action act = () => sink.Emit(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------------
    // Field mapping
    // ------------------------------------------------------------------------

    [Fact]
    public void Emit_MapsCommonFields()
    {
        var (sink, capture) = Build();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallSucceeded,
            PolicyName = "auth-service",
            CorrelationId = "abc-123",
            Attempt = 2,
            DurationMs = 42.5,
            TimestampUtc = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc)
        };

        sink.Emit(evt);

        capture.Records.Should().HaveCount(1);
        var record = capture.Records[0];
        Attr(record, "resilience.event_type").Should().Be("call_succeeded");
        Attr(record, "resilience.policy_name").Should().Be("auth-service");
        Attr(record, "resilience.correlation_id").Should().Be("abc-123");
        Attr(record, "resilience.attempt").Should().Be(2);
        Attr(record, "resilience.duration_ms").Should().Be(42.5);
    }

    [Fact]
    public void Emit_MapsErrorFields()
    {
        var (sink, capture) = Build();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallFailed,
            PolicyName = "p",
            ErrorCategory = ResilienceErrorCategory.Transient,
            ErrorMessage = "boom",
            ErrorType = "System.TimeoutException"
        };

        sink.Emit(evt);

        var record = capture.Records.Single();
        Attr(record, "resilience.error_category").Should().Be("Transient");
        Attr(record, "resilience.error_message").Should().Be("boom");
        Attr(record, "resilience.error_type").Should().Be("System.TimeoutException");
    }

    [Fact]
    public void Emit_FlattensMetadata()
    {
        var (sink, capture) = Build();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.RateLimited,
            PolicyName = "p",
            Metadata = new Dictionary<string, object?>
            {
                ["strategy"] = "SlidingWindow",
                ["permit_limit"] = 100,
                ["queue_depth"] = 5
            }
        };

        sink.Emit(evt);

        var record = capture.Records.Single();
        Attr(record, "resilience.metadata.strategy").Should().Be("SlidingWindow");
        Attr(record, "resilience.metadata.permit_limit").Should().Be(100);
        Attr(record, "resilience.metadata.queue_depth").Should().Be(5);
    }

    [Fact]
    public void Emit_OmitsAbsentOptionalFields()
    {
        var (sink, capture) = Build();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallStarted,
            PolicyName = "p"
            // no CorrelationId, no Attempt, no Duration, no Error
        };

        sink.Emit(evt);

        var record = capture.Records.Single();
        record.Attributes.Any(kv => kv.Key == "resilience.correlation_id").Should().BeFalse();
        record.Attributes.Any(kv => kv.Key == "resilience.attempt").Should().BeFalse();
        record.Attributes.Any(kv => kv.Key == "resilience.duration_ms").Should().BeFalse();
        record.Attributes.Any(kv => kv.Key == "resilience.error_category").Should().BeFalse();
    }

    // ------------------------------------------------------------------------
    // Severity mapping (all 11 event types)
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData(ResilienceEventType.CallStarted,       LogLevel.Debug)]
    [InlineData(ResilienceEventType.CallSucceeded,     LogLevel.Information)]
    [InlineData(ResilienceEventType.CallFailed,        LogLevel.Error)]
    [InlineData(ResilienceEventType.RetryAttempted,    LogLevel.Warning)]
    [InlineData(ResilienceEventType.CircuitOpened,     LogLevel.Warning)]
    [InlineData(ResilienceEventType.CircuitClosed,     LogLevel.Information)]
    [InlineData(ResilienceEventType.CircuitHalfOpened, LogLevel.Information)]
    [InlineData(ResilienceEventType.FallbackUsed,      LogLevel.Information)]
    [InlineData(ResilienceEventType.TimeoutBreached,   LogLevel.Warning)]
    [InlineData(ResilienceEventType.RateLimited,       LogLevel.Warning)]
    [InlineData(ResilienceEventType.BulkheadRejected,  LogLevel.Warning)]
    public void Emit_MapsEventTypeToLogLevel(ResilienceEventType type, LogLevel expected)
    {
        var (sink, capture) = Build();
        sink.Emit(new ResilienceEvent { EventType = type, PolicyName = "p" });

        capture.Records.Single().Level.Should().Be(expected);
    }

    // ------------------------------------------------------------------------
    // Event name mapping (snake_case per SPEC 7.1)
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData(ResilienceEventType.CallStarted,       "call_started")]
    [InlineData(ResilienceEventType.CallSucceeded,     "call_succeeded")]
    [InlineData(ResilienceEventType.CallFailed,        "call_failed")]
    [InlineData(ResilienceEventType.RetryAttempted,    "retry_attempted")]
    [InlineData(ResilienceEventType.CircuitOpened,     "circuit_opened")]
    [InlineData(ResilienceEventType.CircuitClosed,     "circuit_closed")]
    [InlineData(ResilienceEventType.CircuitHalfOpened, "circuit_half_opened")]
    [InlineData(ResilienceEventType.FallbackUsed,      "fallback_used")]
    [InlineData(ResilienceEventType.TimeoutBreached,   "timeout_breached")]
    [InlineData(ResilienceEventType.RateLimited,       "rate_limited")]
    [InlineData(ResilienceEventType.BulkheadRejected,  "bulkhead_rejected")]
    public void Emit_MapsEventTypeToSnakeCaseName(ResilienceEventType type, string expected)
    {
        var (sink, capture) = Build();
        sink.Emit(new ResilienceEvent { EventType = type, PolicyName = "p" });

        Attr(capture.Records.Single(), "resilience.event_type").Should().Be(expected);
    }

    // ------------------------------------------------------------------------
    // Multiple events
    // ------------------------------------------------------------------------

    [Fact]
    public void Emit_MultipleEvents_EachProducesOneRecord()
    {
        var (sink, capture) = Build();

        sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallStarted, PolicyName = "p" });
        sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallSucceeded, PolicyName = "p" });
        sink.Emit(new ResilienceEvent { EventType = ResilienceEventType.CallFailed, PolicyName = "p" });

        capture.Records.Should().HaveCount(3);
        capture.Records.Select(r => Attr(r, "resilience.event_type"))
            .Should().Equal("call_started", "call_succeeded", "call_failed");
    }
}
