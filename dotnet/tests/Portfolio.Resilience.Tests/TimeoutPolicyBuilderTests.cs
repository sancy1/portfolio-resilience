// filepath: tests/Portfolio.Resilience.Tests/TimeoutPolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies TimeoutPolicyBuilder enforces its ceiling and distinguishes caller cancellation.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : TimeoutPolicyBuilder (Policies/TimeoutPolicyBuilder.cs)
//   Depends on : TimeoutOptions, ResilienceException, xUnit, FluentAssertions
//   See also   : docs/timeout.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class TimeoutPolicyBuilderTests
{
    private static readonly TimeoutOptions ShortTimeout = new() { TimeoutMs = 50 };
    private static readonly TimeoutOptions DisabledTimeout = new() { TimeoutMs = 0 };

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var builder = new TimeoutPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>(null!, new TimeoutOptions(), "p");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOptions()
    {
        var builder = new TimeoutPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync(_ => Task.FromResult(1), null!, "p");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var builder = new TimeoutPolicyBuilder();
        Func<Task> actNull = () => builder.ExecuteAsync(_ => Task.FromResult(1), new TimeoutOptions(), null!);
        Func<Task> actEmpty = () => builder.ExecuteAsync(_ => Task.FromResult(1), new TimeoutOptions(), "");
        Func<Task> actWhitespace = () => builder.ExecuteAsync(_ => Task.FromResult(1), new TimeoutOptions(), "   ");

        await actNull.Should().ThrowAsync<ArgumentException>();
        await actEmpty.Should().ThrowAsync<ArgumentException>();
        await actWhitespace.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_FastOperation_ReturnsResult()
    {
        var builder = new TimeoutPolicyBuilder();

        var result = await builder.ExecuteAsync(
            ct => Task.FromResult(42),
            ShortTimeout,
            "p");

        result.Should().Be(42);
    }
    [Fact]
    public async Task ExecuteAsync_SlowOperation_ThrowsTimeoutException()
    {
        var builder = new TimeoutPolicyBuilder();

        Func<Task> act = () => builder.ExecuteAsync(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return 1;
            },
            ShortTimeout,
            "test-policy");

        var ex = await act.Should().ThrowAsync<ResilienceException>();
        ex.Which.Category.Should().Be(ResilienceErrorCategory.Timeout);
        ex.Which.PolicyName.Should().Be("test-policy");
        ex.Which.Metadata.Should().ContainKey("timeout_ms");
    }

    [Fact]
    public async Task ExecuteAsync_CallerCancels_DoesNotThrowTimeout()
    {
        var builder = new TimeoutPolicyBuilder();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(20); // fires before the 5s timeout

        Func<Task> act = () => builder.ExecuteAsync(
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return 1;
            },
            new TimeoutOptions { TimeoutMs = 5000 },
            "p",
            cts.Token);

        // Must throw OperationCanceledException (or a subclass), NOT ResilienceException.
        var exception = await act.Should().ThrowAsync<OperationCanceledException>();
        exception.Which.Should().NotBeOfType<ResilienceException>();
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutDisabled_RunsToCompletion()
    {
        var builder = new TimeoutPolicyBuilder();

        // Would normally exceed a 50ms ceiling, but TimeoutMs=0 disables it.
        var result = await builder.ExecuteAsync(
            async ct =>
            {
                await Task.Delay(150, ct);
                return "done";
            },
            DisabledTimeout,
            "p");

        result.Should().Be("done");
    }

    [Fact]
    public async Task ExecuteAsync_ExactlyAtCeiling_MaySucceed()
    {
        // Not a strict boundary test — just confirms operations near the
        // ceiling complete when they finish in time.
        var builder = new TimeoutPolicyBuilder();

        var result = await builder.ExecuteAsync(
            async ct =>
            {
                await Task.Delay(10, ct);
                return 1;
            },
            new TimeoutOptions { TimeoutMs = 500 },
            "p");

        result.Should().Be(1);
    }
}
