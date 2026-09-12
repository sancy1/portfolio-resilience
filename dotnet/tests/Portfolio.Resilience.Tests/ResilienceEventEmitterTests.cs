// filepath: tests/Portfolio.Resilience.Tests/ResilienceEventEmitterTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies ResilienceEventEmitter builds correctly-shaped events and attaches correlation IDs.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ResilienceEventEmitter (Implementation/ResilienceEventEmitter.cs)
//   Depends on : ILogSink, ResilienceEvent, CorrelationContext, xUnit, FluentAssertions
//   See also   : docs/logging.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Implementation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilienceEventEmitterTests
{
    private sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

    [Fact]
    public void Constructor_UsesNullSinkWhenSinkNotProvided()
    {
        // No exception when no sink is supplied; internally defaults to NullLogSink.
        var emitter = new ResilienceEventEmitter();
        Action act = () => emitter.EmitCallStarted("p");
        act.Should().NotThrow();
    }

    [Fact]
    public void Emit_ThrowsOnNullEvent()
    {
        var emitter = new ResilienceEventEmitter(new CapturingSink());
        Action act = () => emitter.Emit(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EmitCallStarted_ThrowsOnNullOrWhitespacePolicyName()
    {
        var emitter = new ResilienceEventEmitter(new CapturingSink());
        Action actNull = () => emitter.EmitCallStarted(null!);
        Action actEmpty = () => emitter.EmitCallStarted("");
        Action actWhitespace = () => emitter.EmitCallStarted("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void EmitCallStarted_ProducesCorrectlyShapedEvent()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        emitter.EmitCallStarted("auth-service");

        sink.Events.Should().HaveCount(1);
        var evt = sink.Events[0];
        evt.EventType.Should().Be(ResilienceEventType.CallStarted);
        evt.PolicyName.Should().Be("auth-service");
        evt.TimestampUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void EmitCallSucceeded_CarriesDurationAndAttempts()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        emitter.EmitCallSucceeded("p", durationMs: 42.5, attempts: 3);

        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.CallSucceeded);
        evt.DurationMs.Should().Be(42.5);
        evt.Attempt.Should().Be(3);
    }

    [Fact]
    public void EmitCallFailed_CarriesErrorDetails()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        var ex = new InvalidOperationException("boom");
        emitter.EmitCallFailed("p", ex, attempts: 2, totalDuration: TimeSpan.FromMilliseconds(150));

        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.CallFailed);
        evt.ErrorMessage.Should().Be("boom");
        evt.ErrorType.Should().Be(typeof(InvalidOperationException).FullName);
        evt.Attempt.Should().Be(2);
        evt.DurationMs.Should().Be(150);
    }

    [Fact]
    public void EmitCallFailed_ExtractsCategoryFromResilienceException()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        var rex = new ResilienceException(
            message: "timeout",
            policyName: "p",
            category: ResilienceErrorCategory.Timeout,
            attemptsMade: 3,
            totalDuration: TimeSpan.Zero);

        emitter.EmitCallFailed("p", rex, attempts: 3, totalDuration: TimeSpan.Zero);

        sink.Events.Single().ErrorCategory.Should().Be(ResilienceErrorCategory.Timeout);
    }
    [Fact]
    public void EmitCallStarted_AttachesCurrentCorrelationId()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        using (CorrelationContext.Push("test-correlation-xyz"))
        {
            emitter.EmitCallStarted("p");
        }

        sink.Events.Single().CorrelationId.Should().Be("test-correlation-xyz");
    }

    [Fact]
    public void EmitRetryAttempted_CarriesAttemptAndError()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        var ex = new TimeoutException("slow");
        emitter.EmitRetryAttempted("p", attempt: 2, error: ex, delayMs: 500);

        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.RetryAttempted);
        evt.Attempt.Should().Be(2);
        evt.ErrorType.Should().Contain("TimeoutException");
        evt.Metadata.Should().ContainKey("delay_ms");
    }

    [Fact]
    public void EmitCircuitOpened_CarriesFailureCountAndTimestamp()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);
        var openedAt = DateTime.UtcNow;

        emitter.EmitCircuitOpened("p", failures: 5, openedAtUtc: openedAt);

        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.CircuitOpened);
        evt.Metadata.Should().ContainKey("consecutive_failures");
        evt.Metadata["consecutive_failures"].Should().Be(5);
    }

    [Fact]
    public void EmitCircuitClosed_And_HalfOpened_HaveCorrectTypes()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        emitter.EmitCircuitClosed("p");
        emitter.EmitCircuitHalfOpened("p");

        sink.Events.Should().HaveCount(2);
        sink.Events[0].EventType.Should().Be(ResilienceEventType.CircuitClosed);
        sink.Events[1].EventType.Should().Be(ResilienceEventType.CircuitHalfOpened);
    }

    [Fact]
    public void EmitFallbackUsed_CarriesReasonWhenProvided()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        emitter.EmitFallbackUsed("p", reason: "circuit open");

        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.FallbackUsed);
        evt.Metadata["reason"].Should().Be("circuit open");
    }

    [Fact]
    public void EmitTimeoutBreached_CarriesTimeoutAndElapsed()
    {
        var sink = new CapturingSink();
        var emitter = new ResilienceEventEmitter(sink);

        emitter.EmitTimeoutBreached("p", timeoutMs: 5000, elapsedMs: 5200);

        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.TimeoutBreached);
        evt.Metadata["timeout_ms"].Should().Be(5000);
        evt.Metadata["elapsed_ms"].Should().Be(5200.0);
    }
}
