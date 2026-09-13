// filepath: tests/Portfolio.Resilience.Tests/BulkheadPolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: Verifies bulkhead concurrency cap, queue behavior, slot release, and rejection metadata.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : BulkheadPolicyBuilder (Policies/BulkheadPolicyBuilder.cs)
//   Depends on : BulkheadOptions, ResilienceException, xUnit, FluentAssertions
//   See also   : docs/bulkhead.md, SPEC.md section 13
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

public sealed class BulkheadPolicyBuilderTests
{
    // ------------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------------

    private sealed class CapturingLogSink : ILogSink
    {
        public ConcurrentQueue<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Enqueue(evt);
    }

    private static BulkheadOptions Options(
        int maxConcurrency = 2,
        int maxQueue = 0,
        int queueTimeoutMs = 5_000,
        bool enabled = true) => new()
        {
            Enabled = enabled,
            MaxConcurrency = maxConcurrency,
            MaxQueue = maxQueue,
            QueueTimeoutMs = queueTimeoutMs
        };

    // ------------------------------------------------------------------------
    // Argument validation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var builder = new BulkheadPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>("p", null!, Options());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOptions()
    {
        var builder = new BulkheadPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>("p", _ => Task.FromResult(1), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var builder = new BulkheadPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>("  ", _ => Task.FromResult(1), Options());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ------------------------------------------------------------------------
    // Disabled
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Disabled_PassesThrough()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1, enabled: false);

        // Three sequential calls, MaxConcurrency=1 - all pass because disabled.
        for (var i = 0; i < 3; i++)
        {
            var r = await builder.ExecuteAsync("p", _ => Task.FromResult(i), options);
            r.Should().Be(i);
        }
    }

    // ------------------------------------------------------------------------
    // Concurrency cap
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CapsConcurrency()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        Func<Task> act = () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        var ex = await act.Should().ThrowAsync<ResilienceException>();
        ex.Which.Metadata!["reason"].Should().Be("rejected_immediately");

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesSlotOnSuccess()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1);

        var r1 = await builder.ExecuteAsync("p", _ => Task.FromResult(1), options);
        r1.Should().Be(1);

        var r2 = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        r2.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ReleasesSlotOnFailure()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.ExecuteAsync<int>("p", _ => throw new InvalidOperationException("boom"), options));

        // Slot must be free despite the exception.
        var r2 = await builder.ExecuteAsync("p", _ => Task.FromResult(2), options);
        r2.Should().Be(2);
    }

    // ------------------------------------------------------------------------
    // Queue behavior
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_QueuedCallProceedsAfterSlotFrees()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1, maxQueue: 1, queueTimeoutMs: 500);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        var second = builder.ExecuteAsync("p", _ => Task.FromResult(2), options);

        await Task.Delay(50);
        releaseFirst.SetResult();

        (await first).Should().Be(1);
        (await second).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_QueueTimeoutRejects()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1, maxQueue: 1, queueTimeoutMs: 50);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        ex.Metadata!["reason"].Should().Be("queue_timeout");

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_QueueFullRejects()
    {
        var builder = new BulkheadPolicyBuilder();
        // MaxQueue = 1: one waiter allowed. Second waiter must be rejected with queue_full.
        var options = Options(maxConcurrency: 1, maxQueue: 1, queueTimeoutMs: 1_000);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        // First waiter occupies the single queue slot.
        var second = builder.ExecuteAsync("p", async _ =>
        {
            await Task.Delay(200);
            return 2;
        }, options);

        // Give the second call time to enter the waiting state.
        await Task.Delay(50);

        // Third call: queue is full.
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(3), options));

        ex.Metadata!["reason"].Should().Be("queue_full");

        releaseFirst.SetResult();
        (await first).Should().Be(1);
        (await second).Should().Be(2);
    }

    // ------------------------------------------------------------------------
    // Rejection - event emission + category
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Rejection_EmitsBulkheadRejectedEvent()
    {
        var sink = new CapturingLogSink();
        var emitter = new ResilienceEventEmitter(sink);
        var builder = new BulkheadPolicyBuilder(emitter: emitter);
        var options = Options(maxConcurrency: 1);

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        sink.Events.Should().HaveCount(1);
        var evt = sink.Events.Single();
        evt.EventType.Should().Be(ResilienceEventType.BulkheadRejected);
        evt.PolicyName.Should().Be("p");
        evt.Metadata!["max_concurrency"].Should().Be(1);
        evt.Metadata!["max_queue"].Should().Be(0);
        evt.Metadata!["reason"].Should().Be("rejected_immediately");

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_Rejection_UsesConfiguredCategory()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1);
        options.RejectionCategory = ResilienceErrorCategory.Permanent;

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = builder.ExecuteAsync("p", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        }, options);

        await firstEntered.Task;

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => builder.ExecuteAsync("p", _ => Task.FromResult(2), options));

        ex.Category.Should().Be(ResilienceErrorCategory.Permanent);

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    // ------------------------------------------------------------------------
    // Isolation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_IndependentPolicies_HaveIndependentSemaphores()
    {
        var builder = new BulkheadPolicyBuilder();
        var options = Options(maxConcurrency: 1);

        var p1Entered = new TaskCompletionSource();
        var releaseP1 = new TaskCompletionSource();

        var p1 = builder.ExecuteAsync("policy-a", async _ =>
        {
            p1Entered.SetResult();
            await releaseP1.Task;
            return 1;
        }, options);

        await p1Entered.Task;

        // policy-b is unaffected by policy-a holding its slot.
        var r2 = await builder.ExecuteAsync("policy-b", _ => Task.FromResult(2), options);
        r2.Should().Be(2);

        releaseP1.SetResult();
        (await p1).Should().Be(1);
    }
}
