// filepath: tests/Portfolio.Resilience.Tests/CompositeMetricSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies CompositeMetricSink fan-out, per-sink exception isolation, and null filtering.
// RELATIONSHIPS
//   Tests      : CompositeMetricSink (Sinks/CompositeMetricSink.cs)
//   Depends on : IMetricSink, xUnit, FluentAssertions
//   See also   : docs/metrics.md

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CompositeMetricSinkTests
{
    [Fact]
    public void Constructor_ThrowsOnNullSinks()
    {
        Action act = () => new CompositeMetricSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_FiltersNullSinks()
    {
        var sink1 = new CapturingMetricSink();
        var sink2 = new CapturingMetricSink();

        var composite = new CompositeMetricSink(new IMetricSink[] { sink1, null!, sink2 });

        composite.Count.Should().Be(2);
    }

    [Fact]
    public void Count_ReturnsNumberOfChildSinks()
    {
        var composite = new CompositeMetricSink(new IMetricSink[]
        {
            new CapturingMetricSink(),
            new CapturingMetricSink(),
            new CapturingMetricSink()
        });

        composite.Count.Should().Be(3);
    }
    [Fact]
    public void RecordCall_ThrowsOnNullOrWhitespacePolicyName()
    {
        var composite = new CompositeMetricSink(new[] { new CapturingMetricSink() });

        Action actNull = () => composite.RecordCall(null!, TimeSpan.FromMilliseconds(1), true, 1);
        Action actEmpty = () => composite.RecordCall("", TimeSpan.FromMilliseconds(1), true, 1);
        Action actWhitespace = () => composite.RecordCall("   ", TimeSpan.FromMilliseconds(1), true, 1);

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordCall_ForwardsToAllChildSinks()
    {
        var sink1 = new CapturingMetricSink();
        var sink2 = new CapturingMetricSink();

        var composite = new CompositeMetricSink(new IMetricSink[] { sink1, sink2 });

        composite.RecordCall("test-policy", TimeSpan.FromMilliseconds(42), success: true, attempts: 1);

        sink1.Records.Should().HaveCount(1);
        sink2.Records.Should().HaveCount(1);

        sink1.Records[0].PolicyName.Should().Be("test-policy");
        sink1.Records[0].Duration.TotalMilliseconds.Should().Be(42);
        sink1.Records[0].Success.Should().BeTrue();
        sink1.Records[0].Attempts.Should().Be(1);
    }

    [Fact]
    public void RecordCall_ContinuesWhenChildSinkThrows()
    {
        var healthy = new CapturingMetricSink();
        var failing = new ThrowingMetricSink();
        var composite = new CompositeMetricSink(new IMetricSink[] { failing, healthy });

        Action act = () => composite.RecordCall("p", TimeSpan.FromMilliseconds(1), true, 1);

        act.Should().NotThrow();
        healthy.Records.Should().HaveCount(1);
    }

    [Fact]
    public void RecordCall_WithEmptySinks_DoesNotThrow()
    {
        var composite = new CompositeMetricSink(Array.Empty<IMetricSink>());

        Action act = () => composite.RecordCall("p", TimeSpan.FromMilliseconds(1), true, 1);

        act.Should().NotThrow();
    }
    // -- Test doubles --------------------------------------------------------

    private sealed class CapturingMetricSink : IMetricSink
    {
        public List<Record> Records { get; } = new();

        public void RecordCall(string policyName, TimeSpan duration, bool success, int attempts)
            => Records.Add(new Record(policyName, duration, success, attempts));

        public sealed record Record(string PolicyName, TimeSpan Duration, bool Success, int Attempts);
    }

    private sealed class ThrowingMetricSink : IMetricSink
    {
        public void RecordCall(string policyName, TimeSpan duration, bool success, int attempts)
            => throw new InvalidOperationException("simulated metric sink failure");
    }
}
