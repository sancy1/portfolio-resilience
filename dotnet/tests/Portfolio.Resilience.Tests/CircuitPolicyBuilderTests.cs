// filepath: tests/Portfolio.Resilience.Tests/CircuitPolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies circuit breaker state transitions, threshold counting, and half-open probing.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : CircuitPolicyBuilder (Policies/CircuitPolicyBuilder.cs)
//   Depends on : CircuitOptions, ErrorClassifier, xUnit, FluentAssertions
//   See also   : docs/circuit-breaker.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CircuitPolicyBuilderTests
{
    private static readonly CircuitOptions FastCircuit = new()
    {
        FailureThreshold = 3,
        OpenDurationSeconds = 30,
        SuccessThreshold = 1,
        OnlyCountTransient = true
    };

    private static readonly CircuitOptions NoPermanentCounting = new()
    {
        FailureThreshold = 3,
        OpenDurationSeconds = 30,
        SuccessThreshold = 1,
        OnlyCountTransient = false
    };

    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan span) => Now = Now.Add(span);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var cb = new CircuitPolicyBuilder();
        Func<Task> act = () => cb.ExecuteAsync<int>(null!, _ => Task.FromResult(1), FastCircuit);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_SuccessInClosed_StaysClosed()
    {
        var cb = new CircuitPolicyBuilder();

        var result = await cb.ExecuteAsync("p", _ => Task.FromResult(42), FastCircuit);

        result.Should().Be(42);
        var snap = cb.Get("p")!;
        snap.State.Should().Be(CircuitState.Closed);
        snap.ConsecutiveFailures.Should().Be(0);
    }
    [Fact]
    public async Task ExecuteAsync_TransientFailuresAtThreshold_OpensCircuit()
    {
        var cb = new CircuitPolicyBuilder();

        // 3 transient failures open the circuit (threshold = 3)
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new TimeoutException("transient"), FastCircuit));
        }

        var snap = cb.Get("p")!;
        snap.State.Should().Be(CircuitState.Open);
        snap.ConsecutiveFailures.Should().Be(3);
        snap.OpenedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_CircuitOpen_RejectsWithResilienceException()
    {
        var cb = new CircuitPolicyBuilder();

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new TimeoutException(), FastCircuit));
        }

        // Next call is rejected — the operation is never invoked.
        var opCalled = false;
        Func<Task> act = () => cb.ExecuteAsync("p",
            _ => { opCalled = true; return Task.FromResult(1); },
            FastCircuit);

        var ex = await act.Should().ThrowAsync<ResilienceException>();
        ex.Which.Category.Should().Be(ResilienceErrorCategory.CircuitOpen);
        opCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_PermanentFailures_DoNotCountByDefault()
    {
        var cb = new CircuitPolicyBuilder();

        // 5 permanent failures with OnlyCountTransient=true → circuit stays Closed
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new ArgumentException("permanent"), FastCircuit));
        }

        var snap = cb.Get("p")!;
        snap.State.Should().Be(CircuitState.Closed);
        snap.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_PermanentFailures_CountWhenConfigured()
    {
        var cb = new CircuitPolicyBuilder();

        // 3 permanent failures with OnlyCountTransient=false → circuit opens
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new ArgumentException("permanent"), NoPermanentCounting));
        }

        var snap = cb.Get("p")!;
        snap.State.Should().Be(CircuitState.Open);
    }
    [Fact]
    public async Task ExecuteAsync_AfterOpenDuration_MovesToHalfOpen()
    {
        var clock = new FakeClock();
        var cb = new CircuitPolicyBuilder(clock: () => clock.Now);

        // Open the circuit
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new TimeoutException(), FastCircuit));
        }
        cb.Get("p")!.State.Should().Be(CircuitState.Open);

        // Advance past the open duration
        clock.Advance(TimeSpan.FromSeconds(31));

        // Next call enters HalfOpen, succeeds → circuit closes
        var result = await cb.ExecuteAsync("p", _ => Task.FromResult(99), FastCircuit);
        result.Should().Be(99);

        var snap = cb.Get("p")!;
        snap.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task ExecuteAsync_HalfOpenProbeFails_Reopens()
    {
        var clock = new FakeClock();
        var cb = new CircuitPolicyBuilder(clock: () => clock.Now);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new TimeoutException(), FastCircuit));
        }

        clock.Advance(TimeSpan.FromSeconds(31));

        // HalfOpen probe fails
        await Assert.ThrowsAsync<TimeoutException>(() =>
            cb.ExecuteAsync<int>("p", _ => throw new TimeoutException(), FastCircuit));

        var snap = cb.Get("p")!;
        snap.State.Should().Be(CircuitState.Open);
        snap.OpenedAtUtc.Should().Be(clock.Now);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentPolicies_HaveIndependentCircuits()
    {
        var cb = new CircuitPolicyBuilder();

        // Open policy-a
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>("policy-a", _ => throw new TimeoutException(), FastCircuit));
        }

        // policy-b is unaffected
        var result = await cb.ExecuteAsync("policy-b", _ => Task.FromResult(7), FastCircuit);
        result.Should().Be(7);

        cb.Get("policy-a")!.State.Should().Be(CircuitState.Open);
        cb.Get("policy-b")!.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Snapshot_ReturnsAllKnownCircuits()
    {
        var cb = new CircuitPolicyBuilder();

        // Touch two policies (via ExecuteAsync)
        _ = await cb.ExecuteAsync("a", _ => Task.FromResult(1), FastCircuit);
        _ = await cb.ExecuteAsync("b", _ => Task.FromResult(1), FastCircuit);

        var all = cb.Snapshot();
        all.Should().HaveCount(2);
        all.Select(s => s.PolicyName).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task ExecuteAsync_SuccessResetsFailureCounter()
    {
        var cb = new CircuitPolicyBuilder();

        // 2 failures (below threshold of 3)
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cb.ExecuteAsync<int>("p", _ => throw new TimeoutException(), FastCircuit));
        }

        // Success resets
        _ = await cb.ExecuteAsync("p", _ => Task.FromResult(1), FastCircuit);

        var snap = cb.Get("p")!;
        snap.ConsecutiveFailures.Should().Be(0);
    }
}
