// filepath: dotnet/samples/Samples.App/Scenarios/04_RateLimiterScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates all four rate limiter strategies - TokenBucket, SlidingWindow, FixedWindow, ConcurrencyLimit
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, RateLimitStrategy
//   Used by    : Program.cs, Integration tests
//   See also   : docs/rate-limiter.md
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 04 - Rate limiter. Runs one sub-test per strategy.
/// </summary>
/// <remarks>
/// The three windowed strategies (TokenBucket, SlidingWindow, FixedWindow) cap
/// cumulative calls over a rolling window and are tested with sequential calls.
/// The ConcurrencyLimit strategy caps simultaneous calls and is tested by firing
/// overlapping operations that are held open until we release them - a sequential
/// burst would never trigger it.
/// </remarks>
public static class RateLimiterScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Captured but not used for limiter assertions.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>A one-line message describing what was verified.</returns>
    public static async Task<string> RunAsync(
        IResilienceExecutor executor,
        CapturingSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sink);

        sink.Clear();

        var results = new List<string>
        {
            await RunWindowedSubTestAsync(executor, "rate-limiter-token-bucket", "TokenBucket", ct),
            await RunWindowedSubTestAsync(executor, "rate-limiter-sliding-window", "SlidingWindow", ct),
            await RunWindowedSubTestAsync(executor, "rate-limiter-fixed-window", "FixedWindow", ct),
            await RunConcurrencySubTestAsync(executor, "rate-limiter-concurrency", "ConcurrencyLimit", ct),
        };

        return "All four rate limiter strategies enforced: " + string.Join("; ", results) + ".";
    }

    /// <summary>Sequential sub-test for the windowed strategies.</summary>
    private static async Task<string> RunWindowedSubTestAsync(
        IResilienceExecutor executor,
        string policyName,
        string strategyLabel,
        CancellationToken ct)
    {
        const int permitLimit = 3;
        const int totalCalls = 5;

        var invocations = 0;
        var succeeded = 0;
        var rejected = 0;

        for (var i = 0; i < totalCalls; i++)
        {
            try
            {
                await executor.ExecuteAsync<int>(
                    policyName: policyName,
                    operation: _ =>
                    {
                        invocations++;
                        return Task.FromResult(1);
                    },
                    fallback: null!,
                    idempotencyKey: null,
                    timeBudgetMs: null,
                    ct: ct);
                succeeded++;
            }
            catch (Exception)
            {
                rejected++;
            }
        }

        if (succeeded != permitLimit)
        {
            throw new InvalidOperationException(
                $"{strategyLabel}: expected {permitLimit} successes, got {succeeded}");
        }
        if (rejected != totalCalls - permitLimit)
        {
            throw new InvalidOperationException(
                $"{strategyLabel}: expected {totalCalls - permitLimit} rejections, got {rejected}");
        }
        if (invocations != permitLimit)
        {
            throw new InvalidOperationException(
                $"{strategyLabel}: expected {permitLimit} operation invocations, got {invocations}");
        }

        return $"{strategyLabel}({succeeded} ok, {rejected} rejected)";
    }

    /// <summary>
    /// Concurrent sub-test for the ConcurrencyLimit strategy. Launches N+1
    /// overlapping operations, holds them open, and asserts that only N are
    /// permitted to run at the same time.
    /// </summary>
    private static async Task<string> RunConcurrencySubTestAsync(
        IResilienceExecutor executor,
        string policyName,
        string strategyLabel,
        CancellationToken ct)
    {
        const int permitLimit = 3;
        const int totalCalls = 5;

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var rejected = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < totalCalls; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await executor.ExecuteAsync<int>(
                        policyName: policyName,
                        operation: async token =>
                        {
                            Interlocked.Increment(ref started);
                            await release.Task.WaitAsync(token);
                            return 1;
                        },
                        fallback: null!,
                        idempotencyKey: null,
                        timeBudgetMs: null,
                        ct: ct);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref rejected);
                }
            }, ct));
        }

        await Task.Delay(200, ct);

        var startedSoFar = Volatile.Read(ref started);
        var rejectedSoFar = Volatile.Read(ref rejected);

        release.TrySetResult(true);
        await Task.WhenAll(tasks);

        if (startedSoFar != permitLimit)
        {
            throw new InvalidOperationException(
                $"{strategyLabel}: expected {permitLimit} simultaneous starts, got {startedSoFar}");
        }
        if (rejectedSoFar != totalCalls - permitLimit)
        {
            throw new InvalidOperationException(
                $"{strategyLabel}: expected {totalCalls - permitLimit} immediate rejections, got {rejectedSoFar}");
        }

        return $"{strategyLabel}({startedSoFar} concurrent, {rejectedSoFar} rejected)";
    }
}