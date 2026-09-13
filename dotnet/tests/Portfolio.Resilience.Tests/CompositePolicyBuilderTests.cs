// filepath: tests/Portfolio.Resilience.Tests/CompositePolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: Verifies CompositePolicyBuilder chains rate limiter, bulkhead, retry, circuit, timeout in the correct order.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : CompositePolicyBuilder (Policies/CompositePolicyBuilder.cs)
//   Depends on : RateLimiterPolicyBuilder, BulkheadPolicyBuilder, RetryPolicyBuilder,
//                CircuitPolicyBuilder, TimeoutPolicyBuilder, xUnit, FluentAssertions
//   See also   : docs/executor.md, docs/rate-limiter.md, docs/bulkhead.md
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CompositePolicyBuilderTests
{
    // A definition that is fast enough for unit tests, but exercises all three original policies.
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
        var definition = new PolicyDefinition
        {
            Name = "p",
            Retry = new RetryOptions { MaxAttempts = 0 },
            Circuit = new CircuitOptions { FailureThreshold = 3, OpenDurationSeconds = 30 },
            Timeout = new TimeoutOptions { TimeoutMs = 500 }
        };

        var cp = new CompositePolicyBuilder();

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                cp.ExecuteAsync<int>(
                    _ => throw new TimeoutException(),
                    definition));
        }

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
        attempts.Should().Be(1); // no retry - permanent
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

    // ------------------------------------------------------------------------
    // Rate limiter + bulkhead integration (v0.6.0)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_RateLimiterEnabled_RejectsWhenExhausted()
    {
        var rateLimiter = new RateLimiterPolicyBuilder();
        var cp = new CompositePolicyBuilder(rateLimiter: rateLimiter);

        var definition = FastPolicy("p");
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.Strategy = RateLimitStrategy.SlidingWindow;
        definition.RateLimiter.PermitLimit = 2;
        definition.RateLimiter.WindowSeconds = 60;

        // 2 pass.
        _ = await cp.ExecuteAsync(_ => Task.FromResult(1), definition);
        _ = await cp.ExecuteAsync(_ => Task.FromResult(2), definition);

        // 3rd rejects.
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => cp.ExecuteAsync(_ => Task.FromResult(3), definition));

        ex.Metadata!["reason"].Should().Be("rejected_immediately");
        ex.Metadata!["strategy"].Should().Be("SlidingWindow");
    }

    [Fact]
    public async Task ExecuteAsync_BulkheadEnabled_RejectsWhenFull()
    {
        var bulkhead = new BulkheadPolicyBuilder();
        var cp = new CompositePolicyBuilder(bulkhead: bulkhead);

        var definition = FastPolicy("p");
        definition.Bulkhead.Enabled = true;
        definition.Bulkhead.MaxConcurrency = 1;
        definition.Bulkhead.MaxQueue = 0;

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = cp.ExecuteAsync(async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, definition);

        await firstEntered.Task;

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => cp.ExecuteAsync(_ => Task.FromResult(2), definition));

        ex.Metadata!["reason"].Should().Be("rejected_immediately");
        ex.Metadata!["max_concurrency"].Should().Be(1);

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_RateLimiterEnabledButBuilderMissing_ThrowsInvalidOperation()
    {
        var cp = new CompositePolicyBuilder(); // no rate limiter provided

        var definition = FastPolicy("rate-limited-policy");
        definition.RateLimiter.Enabled = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cp.ExecuteAsync(_ => Task.FromResult(1), definition));

        ex.Message.Should().Contain("rate-limited-policy");
        ex.Message.Should().Contain("RateLimiterPolicyBuilder");
    }

    [Fact]
    public async Task ExecuteAsync_BulkheadEnabledButBuilderMissing_ThrowsInvalidOperation()
    {
        var cp = new CompositePolicyBuilder(); // no bulkhead provided

        var definition = FastPolicy("bulkhead-policy");
        definition.Bulkhead.Enabled = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cp.ExecuteAsync(_ => Task.FromResult(1), definition));

        ex.Message.Should().Contain("bulkhead-policy");
        ex.Message.Should().Contain("BulkheadPolicyBuilder");
    }

    [Fact]
    public async Task ExecuteAsync_DisabledRateLimiterAndBulkhead_OldPipelineUnchanged()
    {
        // Neither enabled: existing behavior must be identical to v0.5.x.
        var rateLimiter = new RateLimiterPolicyBuilder();
        var bulkhead = new BulkheadPolicyBuilder();
        var cp = new CompositePolicyBuilder(rateLimiter: rateLimiter, bulkhead: bulkhead);

        var definition = FastPolicy("plain-policy");
        // RateLimiter.Enabled and Bulkhead.Enabled default to false.

        var attempts = 0;
        var result = await cp.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                    throw new TimeoutException("transient");
                return Task.FromResult("ok");
            },
            definition);

        result.Should().Be("ok");
        attempts.Should().Be(2);
    }
}
