// filepath: tests/Portfolio.Resilience.Tests/CompositePolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies CompositePolicyBuilder chains retry + circuit + timeout in the correct order.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : CompositePolicyBuilder (Policies/CompositePolicyBuilder.cs)
//   Depends on : RetryPolicyBuilder, CircuitPolicyBuilder, TimeoutPolicyBuilder, xUnit, FluentAssertions
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CompositePolicyBuilderTests
{
    // A definition that is fast enough for unit tests, but exercises all three policies.
    private static PolicyDefinition FastPolicy(string name = "test-policy") => new()
    {
        Name = name,
        Retry = new RetryOptions { MaxAttempts = 2, BaseDelayMs = 1, MaxDelayMs = 5, JitterRatio = 0.0 },
        Circuit = new CircuitOptions { FailureThreshold = 3, OpenDurationSeconds = 30, OnlyCountTransient = true },
        Timeout = new TimeoutOptions { TimeoutMs = 500 }
    };

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var cp = new CompositePolicyBuilder();
        Func<Task> act = () => cp.ExecuteAsync<int>(null!, FastPolicy());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullDefinition()
    {
        var cp = new CompositePolicyBuilder();
        Func<Task> act = () => cp.ExecuteAsync(_ => Task.FromResult(1), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulOperation_ReturnsResult()
    {
        var cp = new CompositePolicyBuilder();

        var result = await cp.ExecuteAsync(_ => Task.FromResult(42), FastPolicy());

        result.Should().Be(42);
    }
    [Fact]
    public async Task ExecuteAsync_TransientFailure_RetriesAndEventuallySucceeds()
    {
        var cp = new CompositePolicyBuilder();
        var attempts = 0;

        var result = await cp.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new TimeoutException("transient");
                return Task.FromResult("ok");
            },
            FastPolicy());

        result.Should().Be("ok");
        attempts.Should().Be(3); // 1 initial + 2 retries
    }

    [Fact]
    public async Task ExecuteAsync_OpenCircuit_BlocksBeforeTimeoutStarts()
    {
        // Retry with zero retries so the first call short-circuits on circuit open.
        var definition = new PolicyDefinition
        {
            Name = "p",
            Retry = new RetryOptions { MaxAttempts = 0 },
            Circuit = new CircuitOptions { FailureThreshold = 3, OpenDurationSeconds = 30 },
            Timeout = new TimeoutOptions { TimeoutMs = 500 }
        };

        var cp = new CompositePolicyBuilder();

        // Open the circuit first (3 transient failures bypass retry by using max=0).
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cp.ExecuteAsync<int>(
                    _ => throw new TimeoutException(),
                    definition));
        }

        // Now circuit is Open. This call must fail with CircuitOpen, not Timeout.
        Func<Task> act = () => cp.ExecuteAsync(_ => Task.FromResult(1), definition);
        var ex = await act.Should().ThrowAsync<ResilienceException>();
        ex.Which.Category.Should().Be(ResilienceErrorCategory.CircuitOpen);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutFiresInsideAttempt_RetriesThenSucceeds()
    {
        var definition = new PolicyDefinition
        {
            Name = "p",
            Retry = new RetryOptions { MaxAttempts = 1, BaseDelayMs = 1, JitterRatio = 0.0 },
            Circuit = new CircuitOptions { FailureThreshold = 5, OpenDurationSeconds = 30 },
            Timeout = new TimeoutOptions { TimeoutMs = 50 }
        };

        var cp = new CompositePolicyBuilder();
        var attempts = 0;

        var result = await cp.ExecuteAsync<string>(
            async ct =>
            {
                attempts++;
                if (attempts == 1)
                {
                    // First attempt exceeds timeout
                    await Task.Delay(200, ct);
                }
                return "second-time";
            },
            definition);

        result.Should().Be("second-time");
        attempts.Should().Be(2);
    }
    [Fact]
    public async Task Circuit_Property_ExposesUnderlyingMonitor()
    {
        var cp = new CompositePolicyBuilder();

        await cp.ExecuteAsync(_ => Task.FromResult(1), FastPolicy("p"));

        var snap = cp.Circuit.Get("p");
        snap.Should().NotBeNull();
        snap!.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task ExecuteAsync_PermanentError_FailsImmediately()
    {
        var cp = new CompositePolicyBuilder();
        var attempts = 0;

        Func<Task> act = () => cp.ExecuteAsync<int>(
            _ =>
            {
                attempts++;
                throw new ArgumentException("permanent");
            },
            FastPolicy());

        await act.Should().ThrowAsync<ArgumentException>();
        attempts.Should().Be(1); // no retry — permanent
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulRetryAfterTransientFailure_LeavesCircuitClosed()
    {
        var cp = new CompositePolicyBuilder();
        var attempts = 0;

        _ = await cp.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                    throw new TimeoutException("transient");
                return Task.FromResult(1);
            },
            FastPolicy());

        var snap = cp.Circuit.Get("test-policy")!;
        snap.State.Should().Be(CircuitState.Closed);
        snap.ConsecutiveFailures.Should().Be(0);
    }
}
