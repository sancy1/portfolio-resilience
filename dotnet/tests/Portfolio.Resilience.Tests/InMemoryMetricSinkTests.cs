// filepath: tests/Portfolio.Resilience.Tests/InMemoryMetricSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies InMemoryMetricSink percentile math, error rate, window bounds, and thread safety.
// RELATIONSHIPS
//   Tests      : InMemoryMetricSink (Sinks/InMemoryMetricSink.cs)
//   Depends on : LatencySnapshot (Abstractions/ILatencyTracker.cs), xUnit, FluentAssertions
//   See also   : docs/metrics.md

using FluentAssertions;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class InMemoryMetricSinkTests
{
    [Fact]
    public void Constructor_ThrowsOnNonPositiveWindowSize()
    {
        Action actZero = () => new InMemoryMetricSink(0);
        Action actNegative = () => new InMemoryMetricSink(-1);

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RecordCall_ThrowsOnNullOrWhitespacePolicyName()
    {
        var sink = new InMemoryMetricSink();

        Action actNull = () => sink.RecordCall(null!, TimeSpan.FromMilliseconds(1), true, 1);
        Action actEmpty = () => sink.RecordCall("", TimeSpan.FromMilliseconds(1), true, 1);
        Action actWhitespace = () => sink.RecordCall("   ", TimeSpan.FromMilliseconds(1), true, 1);

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Get_ReturnsNullForUnknownPolicy()
    {
        var sink = new InMemoryMetricSink();
        sink.Get("never-recorded").Should().BeNull();
    }

    [Fact]
    public void Get_ThrowsOnNullOrWhitespacePolicyName()
    {
        var sink = new InMemoryMetricSink();

        Action actNull = () => sink.Get(null!);
        Action actEmpty = () => sink.Get("");
        Action actWhitespace = () => sink.Get("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void Snapshot_ReflectsCallCounts()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), success: true, attempts: 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(20), success: true, attempts: 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(30), success: false, attempts: 3);

        var snap = sink.Get("p")!;

        snap.TotalCalls.Should().Be(3);
        snap.FailedCalls.Should().Be(1);
    }

    [Fact]
    public void Snapshot_ComputesErrorRate()
    {
        var sink = new InMemoryMetricSink();
        for (var i = 0; i < 8; i++)
            sink.RecordCall("p", TimeSpan.FromMilliseconds(10), success: true, attempts: 1);
        for (var i = 0; i < 2; i++)
            sink.RecordCall("p", TimeSpan.FromMilliseconds(10), success: false, attempts: 1);

        var snap = sink.Get("p")!;
        snap.ErrorRate.Should().BeApproximately(0.2, 0.0001);
    }

    [Fact]
    public void Snapshot_ErrorRateIsZeroWhenNoCalls()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), success: true, attempts: 1);
        var snap = sink.Get("p")!;
        snap.ErrorRate.Should().Be(0.0);
    }

    [Fact]
    public void Snapshot_EmptyPolicy_ReturnsNullFromGet()
    {
        var sink = new InMemoryMetricSink();
        sink.Get("empty").Should().BeNull();
    }
    [Fact]
    public void Snapshot_P50_WithOddCount_ReturnsMiddle()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(20), true, 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(30), true, 1);

        var snap = sink.Get("p")!;
        snap.P50Ms.Should().BeApproximately(20.0, 0.0001);
    }

    [Fact]
    public void Snapshot_P50_WithEvenCount_Interpolates()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(20), true, 1);

        var snap = sink.Get("p")!;
        snap.P50Ms.Should().BeApproximately(15.0, 0.0001);
    }

    [Fact]
    public void Snapshot_P95_IsNearTopOfRange()
    {
        var sink = new InMemoryMetricSink();
        for (var i = 1; i <= 100; i++)
            sink.RecordCall("p", TimeSpan.FromMilliseconds(i), true, 1);

        var snap = sink.Get("p")!;
        snap.P95Ms.Should().BeApproximately(95.05, 0.01);
    }

    [Fact]
    public void Snapshot_P99_IsNearTopOfRange()
    {
        var sink = new InMemoryMetricSink();
        for (var i = 1; i <= 100; i++)
            sink.RecordCall("p", TimeSpan.FromMilliseconds(i), true, 1);

        var snap = sink.Get("p")!;
        snap.P99Ms.Should().BeApproximately(99.01, 0.01);
    }

    [Fact]
    public void Snapshot_Average_IsArithmeticMean()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(20), true, 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(30), true, 1);

        var snap = sink.Get("p")!;
        snap.AvgMs.Should().BeApproximately(20.0, 0.0001);
    }
    [Fact]
    public void Snapshot_WindowIsBounded_DiscardsOldSamples()
    {
        var sink = new InMemoryMetricSink(windowSize: 10);
        for (var i = 0; i < 100; i++)
            sink.RecordCall("p", TimeSpan.FromMilliseconds(i + 1), true, 1);

        var snap = sink.Get("p")!;

        snap.TotalCalls.Should().Be(100);
        snap.P50Ms.Should().BeApproximately(95.5, 0.5);
    }

    [Fact]
    public void Snapshot_TracksMultiplePoliciesIndependently()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("policy-a", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("policy-a", TimeSpan.FromMilliseconds(20), true, 1);
        sink.RecordCall("policy-b", TimeSpan.FromMilliseconds(100), true, 1);
        sink.RecordCall("policy-b", TimeSpan.FromMilliseconds(200), false, 1);
        sink.RecordCall("policy-b", TimeSpan.FromMilliseconds(300), true, 1);

        var snapA = sink.Get("policy-a")!;
        var snapB = sink.Get("policy-b")!;

        snapA.TotalCalls.Should().Be(2);
        snapA.FailedCalls.Should().Be(0);
        snapB.TotalCalls.Should().Be(3);
        snapB.FailedCalls.Should().Be(1);
    }

    [Fact]
    public void Snapshot_IsCaseInsensitiveOnPolicyName()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("Auth-Service", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("auth-service", TimeSpan.FromMilliseconds(20), true, 1);

        var snap = sink.Get("AUTH-SERVICE")!;
        snap.TotalCalls.Should().Be(2);
    }

    [Fact]
    public void Snapshot_ReturnsAllRecordedPolicies()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("a", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("b", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("c", TimeSpan.FromMilliseconds(10), true, 1);

        var all = sink.Snapshot();
        all.Should().HaveCount(3);
    }

    [Fact]
    public void RecordCall_IsThreadSafe()
    {
        var sink = new InMemoryMetricSink();
        var count = 1000;

        Parallel.For(0, count, i =>
        {
            sink.RecordCall("p", TimeSpan.FromMilliseconds(10), true, 1);
        });

        var snap = sink.Get("p")!;
        snap.TotalCalls.Should().Be(count);
    }
    [Fact]
    public void Snapshot_InFlightIsZeroToday()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), true, 1);

        var snap = sink.Get("p")!;
        snap.InFlight.Should().Be(0);
    }
}
