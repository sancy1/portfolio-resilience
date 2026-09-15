// filepath: dotnet/samples/Samples.App/Infrastructure/ScenarioResult.cs
// layer: Infrastructure | package: Samples.App | since: n/a
// purpose: Immutable outcome record produced by every scenario run
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (record)
//   Depends on : n/a
//   Used by    : ScenarioRunner, ConsoleRenderer, Program, all Integration tests
//   See also   : README.md - Console output format
// -----------------------------------------------------------------------------

namespace Samples.App.Infrastructure;

/// <summary>
/// The outcome of running one sample scenario. Immutable, self-describing, and
/// suitable for direct rendering to a terminal without further transformation.
/// </summary>
/// <param name="Number">Two-digit ordinal, e.g. "01". Used for the [01/10] prefix.</param>
/// <param name="Name">Short scenario name, e.g. "Retry". Rendered uppercase by the renderer.</param>
/// <param name="Passed">True if the scenario completed and met all its assertions.</param>
/// <param name="Message">One-line human-readable summary printed under the header.</param>
/// <param name="DurationMs">Wall-clock duration of the scenario body in milliseconds.</param>
public sealed record ScenarioResult(
    string Number,
    string Name,
    bool Passed,
    string Message,
    long DurationMs);