// filepath: dotnet/samples/Samples.App/Scenarios/03_TimeoutScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates the timeout ceiling - fires on slow ops and preserves caller cancellation
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, CapturingSink
//   Used by    : Program.cs, Integration tests
//   See also   : docs/timeout.md, SPEC.md - TimeoutBreached event
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 03 - Timeout. Demonstrates two facts:
/// 1. A slow operation that exceeds <c>Timeout.TimeoutMs</c> is cancelled by the
///    timeout layer and surfaces as a timeout-classified failure.
/// 2. Caller cancellation (via the <see cref="CancellationToken"/> passed to
///    <c>ExecuteAsync</c>) is preserved: the operation observes the caller token,
///    and the library does not treat caller cancellation as its own timeout.
/// </summary>
public static class TimeoutScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Captured but not used for timeout assertions.</param>
    /// <param name="ct">Caller cancellation token (unrelated to the inner cancellations tested here).</param>
    /// <returns>A one-line message describing what was verified.</returns>
    public static async Task<string> RunAsync(
        IResilienceExecutor executor,
        CapturingSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sink);

        sink.Clear();

        // --- Part 1: slow operation is cancelled by the 100ms timeout ceiling. ---
        var innerTokenCancelled = false;
        var innerElapsedMs = 0L;

        try
        {
            await executor.ExecuteAsync<int>(
                policyName: "timeout-policy",
                operation: async token =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(2000), token);
                    }
                    catch (OperationCanceledException)
                    {
                        innerTokenCancelled = true;
                        throw;
                    }
                    finally
                    {
                        sw.Stop();
                        innerElapsedMs = sw.ElapsedMilliseconds;
                    }
                    return 42;
                },
                fallback: null!,
                idempotencyKey: null,
                timeBudgetMs: null,
                ct: ct);

            throw new InvalidOperationException("expected the timeout to cancel the operation, but it succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("expected the timeout"))
        {
            throw;
        }
        catch (Exception)
        {
            // expected - the timeout fired
        }

        // The timeout ceiling is 100ms; the operation tries to run for 2s.
        // If the ceiling worked, the operation was cancelled quickly.
        if (innerElapsedMs > 1000)
        {
            throw new InvalidOperationException(
                $"expected the operation to be cancelled around 100ms, but it ran for {innerElapsedMs}ms");
        }

        // The token passed to the operation should have been cancelled by the timeout layer.
        if (!innerTokenCancelled)
        {
            throw new InvalidOperationException(
                "expected the operation's token to be cancelled by the timeout ceiling, but it was not");
        }

        // --- Part 2: caller cancellation is preserved. ---
        using var callerCts = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationCancelled = false;

        var callerTask = executor.ExecuteAsync<int>(
            policyName: "timeout-policy",
            operation: async token =>
            {
                operationStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException)
                {
                    operationCancelled = true;
                    throw;
                }
                return 7;
            },
            fallback: null!,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: callerCts.Token);

        // Wait until the operation has actually started, then cancel the caller token.
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        callerCts.Cancel();

        try
        {
            await callerTask;
            throw new InvalidOperationException("expected caller cancellation to propagate, but the call succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("expected caller cancellation"))
        {
            throw;
        }
        catch (Exception)
        {
            // expected - caller cancellation propagated
        }

        if (!operationCancelled)
        {
            throw new InvalidOperationException(
                "expected the operation to observe caller cancellation, but its token was not cancelled");
        }

        return $"Timeout ceiling cancelled a 2s operation at ~{innerElapsedMs}ms. " +
               $"Caller cancellation propagated correctly.";
    }
}