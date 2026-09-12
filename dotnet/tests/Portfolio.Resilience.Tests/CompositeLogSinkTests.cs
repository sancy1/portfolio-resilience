// filepath: tests/Portfolio.Resilience.Tests/CompositeLogSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies CompositeLogSink fan-out, per-sink exception isolation, and null filtering.
// RELATIONSHIPS
//   Tests      : CompositeLogSink (Sinks/CompositeLogSink.cs)
//   Depends on : ILogSink, ResilienceEvent, xUnit, FluentAssertions
//   See also   : docs/logging.md

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CompositeLogSinkTests
{
    [Fact]
    public void Constructor_ThrowsOnNullSinks()
    {
        Action act = () => new CompositeLogSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_FiltersNullSinks()
    {
        var sink1 = new CapturingSink();
        var sink2 = new CapturingSink();

        var composite = new CompositeLogSink(new ILogSink[] { sink1, null!, sink2 });

        composite.Count.Should().Be(2);
    }

    [Fact]
    public void Count_ReturnsNumberOfChildSinks()
    {
        var composite = new CompositeLogSink(new ILogSink[]
        {
            new CapturingSink(),
            new CapturingSink(),
            new CapturingSink()
        });

        composite.Count.Should().Be(3);
    }

    [Fact]
    public void Emit_ThrowsOnNullEvent()
    {
        var composite = new CompositeLogSink(new[] { new CapturingSink() });
        Action act = () => composite.Emit(null!);
        act.Should().Throw<ArgumentNullException>();
    }
    [Fact]
    public void Emit_ForwardsToAllChildSinks()
    {
        var sink1 = new CapturingSink();
        var sink2 = new CapturingSink();
        var sink3 = new CapturingSink();

        var composite = new CompositeLogSink(new ILogSink[] { sink1, sink2, sink3 });

        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallSucceeded,
            PolicyName = "test"
        };

        composite.Emit(evt);

        sink1.Events.Should().HaveCount(1);
        sink2.Events.Should().HaveCount(1);
        sink3.Events.Should().HaveCount(1);
        sink1.Events[0].Should().BeSameAs(evt);
    }

    [Fact]
    public void Emit_ContinuesWhenChildSinkThrows()
    {
        var healthy = new CapturingSink();
        var failing = new ThrowingSink();
        var composite = new CompositeLogSink(new ILogSink[] { failing, healthy });

        var evt = new ResilienceEvent
        {
            EventType = ResilienceEventType.CallFailed,
            PolicyName = "test"
        };

        Action act = () => composite.Emit(evt);

        act.Should().NotThrow();
        healthy.Events.Should().HaveCount(1);
    }

    [Fact]
    public void Emit_WithEmptySinks_DoesNotThrow()
    {
        var composite = new CompositeLogSink(Array.Empty<ILogSink>());

        Action act = () => composite.Emit(new ResilienceEvent
        {
            EventType = ResilienceEventType.CallStarted,
            PolicyName = "test"
        });

        act.Should().NotThrow();
    }
    // -- Test doubles --------------------------------------------------------

    private sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Emit(ResilienceEvent evt)
            => throw new InvalidOperationException("simulated sink failure");
    }
}
