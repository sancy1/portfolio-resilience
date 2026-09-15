// filepath: dotnet/samples/Samples.App/Infrastructure/ScenarioRunner.cs
// layer: Infrastructure | package: Samples.App | since: n/a
// purpose: Runs one scenario body, times it, catches every exception, returns a ScenarioResult
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : ScenarioResult
//   Used by    : Program, all Integration tests
//   See also   : README.md - Console output format
// -----------------------------------------------------------------------------

using System.Diagnostics;

namespace Samples.App.Infrastructure;

/// <summary>
/// Runs a scenario body under a stopwatch and converts the outcome into a
/// <see cref="ScenarioResult"/>. Never rethrows: a thrown exception becomes a
/// failed result so the sample can run all scenarios and report every failure
/// instead of stopping at the first one.
/// </summary>
public static class ScenarioRunner
{
    /// <summary>Runs the scenario body, measures wall-clock duration, and returns a result.</summary>
    /// <param name="number">Two-digit ordinal, e.g. "01".</param>
    /// <param name="name">Short scenario name, e.g. "Retry".</param>
    /// <param name="body">Scenario body. On success returns a one-line message; on failure throws.</param>
    /// <param name="ct">Caller cancellation token, forwarded to the body.</param>
    /// <returns>A <see cref="ScenarioResult"/> describing the run.</returns>
    public static async Task<ScenarioResult> RunAsync(
        string number,
        string name,
        Func<CancellationToken, Task<string>> body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);

        var sw = Stopwatch.StartNew();
        try
        {
            var message = await body(ct).ConfigureAwait(false);
            sw.Stop();
            return new ScenarioResult(number, name, Passed: true, message, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var message = $"{ex.GetType().Name}: {ex.Message}";
            return new ScenarioResult(number, name, Passed: false, message, sw.ElapsedMilliseconds);
        }
    }
}