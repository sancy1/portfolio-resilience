// filepath: dotnet/samples/Samples.App/Scenarios/05_BulkheadScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates bulkhead isolation - concurrency cap, bounded queue, immediate rejection
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor
//   Used by    : Program.cs, Integration tests
//   See also   : docs/bulkhead.md
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 05 - Bulkhead. Demonstrates a bulkhead configured with
/// <c>MaxConcurrency = 2</c>, <c>MaxQueue = 1</c>, and a generous queue timeout.
/// Four operations are launched simultaneously and held open:
/// 2 are permitted to run, 1 waits in the queue, and 1 is rejected immediately
/// because the queue is full. When the running operations complete, the queued
/// call acquires a slot and finishes.
/// </summary>
public static class BulkheadScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Captured but not used for bulkhead assertions.</param>
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

        const int maxConcurrency = 2;
        const int maxQueue = 1;
        const int totalCalls = maxConcurrency + maxQueue + 1;   // 4

        // release holds every started operation open until we want it to finish.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var finished = 0;
        var rejected = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < totalCalls; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await executor.ExecuteAsync<int>(
                        policyName: "bulkhead-policy",
                        operation: async token =>
                        {
                            Interlocked.Increment(ref started);
                            await release.Task.WaitAsync(token);
                            Interlocked.Increment(ref finished);
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

        // Give the bulkhead time to admit, queue, or reject each call.
        await Task.Delay(200, ct);

        var startedDuring = Volatile.Read(ref started);
        var rejectedDuring = Volatile.Read(ref rejected);

        // At steady state: 2 running, 1 queued, 1 rejected.
        if (startedDuring != maxConcurrency)
        {
            throw new InvalidOperationException(
                $"expected {maxConcurrency} running during steady state, got {startedDuring}");
        }
        if (rejectedDuring != 1)
        {
            throw new InvalidOperationException(
                $"expected 1 immediate rejection (queue full), got {rejectedDuring}");
        }

        // Release the running operations. The queued call should acquire a slot and finish.
        release.TrySetResult(true);
        await Task.WhenAll(tasks);

        var totalStarted = Volatile.Read(ref started);
        var totalFinished = Volatile.Read(ref finished);
        var totalRejected = Volatile.Read(ref rejected);

        if (totalStarted != maxConcurrency + maxQueue)
        {
            throw new InvalidOperationException(
                $"expected {maxConcurrency + maxQueue} total starts (2 running + 1 queued), got {totalStarted}");
        }
        if (totalFinished != maxConcurrency + maxQueue)
        {
            throw new InvalidOperationException(
                $"expected {maxConcurrency + maxQueue} total finishes, got {totalFinished}");
        }
        if (totalRejected != 1)
        {
            throw new InvalidOperationException(
                $"expected 1 total rejection, got {totalRejected}");
        }

        return $"Bulkhead held {maxConcurrency} concurrent, queued {maxQueue}, " +
               $"and rejected {totalRejected} immediately. " +
               $"All admitted calls completed.";
    }
}