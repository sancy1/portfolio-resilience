// filepath: tests/Portfolio.Resilience.Tests/HedgingPolicyBuilderTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.7.0
// purpose: Verifies hedging: stagger, winner selection, cancellation, per-attempt timeout, and events.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : HedgingPolicyBuilder (Policies/)
//   Depends on : HedgingOptions, ResilienceEventType, xUnit, FluentAssertions
//   See also   : docs/hedging.md, SPEC.md section 16
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class HedgingPolicyBuilderTests
{
    // ------------------------------------------------------------------------
    // Test doubles
    // ------------------------------------------------------------------------

    private sealed class CapturingLogSink : ILogSink
    {
        public ConcurrentQueue<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Enqueue(evt);
    }

    private static HedgingOptions Options(
        bool enabled = true,
        int maxAttempts = 2,
        int delayMs = 10,
        bool exponentialBackoff = false,
        int attemptTimeoutMs = 0,
        bool cancelOnSuccess = true,
        bool emitEvents = false) => new()
        {
            Enabled = enabled,
            MaxAttempts = maxAttempts,
            DelayMs = delayMs,
            ExponentialBackoff = exponentialBackoff,
            AttemptTimeoutMs = attemptTimeoutMs,
            CancelOnSuccess = cancelOnSuccess,
            EmitAttemptEvents = emitEvents
        };

    // ------------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var builder = new HedgingPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>("p", null!, Options());
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOptions()
    {
        var builder = new HedgingPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>("p", _ => Task.FromResult(1), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var builder = new HedgingPolicyBuilder();
        Func<Task> act = () => builder.ExecuteAsync<int>("  ", _ => Task.FromResult(1), Options());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ------------------------------------------------------------------------
    // Disabled / single attempt — pass-through
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Disabled_RunsOnlyOnce()
    {
        var builder = new HedgingPolicyBuilder();
        var calls = 0;

        var result = await builder.ExecuteAsync("p", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(42);
        }, Options(enabled: false));

        result.Should().Be(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_MaxAttemptsOne_RunsOnlyOnce()
    {
        var builder = new HedgingPolicyBuilder();
        var calls = 0;

        var result = await builder.ExecuteAsync("p", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(42);
        }, Options(maxAttempts: 1));

        result.Should().Be(42);
        calls.Should().Be(1);
    }

    // ------------------------------------------------------------------------
    // Primary wins
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_PrimarySucceedsFirst_ReturnsPrimaryResult()
    {
        // Primary returns instantly with result 1; hedge would fire after 100ms
        // (delay), but the race is already won.
        var builder = new HedgingPolicyBuilder();
        var attempts = 0;

        var result = await builder.ExecuteAsync("p", _ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(1);
        }, Options(delayMs: 100));

        result.Should().Be(1);
        attempts.Should().Be(1, "primary completed before the hedge fired");
    }

    // ------------------------------------------------------------------------
    // Hedge wins
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_HedgeSucceedsFirst_ReturnsHedgeResult()
    {
        // Primary is slow (waits on TCS). Hedge (attempt 2) returns instantly
        // after its 10ms delay. Hedge wins.
        var builder = new HedgingPolicyBuilder();
        var attemptCounter = 0;
        var primaryTcs = new TaskCompletionSource<int>();

        var result = await builder.ExecuteAsync("p", async ct =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            if (n == 1)
            {
                // Primary: wait for the TCS (or cancellation)
                using (ct.Register(() => primaryTcs.TrySetCanceled()))
                {
                    return await primaryTcs.Task;
                }
            }
            // Hedge: return instantly
            return 99;
        }, Options(delayMs: 10, cancelOnSuccess: true));

        result.Should().Be(99);
        primaryTcs.TrySetResult(0); // clean up
    }

    // ------------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CancelOnSuccess_CancelsLoser()
    {
        var builder = new HedgingPolicyBuilder();
        var attemptCounter = 0;
        var loserStarted = new TaskCompletionSource();
        var loserCancelled = new TaskCompletionSource();
        var winnerTcs = new TaskCompletionSource<int>();

        var task = builder.ExecuteAsync("p", async ct =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            if (n == 2)
            {
                // Hedge is the loser. Wait for the cancellation.
                loserStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    loserCancelled.TrySetResult();
                    throw;
                }
            }
            // Primary (n == 1): wait for the winner signal
            return await winnerTcs.Task;
        }, Options(delayMs: 10, cancelOnSuccess: true));

        // Wait for both attempts to be in flight
        await loserStarted.Task;
        winnerTcs.TrySetResult(7);

        (await task).Should().Be(7);
        // The loser must have observed cancellation
        await loserCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        loserCancelled.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_CancelOnSuccessFalse_LetsLoserComplete()
    {
        var builder = new HedgingPolicyBuilder();
        var attemptCounter = 0;
        var loserStarted = new TaskCompletionSource();
        var loserFinished = new TaskCompletionSource<int>();
        var winnerTcs = new TaskCompletionSource<int>();

        var task = builder.ExecuteAsync("p", async ct =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            if (n == 2)
            {
                // Hedge is the loser; allow it to complete normally.
                loserStarted.TrySetResult();
                await Task.Delay(50, ct);  // short real delay
                loserFinished.TrySetResult(2);
                return 2;
            }
            return await winnerTcs.Task;
        }, Options(delayMs: 10, cancelOnSuccess: false));

        await loserStarted.Task;
        winnerTcs.TrySetResult(7);

        (await task).Should().Be(7);
        // The loser completes on its own.
        (await loserFinished.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(2);
    }

    // ------------------------------------------------------------------------
    // Per-attempt timeout
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_AttemptTimeout_CancelsSlowAttempt()
    {
        // Primary is slow; AttemptTimeoutMs=50 kills it. Hedge returns instantly.
        var builder = new HedgingPolicyBuilder();
        var attemptCounter = 0;

        var result = await builder.ExecuteAsync("p", async ct =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            if (n == 1)
            {
                await Task.Delay(5000, ct);  // would time out
                return 1;
            }
            return 2;
        }, Options(delayMs: 10, attemptTimeoutMs: 50));

        result.Should().Be(2);
    }

    // ------------------------------------------------------------------------
    // Delay calculation
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 100, false, 0)]
    [InlineData(1, 100, false, 100)]
    [InlineData(2, 100, false, 100)]
    [InlineData(1, 100, true,  100)]
    [InlineData(2, 100, true,  200)]
    [InlineData(3, 100, true,  400)]
    [InlineData(4, 100, true,  800)]
    public void CalculateDelayMs_ReturnsExpected(int attemptIndex, int baseMs, bool exp, int expected)
    {
        var options = new HedgingOptions { DelayMs = baseMs, ExponentialBackoff = exp };
        HedgingPolicyBuilder.CalculateDelayMs(attemptIndex, options).Should().Be(expected);
    }

    // ------------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_EmitAttemptEvents_EmitsHedgeWon()
    {
        var sink = new CapturingLogSink();
        var emitter = new ResilienceEventEmitter(sink);
        var builder = new HedgingPolicyBuilder(emitter: emitter);

        var result = await builder.ExecuteAsync("p", _ => Task.FromResult(1),
            Options(emitEvents: true, maxAttempts: 1));  // single attempt = instant win

        result.Should().Be(1);

        // With a single attempt, only the winner event fires.
        sink.Events.Should().Contain(e => e.EventType == ResilienceEventType.HedgeWon);
    }

    [Fact]
    public async Task ExecuteAsync_EmitAttemptEventsFalse_NoEvents()
    {
        var sink = new CapturingLogSink();
        var emitter = new ResilienceEventEmitter(sink);
        var builder = new HedgingPolicyBuilder(emitter: emitter);

        await builder.ExecuteAsync("p", _ => Task.FromResult(1),
            Options(emitEvents: false, maxAttempts: 1));

        sink.Events.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------
    // All attempts fail — exception preservation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_AllAttemptsFail_ThrowsFirstAttemptException()
    {
        var builder = new HedgingPolicyBuilder();
        var attemptCounter = 0;

        Func<Task> act = () => builder.ExecuteAsync<int>("p", _ =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            throw new InvalidOperationException($"attempt-{n}");
        }, Options(delayMs: 10, maxAttempts: 2));

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        // Option D: the primary failure is the outer exception.
        ex.Which.Message.Should().Be("attempt-1");
    }

    [Fact]
    public async Task ExecuteAsync_AllAttemptsFail_AttachesOtherErrorsToData()
    {
        var builder = new HedgingPolicyBuilder();
        var attemptCounter = 0;

        Func<Task> act = () => builder.ExecuteAsync<int>("p", _ =>
        {
            var n = Interlocked.Increment(ref attemptCounter);
            throw new InvalidOperationException($"attempt-{n}");
        }, Options(delayMs: 10, maxAttempts: 3));

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        // The second and third attempts' errors are attached to Data.
        ex.Which.Data["hedge_attempt_2"].Should().Be("attempt-2");
        ex.Which.Data["hedge_attempt_3"].Should().Be("attempt-3");
    }

    [Fact]
    public async Task ExecuteAsync_SingleFailure_NoDataDecoration()
    {
        var builder = new HedgingPolicyBuilder();

        Func<Task> act = () => builder.ExecuteAsync<int>("p", _ =>
            throw new InvalidOperationException("only-one"),
            Options(delayMs: 10, maxAttempts: 1));

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Data.Count.Should().Be(0);
    }

    // ------------------------------------------------------------------------
    // Caller cancellation
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CallerCancels_ThrowsOperationCanceled()
    {
        var builder = new HedgingPolicyBuilder();
        using var cts = new CancellationTokenSource();

        var started = new TaskCompletionSource();

        var task = builder.ExecuteAsync("p", async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        }, Options(delayMs: 100, maxAttempts: 1), cts.Token);

        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
