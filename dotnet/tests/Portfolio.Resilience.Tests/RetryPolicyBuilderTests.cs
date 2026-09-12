// filepath: tests/Portfolio.Resilience.Tests/RetryPolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies retry attempt counts, backoff formula, jitter bounds, and classifier gating.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : RetryPolicyBuilder (Policies/RetryPolicyBuilder.cs)
//   Depends on : RetryOptions, ErrorClassifier, xUnit, FluentAssertions
//   See also   : docs/retry.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class RetryPolicyBuilderTests
{
    // ------------------------------------------------------------------------
    // Test double: records delays without actually sleeping
    // ------------------------------------------------------------------------

    private sealed class FakeDelay
    {
        public List<TimeSpan> Delays { get; } = new();

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
    // ------------------------------------------------------------------------
    // Constructor + validation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var builder = new RetryPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>(null!, new RetryOptions());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOptions()
    {
        var builder = new RetryPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync(_ => Task.FromResult(1), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ------------------------------------------------------------------------
    // Success on first attempt
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SucceedsFirstAttempt_DoesNotRetry()
    {
        var fake = new FakeDelay();
        var builder = new RetryPolicyBuilder(delayStrategy: fake.Delay);
        var attempts = 0;

        var result = await builder.ExecuteAsync(
            ct => { attempts++; return Task.FromResult(42); },
            new RetryOptions { MaxAttempts = 3 });

        result.Should().Be(42);
        attempts.Should().Be(1);
        fake.Delays.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------
    // Retry on transient failure
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_RetriesOnTransientFailure_SucceedsOnSecond()
    {
        var fake = new FakeDelay();
        var builder = new RetryPolicyBuilder(delayStrategy: fake.Delay);
        var attempts = 0;

        var result = await builder.ExecuteAsync(
            ct =>
            {
                attempts++;
                if (attempts < 2)
                    throw new TimeoutException("transient");
                return Task.FromResult("ok");
            },
            new RetryOptions { MaxAttempts = 3, BaseDelayMs = 10 });

        result.Should().Be("ok");
        attempts.Should().Be(2);
        fake.Delays.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustsRetries_ThrowsLastException()
    {
        var fake = new FakeDelay();
        var builder = new RetryPolicyBuilder(delayStrategy: fake.Delay);
        var attempts = 0;

        Func<Task> act = () => builder.ExecuteAsync<int>(
            ct =>
            {
                attempts++;
                throw new TimeoutException("always");
            },
            new RetryOptions { MaxAttempts = 2, BaseDelayMs = 10 });

        await act.Should().ThrowAsync<TimeoutException>();
        attempts.Should().Be(3); // 1 initial + 2 retries
        fake.Delays.Should().HaveCount(2);
    }
    // ------------------------------------------------------------------------
    // Classifier gating — permanent errors must not be retried
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_PermanentError_DoesNotRetry()
    {
        var fake = new FakeDelay();
        var builder = new RetryPolicyBuilder(delayStrategy: fake.Delay);
        var attempts = 0;

        Func<Task> act = () => builder.ExecuteAsync<int>(
            ct =>
            {
                attempts++;
                throw new ArgumentException("permanent");
            },
            new RetryOptions { MaxAttempts = 5, BaseDelayMs = 10 });

        await act.Should().ThrowAsync<ArgumentException>();
        attempts.Should().Be(1);            // no retry
        fake.Delays.Should().BeEmpty();     // no delay
    }

    [Fact]
    public async Task ExecuteAsync_RetryOnPermanent_RetriesWhenEnabled()
    {
        var fake = new FakeDelay();
        var builder = new RetryPolicyBuilder(delayStrategy: fake.Delay);
        var attempts = 0;

        Func<Task> act = () => builder.ExecuteAsync<int>(
            ct =>
            {
                attempts++;
                throw new ArgumentException("permanent");
            },
            new RetryOptions { MaxAttempts = 2, BaseDelayMs = 10, RetryOnPermanent = true });

        await act.Should().ThrowAsync<ArgumentException>();
        attempts.Should().Be(3); // 1 + 2 retries because RetryOnPermanent=true
    }

    [Fact]
    public async Task ExecuteAsync_ZeroMaxAttempts_DoesNotRetry()
    {
        var fake = new FakeDelay();
        var builder = new RetryPolicyBuilder(delayStrategy: fake.Delay);
        var attempts = 0;

        Func<Task> act = () => builder.ExecuteAsync<int>(
            ct =>
            {
                attempts++;
                throw new TimeoutException("transient");
            },
            new RetryOptions { MaxAttempts = 0 });

        await act.Should().ThrowAsync<TimeoutException>();
        attempts.Should().Be(1);
        fake.Delays.Should().BeEmpty();
    }
    // ------------------------------------------------------------------------
    // Backoff formula
    // ------------------------------------------------------------------------

    [Fact]
    public void CalculateDelay_FirstRetry_IsBasePlusJitter()
    {
        var builder = new RetryPolicyBuilder();
        var options = new RetryOptions { BaseDelayMs = 100, MaxDelayMs = 30_000, JitterRatio = 0.3 };

        var delay = builder.CalculateDelay(attempt: 1, options);

        delay.TotalMilliseconds.Should().BeInRange(100.0, 130.0); // 100 + up to 30% jitter
    }

    [Fact]
    public void CalculateDelay_SecondRetry_DoublesBase()
    {
        var builder = new RetryPolicyBuilder();
        var options = new RetryOptions { BaseDelayMs = 100, MaxDelayMs = 30_000, JitterRatio = 0.3 };

        var delay = builder.CalculateDelay(attempt: 2, options);

        delay.TotalMilliseconds.Should().BeInRange(200.0, 230.0); // 200 + up to 30% jitter
    }

    [Fact]
    public void CalculateDelay_ThirdRetry_QuadruplesBase()
    {
        var builder = new RetryPolicyBuilder();
        var options = new RetryOptions { BaseDelayMs = 100, MaxDelayMs = 30_000, JitterRatio = 0.3 };

        var delay = builder.CalculateDelay(attempt: 3, options);

        delay.TotalMilliseconds.Should().BeInRange(400.0, 430.0);
    }

    [Fact]
    public void CalculateDelay_CapsAtMaxDelay()
    {
        var builder = new RetryPolicyBuilder();
        var options = new RetryOptions { BaseDelayMs = 100, MaxDelayMs = 250, JitterRatio = 0.0 };

        // Attempt 5 would be 100 * 2^4 = 1600, but cap is 250
        var delay = builder.CalculateDelay(attempt: 5, options);

        delay.TotalMilliseconds.Should().Be(250.0);
    }

    [Fact]
    public void CalculateDelay_ZeroJitter_IsExact()
    {
        var builder = new RetryPolicyBuilder();
        var options = new RetryOptions { BaseDelayMs = 100, MaxDelayMs = 30_000, JitterRatio = 0.0 };

        var delay = builder.CalculateDelay(attempt: 1, options);

        delay.TotalMilliseconds.Should().Be(100.0);
    }

    [Fact]
    public void CalculateDelay_JitterStaysWithinBounds()
    {
        var builder = new RetryPolicyBuilder();
        var options = new RetryOptions { BaseDelayMs = 100, MaxDelayMs = 30_000, JitterRatio = 0.3 };

        // Run 100 times, verify every delay is within [100, 130]
        for (var i = 0; i < 100; i++)
        {
            var delay = builder.CalculateDelay(attempt: 1, options);
            delay.TotalMilliseconds.Should().BeInRange(100.0, 130.0);
        }
    }
}
