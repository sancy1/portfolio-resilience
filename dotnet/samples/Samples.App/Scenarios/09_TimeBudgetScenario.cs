// filepath: dotnet/samples/Samples.App/Scenarios/09_TimeBudgetScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates the total wall-clock time budget capping the retry sequence (v0.8.0)
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, TimeBudgetContext
//   Used by    : Program.cs, Integration tests
//   See also   : docs/time-budget.md
// -----------------------------------------------------------------------------

using System.Diagnostics;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Correlation;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 09 - Time budget (v0.8.0). Runs the same failing operation twice:
/// once with no budget, and once with a 1500ms budget. Without a budget, the
/// retry sequence runs to exhaustion (up to 6 attempts, ~3.2s of delay). With
/// the budget, the retry layer stops early because the remaining time can no
/// longer fit the next attempt's delay - the sequence finishes in well under
/// a second. The circuit breaker is neutralized for this scenario so it does
/// not mask the budget's effect.
/// </summary>
public static class TimeBudgetScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    public static async Task<string> RunAsync(
        IResilienceExecutor executor,
        CapturingSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sink);

        sink.Clear();

        // --- Sub-test 1: no budget. Full retry sequence runs. ---
        var unboundedSw = Stopwatch.StartNew();
        var unboundedAttempts = await RunFailingAsync(executor, "budget-unbounded-policy", timeBudgetMs: null, ct);
        unboundedSw.Stop();

        // --- Sub-test 2: 1500ms budget. Retry layer stops when the budget cannot fit the next attempt. ---
        var boundedSw = Stopwatch.StartNew();
        var boundedAttempts = await RunFailingAsync(executor, "budget-bounded-policy", timeBudgetMs: 1500, ct);
        boundedSw.Stop();

        // --- Assertions: prove the budget caps the retry sequence. ---
        // The exact attempt count depends on scheduling and the retry delay
        // schedule, so we assert a shape: unbounded made multiple attempts,
        // bounded made at least one, bounded finished faster and with fewer
        // attempts than unbounded, and bounded respected its ceiling.

        if (unboundedAttempts < 3)
        {
            throw new InvalidOperationException(
                $"unbounded run should make at least 3 attempts, got {unboundedAttempts}");
        }
        if (boundedAttempts < 1)
        {
            throw new InvalidOperationException(
                $"budgeted run should make at least 1 attempt, got {boundedAttempts}");
        }
        if (boundedAttempts >= unboundedAttempts)
        {
            throw new InvalidOperationException(
                $"budget should cap the retry sequence; " +
                $"bounded={boundedAttempts}, unbounded={unboundedAttempts}");
        }
        if (boundedSw.ElapsedMilliseconds >= unboundedSw.ElapsedMilliseconds)
        {
            throw new InvalidOperationException(
                $"budgeted run should be faster; " +
                $"bounded={boundedSw.ElapsedMilliseconds}ms, unbounded={unboundedSw.ElapsedMilliseconds}ms");
        }
        if (boundedSw.ElapsedMilliseconds > 1500)
        {
            throw new InvalidOperationException(
                $"budgeted run should finish within the 1500ms budget (plus a small margin); " +
                $"got {boundedSw.ElapsedMilliseconds}ms");
        }

        // Ambient scope must be restored after the call.
        if (TimeBudgetContext.RemainingMs is not null)
        {
            throw new InvalidOperationException(
                "TimeBudgetContext.RemainingMs should be null after the budgeted call returns");
        }

        return $"Budget capped retries at {boundedAttempts} attempts / {boundedSw.ElapsedMilliseconds}ms, " +
               $"vs {unboundedAttempts} attempts / {unboundedSw.ElapsedMilliseconds}ms unbounded.";
    }

    /// <summary>Runs the failing operation under the given budget and returns the attempt count.</summary>
    private static async Task<int> RunFailingAsync(
        IResilienceExecutor executor,
        string policyName,
        int? timeBudgetMs,
        CancellationToken ct)
    {
        var attempts = 0;

        try
        {
            await executor.ExecuteAsync<int>(
                policyName: policyName,
                operation: _ =>
                {
                    attempts++;
                    throw new TimeoutException("always fails");
                },
                fallback: null!,
                idempotencyKey: null,
                timeBudgetMs: timeBudgetMs,
                ct: ct);
            throw new InvalidOperationException("expected the operation to fail");
        }
        catch (InvalidOperationException ex) when (ex.Message == "expected the operation to fail")
        {
            throw;
        }
        catch (Exception)
        {
            // expected
        }

        return attempts;
    }
}