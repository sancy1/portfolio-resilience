// filepath: tests/Portfolio.Resilience.Tests/RateLimiterPolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: Verifies the four rate limiter strategies, queue behavior, and rejection metadata.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : RateLimiterPolicyBuilder (Policies/RateLimiterPolicyBuilder.cs)
//   Depends on : RateLimiterOptions, RateLimitStrategy, ResilienceException, xUnit, FluentAssertions
//   See also   : docs/rate-limiter.md, SPEC.md section 12
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class RateLimiterPolicyBuilderTests
{
    // ------------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------------

    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan span) => Now = Now.Add(span);
    }

    private sealed class FakeDelay
    {
        public List<TimeSpan> Delays { get; } = new();
        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingLogSink : ILogSink
    {
        public ConcurrentQueue<ResilienceEvent> Events { get; } = new();

        public void Emit(ResilienceEvent evt) => Events.Enqueue(evt);
    }

    private static RateLimiterOptions Options(
        RateLimitStrategy strategy,
        int permitLimit = 2,
        int windowSeconds = 60,
        int queueLimit = 0,
        int queueTimeoutMs = 5_000,
        bool enabled = true) => new()
        {
            Enabled = enabled,
            Strategy = strategy,
            PermitLimit = permitLimit,
            WindowSeconds = windowSeconds,
            QueueLimit = queueLimit,
            QueueTimeoutMs = queueTimeoutMs
        };

    // ------------------------------------------------------------------------
    // Constructor / argument validation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var builder = new RateLimiterPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>(
            "p", null!, Options(RateLimitStrategy.SlidingWindow));
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOptions()
    {
        var builder = new RateLimiterPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>(
            "p", _ => Task.FromResult(1), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var builder = new RateLimiterPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>(
            "  ", _ => Task.FromResult(1), Options(RateLimitStrategy.SlidingWindow));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ------------------------------------------------------------------------
    // Disabled limiter
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_DisabledLimiter_PassesThrough()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(RateLimitStrategy.SlidingWindow, enabled: false);

        // Three calls, PermitLimit=2 - disabled means all pass.
        for (var i = 0; i < 3; i++)
        {
            var result = await builder.ExecuteAsync("p", _ => Task.FromResult(i), options);
            result.Should().Be(i);
        }
    }

    // ------------------------------------------------------------------------
    // TokenBucket
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_TokenBucket_AllowsUpToCapacity()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        var options = Options(RateLimitStrategy.TokenBucket, permitLimit: 3, windowSeconds: 60);

        for (var i = 0; i < 3; i++)
        {
            var r = await builder.ExecuteAsync("p", _ => Task.FromResult(i), options);
            r.Should().Be(i);
        }

        Func<Task> act = () => builder.ExecuteAsync("p", _ => Task.FromResult(99), options);
        await act.Should().ThrowAsync<ResilienceException>();
    }

    [Fact]
    public async Task ExecuteAsync_TokenBucket_RefillsOverTime()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        // PermitLimit=1, WindowSeconds=1 -> 1 token per second.
        var options = Options(RateLimitStrategy.TokenBucket, permitLimit: 1, windowSeconds: 1);

        // Consume the only token.
        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);

        // Advance 1 second -> one token refills.
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        result.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_TokenBucket_RejectsWhenEmptyAndNoQueue()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        var options = Options(RateLimitStrategy.TokenBucket, permitLimit: 1, windowSeconds: 60);

        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        ex.Category.Should().Be(ResilienceErrorCategory.Transient);
        ex.Metadata!["reason"].Should().Be("rejected_immediately");
    }

    // ------------------------------------------------------------------------
    // SlidingWindow
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SlidingWindow_AllowsUpToLimit()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        var options = Options(RateLimitStrategy.SlidingWindow, permitLimit: 3, windowSeconds: 60);

        for (var i = 0; i < 3; i++)
        {
            _ = await builder.ExecuteAsync("p", _ => Task.FromResult(i), options);
        }

        Func<Task> act = () => builder.ExecuteAsync("p", _ => Task.FromResult(99), options);
        await act.Should().ThrowAsync<ResilienceException>();
    }

    [Fact]
    public async Task ExecuteAsync_SlidingWindow_WindowSlides()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        var options = Options(RateLimitStrategy.SlidingWindow, permitLimit: 2, windowSeconds: 10);

        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);
        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);

        // Window full. Advance past it.
        clock.Advance(TimeSpan.FromSeconds(11));

        // Capacity returns.
        var r = await builder.ExecuteAsync("p", _ => Task.FromResult(3), options);
        r.Should().Be(3);
    }

    // ------------------------------------------------------------------------
    // FixedWindow
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_FixedWindow_AllowsUpToLimit()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        var options = Options(RateLimitStrategy.FixedWindow, permitLimit: 3, windowSeconds: 60);

        for (var i = 0; i < 3; i++)
        {
            _ = await builder.ExecuteAsync("p", _ => Task.FromResult(i), options);
        }

        Func<Task> act = () => builder.ExecuteAsync("p", _ => Task.FromResult(99), options);
        await act.Should().ThrowAsync<ResilienceException>();
    }

    [Fact]
    public async Task ExecuteAsync_FixedWindow_RollsOver()
    {
        var clock = new FakeClock();
        var builder = new RateLimiterPolicyBuilder(clock: () => clock.Now);
        var options = Options(RateLimitStrategy.FixedWindow, permitLimit: 2, windowSeconds: 10);

        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);
        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);

        // Bucket full. Advance to the next bucket.
        clock.Advance(TimeSpan.FromSeconds(10));

        var r = await builder.ExecuteAsync("p", _ => Task.FromResult(3), options);
        r.Should().Be(3);
    }

    // ------------------------------------------------------------------------
    // ConcurrencyLimit - the critical strategy
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_CapsConcurrency()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(RateLimitStrategy.ConcurrencyLimit, permitLimit: 1);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        // First call holds the slot.
        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        // Second call, no queue -> rejects immediately.
        Func<Task> act = () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        var ex = await act.Should().ThrowAsync<ResilienceException>();
        ex.Which.Metadata!["reason"].Should().Be("rejected_immediately");

        // Release the first.
        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_ReleasesSlotOnSuccess()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(RateLimitStrategy.ConcurrencyLimit, permitLimit: 1);

        var r1 = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);
        r1.Should().Be(1);

        // Slot must be free now.
        var r2 = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        r2.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_ReleasesSlotOnFailure()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(RateLimitStrategy.ConcurrencyLimit, permitLimit: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.ExecuteAsync<int>("p", _ => throw new InvalidOperationException("boom"), options));

        // Slot must be free despite the exception (finally releases).
        var r2 = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        r2.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_QueuedCallProceedsAfterSlotFrees()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(
            RateLimitStrategy.ConcurrencyLimit,
            permitLimit: 1,
            queueLimit: 1,
            queueTimeoutMs: 500);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        // Second call queues. Release the first after a brief moment.
        var second = builder.ExecuteAsync("p", _ => Task.FromResult(2), options);

        await Task.Delay(50);
        releaseFirst.SetResult();

        (await first).Should().Be(1);
        (await second).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrencyLimit_QueueTimeoutRejects()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(
            RateLimitStrategy.ConcurrencyLimit,
            permitLimit: 1,
            queueLimit: 1,
            queueTimeoutMs: 50);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        // Second call queues, but the first does not release within 50ms.
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        ex.Metadata!["reason"].Should().Be("queue_timeout");

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    // ------------------------------------------------------------------------
    // Rejection - event emission + category
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Rejection_EmitsRateLimitedEvent()
    {
        var sink = new CapturingLogSink();
        var emitter = new ResilienceEventEmitter(sink);
        var builder = new RateLimiterPolicyBuilder(emitter: emitter);
        var options = Options(RateLimitStrategy.SlidingWindow, permitLimit: 1);

        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);

        await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        sink.Events.Should().HaveCount(1);
        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.RateLimited);
        evt.PolicyName.Should().Be("p");
        evt.Metadata!["strategy"].Should().Be("SlidingWindow");
        evt.Metadata!["permit_limit"].Should().Be(1);
        evt.Metadata!["queue_limit"].Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Rejection_UsesConfiguredCategory()
    {
        var builder = new RateLimiterPolicyBuilder();
        var options = Options(RateLimitStrategy.SlidingWindow, permitLimit: 1);
        options.RejectionCategory = ResilienceErrorCategory.Permanent;

        _ = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        ex.Category.Should().Be(ResilienceErrorCategory.Permanent);
    }
}
