// filepath: tests/Portfolio.Resilience.Tests/LatencyTrackerTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies LatencyTracker delegates correctly to the underlying InMemoryMetricSink.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : LatencyTracker (Implementation/LatencyTracker.cs)
//   Depends on : InMemoryMetricSink, LatencySnapshot, xUnit, FluentAssertions
//   See also   : docs/metrics.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class LatencyTrackerTests
{
    [Fact]
    public void Constructor_WithNoSink_CreatesAnEmptyInMemorySink()
    {
        var tracker = new LatencyTracker();

        tracker.Snapshot().Should().BeEmpty();
        tracker.Get("unknown").Should().BeNull();
    }

    [Fact]
    public void Snapshot_ReturnsEmptyForFreshSink()
    {
        var sink = new InMemoryMetricSink();
        var tracker = new LatencyTracker(sink);

        tracker.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Get_ThrowsOnNullOrWhitespacePolicyName()
    {
        var tracker = new LatencyTracker();

        Action actNull = () => tracker.Get(null!);
        Action actEmpty = () => tracker.Get("");
        Action actWhitespace = () => tracker.Get("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Get_ReturnsNullForUnknownPolicy()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("known", TimeSpan.FromMilliseconds(10), success: true, attempts: 1);
        var tracker = new LatencyTracker(sink);

        tracker.Get("unknown").Should().BeNull();
    }

    [Fact]
    public void Get_ReturnsSnapshot_ForRecordedPolicy()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("test-policy", TimeSpan.FromMilliseconds(42), success: true, attempts: 1);
        var tracker = new LatencyTracker(sink);

        var snap = tracker.Get("test-policy");

        snap.Should().NotBeNull();
        snap!.PolicyName.Should().Be("test-policy");
        snap.TotalCalls.Should().Be(1);
        snap.P50Ms.Should().BeApproximately(42.0, 0.01);
    }

    [Fact]
    public void Snapshot_ReflectsEveryRecordedPolicy()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("a", TimeSpan.FromMilliseconds(10), true, 1);
        sink.RecordCall("b", TimeSpan.FromMilliseconds(20), true, 1);
        sink.RecordCall("c", TimeSpan.FromMilliseconds(30), true, 1);
        var tracker = new LatencyTracker(sink);

        var all = tracker.Snapshot();

        all.Should().HaveCount(3);
        all.Select(s => s.PolicyName).Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var sink = new InMemoryMetricSink();
        sink.RecordCall("Auth-Service", TimeSpan.FromMilliseconds(50), true, 1);
        var tracker = new LatencyTracker(sink);

        tracker.Get("auth-service").Should().NotBeNull();
        tracker.Get("AUTH-SERVICE").Should().NotBeNull();
    }

    [Fact]
    public void Tracker_ObservesNewSamples_RecordedAfterConstruction()
    {
        var sink = new InMemoryMetricSink();
        var tracker = new LatencyTracker(sink);

        // Fresh — no data yet
        tracker.Snapshot().Should().BeEmpty();

        // Record through the sink; the tracker must see it (shared instance)
        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), true, 1);

        tracker.Snapshot().Should().HaveCount(1);
    }
}
