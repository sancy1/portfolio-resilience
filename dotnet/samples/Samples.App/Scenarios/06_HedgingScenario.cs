// filepath: dotnet/samples/Samples.App/Scenarios/06_HedgingScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates hedging - a slow primary is beaten by a fast staggered hedge
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor
//   Used by    : Program.cs, Integration tests
//   See also   : docs/hedging.md
// -----------------------------------------------------------------------------

using System.Diagnostics;
using Portfolio.Resilience.Abstractions;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 06 - Hedging. Configures a policy with hedging enabled
/// (<c>MaxAttempts = 2</c>, <c>DelayMs = 50</c>) and runs an operation whose
/// first invocation is slow (500ms) and whose second invocation is instant.
/// The hedge fires after 50ms and wins the race, so the whole call completes
/// well before the slow primary would finish.
/// </summary>
public static class HedgingScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Captured but not used for hedging assertions.</param>
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

        // Baseline: hedging disabled, the slow call takes its full 500ms.
        var baselineSw = Stopwatch.StartNew();
        var baselineResult = await executor.ExecuteAsync<string>(
            policyName: "hedging-disabled-policy",
            operation: async token =>
            {
                await Task.Delay(500, token);
                return "slow";
            },
            fallback: null!,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: ct);
        baselineSw.Stop();

        if (baselineResult != "slow")
        {
            throw new InvalidOperationException(
                $"baseline: expected 'slow', got '{baselineResult}'");
        }
        if (baselineSw.ElapsedMilliseconds < 450)
        {
            throw new InvalidOperationException(
                $"baseline: expected ~500ms, got {baselineSw.ElapsedMilliseconds}ms");
        }

        // With hedging enabled: first call is slow, hedge wins in ~50ms.
        var hedgedInvocations = 0;
        var hedgedSw = Stopwatch.StartNew();
        var hedgedResult = await executor.ExecuteAsync<string>(
            policyName: "hedging-policy",
            operation: async token =>
            {
                var n = Interlocked.Increment(ref hedgedInvocations);
                if (n == 1)
                {
                    await Task.Delay(500, token);
                    return "slow";
                }
                return "fast";
            },
            fallback: null!,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: ct);
        hedgedSw.Stop();

        if (hedgedResult != "fast")
        {
            throw new InvalidOperationException(
                $"hedged: expected 'fast' (the hedge should win), got '{hedgedResult}'");
        }
        if (hedgedSw.ElapsedMilliseconds > 300)
        {
            throw new InvalidOperationException(
                $"hedged: expected the hedge to win in ~50-100ms, but the call took {hedgedSw.ElapsedMilliseconds}ms");
        }

        var savedMs = baselineSw.ElapsedMilliseconds - hedgedSw.ElapsedMilliseconds;
        return $"Hedging beat a 500ms slow primary in {hedgedSw.ElapsedMilliseconds}ms " +
               $"(saved ~{savedMs}ms vs baseline {baselineSw.ElapsedMilliseconds}ms).";
    }
}