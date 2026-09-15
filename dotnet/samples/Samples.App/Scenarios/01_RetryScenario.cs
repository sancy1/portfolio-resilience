// filepath: dotnet/samples/Samples.App/Scenarios/01_RetryScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates retry - a transient failure is retried and succeeds on attempt 2
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, CapturingSink, ResilienceEvent, ResilienceEventType
//   Used by    : Program.cs, Integration tests
//   See also   : docs/retry.md, README.md - Findings (retry_attempted not emitted in 0.8.0)
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 01 - Retry. A policy configured with <c>MaxAttempts = 3</c> executes an
/// operation that fails with a transient error on the first attempt and succeeds on
/// the second. The retry is proven by <c>CallSucceeded.Attempt == 2</c>, which only
/// happens when the retry layer actually re-invoked the operation.
/// </summary>
/// <remarks>
/// As of Portfolio.Resilience 0.8.0, a successful retry does not emit a
/// <c>RetryAttempted</c> event to registered <c>ILogSink</c> implementations even
/// though the retry runs. The scenario therefore asserts on the observable evidence
/// of the retry (the attempt counter on <c>CallSucceeded</c>) rather than on a
/// <c>RetryAttempted</c> event. See README.md - Findings.
/// </remarks>
public static class RetryScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink that received every emitted event.</param>
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

        var attemptCount = 0;

        var result = await executor.ExecuteAsync<int>(
            policyName: "retry-policy",
            operation: _ =>
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    throw new TimeoutException("simulated transient failure");
                }
                return Task.FromResult(42);
            },
            fallback: null!,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: ct);

        if (result != 42)
        {
            throw new InvalidOperationException($"expected result 42, got {result}");
        }

        if (attemptCount != 2)
        {
            throw new InvalidOperationException($"expected 2 operation invocations, got {attemptCount}");
        }

        var events = sink.Events;

        if (!events.Any(e => e.EventType == ResilienceEventType.CallStarted))
        {
            throw new InvalidOperationException("expected CallStarted event, none captured");
        }

        var succeeded = events.FirstOrDefault(e => e.EventType == ResilienceEventType.CallSucceeded);
        if (succeeded is null)
        {
            throw new InvalidOperationException("expected CallSucceeded event, none captured");
        }

        if (succeeded.Attempt != 2)
        {
            throw new InvalidOperationException(
                $"expected CallSucceeded.Attempt == 2 (proving the retry ran), got {succeeded.Attempt}");
        }

        return $"Transient error retried; succeeded on attempt 2 of 3. " +
               $"Event log: {events.Count} events captured; " +
               $"CallSucceeded.Attempt=2 proves the retry ran.";
    }
}