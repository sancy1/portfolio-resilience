// filepath: dotnet/samples/Samples.App/Scenarios/07_IdempotencyScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates idempotency key propagation - same key across all retry attempts (v0.8.0)
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, IdempotencyContext
//   Used by    : Program.cs, Integration tests
//   See also   : docs/idempotency.md
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Correlation;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 07 - Idempotency key propagation (v0.8.0). Demonstrates that the
/// same idempotency key reaches every retry attempt of the same logical call:
/// 1. When the caller supplies an explicit key, that exact key is observed on
///    every invocation via <see cref="IdempotencyContext.CurrentKey"/>.
/// 2. When the caller passes null, the executor derives a key from the ambient
///    correlation ID (or a fresh GUID), and the same derived key is observed on
///    every invocation.
/// </summary>
public static class IdempotencyScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Captured but not used for idempotency assertions.</param>
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

        // --- Sub-test 1: explicit idempotency key is preserved across retries. ---
        const string explicitKey = "charge-order-42";
        var explicitKeysSeen = new List<string?>();
        var explicitAttempts = 0;

        await executor.ExecuteAsync<int>(
            policyName: "idempotency-policy",
            operation: _ =>
            {
                explicitKeysSeen.Add(IdempotencyContext.CurrentKey);
                explicitAttempts++;
                if (explicitAttempts == 1)
                {
                    throw new TimeoutException("simulated transient failure");
                }
                return Task.FromResult(1);
            },
            fallback: null!,
            idempotencyKey: explicitKey,
            timeBudgetMs: null,
            ct: ct);

        if (explicitAttempts != 2)
        {
            throw new InvalidOperationException(
                $"explicit: expected 2 attempts, got {explicitAttempts}");
        }
        if (explicitKeysSeen.Count != 2)
        {
            throw new InvalidOperationException(
                $"explicit: expected 2 key observations, got {explicitKeysSeen.Count}");
        }
        if (explicitKeysSeen[0] != explicitKey || explicitKeysSeen[1] != explicitKey)
        {
            throw new InvalidOperationException(
                $"explicit: expected both attempts to see '{explicitKey}', " +
                $"got '{explicitKeysSeen[0]}' and '{explicitKeysSeen[1]}'");
        }

        // --- Sub-test 2: null key is auto-derived and stable across retries. ---
        var autoKeysSeen = new List<string?>();
        var autoAttempts = 0;

        await executor.ExecuteAsync<int>(
            policyName: "idempotency-policy",
            operation: _ =>
            {
                autoKeysSeen.Add(IdempotencyContext.CurrentKey);
                autoAttempts++;
                if (autoAttempts == 1)
                {
                    throw new TimeoutException("simulated transient failure");
                }
                return Task.FromResult(2);
            },
            fallback: null!,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: ct);

        if (autoAttempts != 2)
        {
            throw new InvalidOperationException(
                $"auto: expected 2 attempts, got {autoAttempts}");
        }
        if (autoKeysSeen.Count != 2)
        {
            throw new InvalidOperationException(
                $"auto: expected 2 key observations, got {autoKeysSeen.Count}");
        }
        if (string.IsNullOrWhiteSpace(autoKeysSeen[0]))
        {
            throw new InvalidOperationException(
                "auto: expected a non-empty derived key on attempt 1, got null/empty");
        }
        if (autoKeysSeen[0] != autoKeysSeen[1])
        {
            throw new InvalidOperationException(
                $"auto: expected the same derived key on both attempts, " +
                $"got '{autoKeysSeen[0]}' and '{autoKeysSeen[1]}'");
        }

        return $"Explicit key '{explicitKey}' preserved on both attempts. " +
               $"Null key auto-derived to '{Truncate(autoKeysSeen[0])}' and preserved across attempts.";
    }

    /// <summary>Shortens a key for display in the scenario message.</summary>
    private static string Truncate(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";
        return key.Length <= 16 ? key : key.Substring(0, 16) + "...";
    }
}