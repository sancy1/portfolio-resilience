// filepath: dotnet/samples/Samples.App/Infrastructure/ConsoleRenderer.cs
// layer: Infrastructure | package: Samples.App | since: n/a
// purpose: Renders the banner, per-scenario blocks, and the summary to a TextWriter
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : ScenarioResult
//   Used by    : Program, Unit tests (ConsoleRendererTests)
//   See also   : README.md - Console output format
// -----------------------------------------------------------------------------

namespace Samples.App.Infrastructure;

/// <summary>
/// Renders the sample's terminal output. All methods take a <see cref="TextWriter"/>
/// so unit tests can capture output without redirecting <see cref="System.Console.Out"/>.
/// </summary>
public static class ConsoleRenderer
{
    private const int TotalWidth = 72;
    private const int RightAlignColumn = 60;

    /// <summary>Renders the top banner with the resolved library version and runtime version.</summary>
    /// <param name="writer">Destination writer. Must not be null.</param>
    /// <param name="libraryVersion">The Portfolio.Resilience version actually loaded at runtime.</param>
    /// <param name="runtimeVersion">The .NET runtime version.</param>
    public static void RenderBanner(TextWriter writer, string libraryVersion, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(libraryVersion);
        ArgumentNullException.ThrowIfNull(runtimeVersion);

        var line = new string('=', TotalWidth);
        writer.WriteLine(line);
        writer.WriteLine("  Samples.App - Portfolio.Resilience demonstration");
        writer.WriteLine($"  Library: Portfolio.Resilience {libraryVersion} (from nuget.org)");
        writer.WriteLine($"  .NET Runtime: {runtimeVersion}");
        writer.WriteLine(line);
    }

    /// <summary>Renders one scenario block.</summary>
    /// <param name="writer">Destination writer. Must not be null.</param>
    /// <param name="result">The scenario result to render. Must not be null.</param>
    /// <param name="totalCount">Total number of scenarios in the run.</param>
    public static void RenderScenario(TextWriter writer, ScenarioResult result, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        var prefix = $"[{result.Number}/{totalCount:D2}]  {result.Name.ToUpperInvariant()} ";
        var dashes = new string('-', Math.Max(1, TotalWidth - prefix.Length - 1));
        writer.WriteLine($"{prefix}{dashes}");

        writer.WriteLine($"         {result.Message}");

        var durationLine = $"         Duration: {result.DurationMs}ms";
        var marker = result.Passed ? "[PASS]" : "[FAIL]";
        var padding = Math.Max(1, RightAlignColumn - durationLine.Length);
        writer.WriteLine($"{durationLine}{new string(' ', padding)}{marker}");
        writer.WriteLine();
    }

    /// <summary>Renders the final summary block.</summary>
    /// <param name="writer">Destination writer. Must not be null.</param>
    /// <param name="results">All scenario results from the run. Must not be null.</param>
    public static void RenderSummary(TextWriter writer, IReadOnlyList<ScenarioResult> results)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(results);

        var total = results.Count;
        var passed = 0;
        var failed = 0;
        long totalMs = 0;
        foreach (var r in results)
        {
            if (r.Passed) passed++; else failed++;
            totalMs += r.DurationMs;
        }

        var line = new string('=', TotalWidth);
        writer.WriteLine(line);
        writer.WriteLine("  SUMMARY");
        writer.WriteLine(line);
        writer.WriteLine($"  Scenarios run:     {total}");
        writer.WriteLine($"  Passed:            {passed}");
        writer.WriteLine($"  Failed:            {failed}");
        writer.WriteLine($"  Total duration:    {totalMs}ms");
        writer.WriteLine($"  Result:            {(failed == 0 ? "ALL PASSED" : "FAILURES PRESENT")}");
        writer.WriteLine(line);
    }
}