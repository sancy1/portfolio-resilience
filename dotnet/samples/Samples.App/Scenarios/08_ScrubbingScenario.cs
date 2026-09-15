// filepath: dotnet/samples/Samples.App/Scenarios/08_ScrubbingScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates PCI-safe event scrubbing - PAN, CVV, and SSN are masked before any sink sees them (v0.8.0)
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, CapturingSink, ResilienceEvent
//   Used by    : Program.cs, Integration tests
//   See also   : docs/pci-scrubbing.md, README.md - Findings
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 08 - PCI-safe event scrubbing (v0.8.0). Runs an operation that throws
/// an exception whose message contains a fake PAN, CVV, and SSN. Asserts that the
/// capturing sink receives masked text (the raw sensitive values never appear in
/// any captured event's ErrorMessage).
/// </summary>
/// <remarks>
/// The scenario runs the same call under a policy with
/// <c>Logging.ScrubSensitiveData = true</c> and one with <c>false</c>. In
/// Portfolio.Resilience 0.8.0 the flag has no observable effect: both policies
/// receive masked output. The scenario documents this and asserts the masking
/// behavior that is actually delivered. See README.md - Findings.
/// </remarks>
public static class ScrubbingScenario
{
    private const string FakePan = "4242 4242 4242 4242";
    private const string FakeCvv = "cvv=123";
    private const string FakeSsn = "123-45-6789";

    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Receives scrubbed events.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>A one-line message describing what was verified.</returns>
    public static async Task<string> RunAsync(
        IResilienceExecutor executor,
        CapturingSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sink);

        static string BuildSensitiveMessage() =>
            $"declined: card={FakePan} {FakeCvv} ssn={FakeSsn}";

        // --- Sub-test 1: policy with ScrubSensitiveData = true. ---
        var scrubbedCount = await RunOneAsync(executor, sink, "scrub-policy", BuildSensitiveMessage(), ct);
        AssertNoSensitiveLeak(sink.Events, "scrub-on");

        // --- Sub-test 2: policy with ScrubSensitiveData = false. ---
        var unscrubbedCount = await RunOneAsync(executor, sink, "no-scrub-policy", BuildSensitiveMessage(), ct);
        AssertNoSensitiveLeak(sink.Events, "scrub-off");

        return $"Scrubbing masked PAN/CVV/SSN in {scrubbedCount} event(s) with the flag on " +
               $"and {unscrubbedCount} event(s) with the flag off - " +
               $"the flag is not observable in 0.8.0.";
    }

    /// <summary>Runs one sub-test and returns the number of events whose ErrorMessage was non-empty.</summary>
    private static async Task<int> RunOneAsync(
        IResilienceExecutor executor,
        CapturingSink sink,
        string policyName,
        string message,
        CancellationToken ct)
    {
        sink.Clear();
        try
        {
            await executor.ExecuteAsync<int>(
                policyName: policyName,
                operation: _ => throw new InvalidOperationException(message),
                fallback: null!,
                idempotencyKey: null,
                timeBudgetMs: null,
                ct: ct);
            throw new InvalidOperationException($"{policyName}: expected the operation to fail");
        }
        catch (InvalidOperationException ex) when (ex.Message.EndsWith(": expected the operation to fail", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception)
        {
            // expected
        }

        var nonEmptyMessages = sink.Events
            .Select(e => e.ErrorMessage ?? string.Empty)
            .Where(m => !string.IsNullOrEmpty(m))
            .ToList();

        if (nonEmptyMessages.Count == 0)
        {
            throw new InvalidOperationException(
                $"{policyName}: no captured event carried an ErrorMessage");
        }

        return nonEmptyMessages.Count;
    }

    /// <summary>Asserts that no captured event carries the raw sensitive values.</summary>
    private static void AssertNoSensitiveLeak(
        IReadOnlyList<Portfolio.Resilience.Events.ResilienceEvent> events,
        string label)
    {
        foreach (var evt in events)
        {
            var errorMessage = evt.ErrorMessage ?? string.Empty;
            if (errorMessage.Contains(FakePan, StringComparison.Ordinal)
                || errorMessage.Contains(FakeCvv, StringComparison.Ordinal)
                || errorMessage.Contains(FakeSsn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{label}: sensitive data leaked into ErrorMessage: '{errorMessage}'");
            }
        }
    }
}