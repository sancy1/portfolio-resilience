// filepath: tests/Portfolio.Resilience.Tests/NullLogSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies NullLogSink silently accepts every event without throwing or producing side effects.
// RELATIONSHIPS
//   Tests      : NullLogSink (Sinks/NullLogSink.cs)
//   Depends on : xUnit, FluentAssertions
//   See also   : docs/logging.md

using FluentAssertions;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class NullLogSinkTests
{
    [Fact]
    public void Instance_IsSingleton()
    {
        NullLogSink.Instance.Should().NotBeNull();
        NullLogSink.Instance.Should().BeSameAs(NullLogSink.Instance);
    }

    [Fact]
    public void Emit_DoesNotThrow_ForAnyEvent()
    {
        var sink = new NullLogSink();
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallSucceeded,
            PolicyName = "test-policy",
            CorrelationId = "abc-123",
            DurationMs = 42.5
        };

        Action act = () => sink.Emit(evt);
        act.Should().NotThrow();
    }

    [Fact]
    public void Emit_SilentlyAcceptsNull()
    {
        // NullLogSink is deliberately permissive — its whole purpose is to be a no-op.
        var sink = new NullLogSink();
        Action act = () => sink.Emit(null!);
        act.Should().NotThrow();
    }

    [Fact]
    public void Emit_CanBeCalledManyTimesWithoutSideEffects()
    {
        var sink = NullLogSink.Instance;
        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.RetryAttempted,
            PolicyName = "test-policy",
            Attempt = 2
        };

        Action act = () =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                sink.Emit(evt);
            }
        };

        act.Should().NotThrow();
    }
}
