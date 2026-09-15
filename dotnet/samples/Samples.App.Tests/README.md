<!--
filepath: dotnet/samples/Samples.App.Tests/README.md
package:  Samples.App.Tests | since: n/a
purpose:  Test project for the Samples.App console demonstration
-->

# Samples.App.Tests

Tests for the Samples.App console project. Two categories:

- **Unit/** - pure logic: record equality, console rendering format, the fake HTTP handler. No I/O, no resilience pipeline.
- **Integration/** - runs each scenario against a real `ServiceCollection` + `IResilienceExecutor` and asserts the scenario returned a passing result.

The test project references `Samples.App.csproj` directly (via `ProjectReference`) and references the published `Portfolio.Resilience 0.8.0` package (via `PackageReference` with the same version range as the sample). It never references the library source tree.

## How to run

From the repository root:

    dotnet test dotnet\samples\Samples.App.Tests\Samples.App.Tests.csproj

From anywhere:

    dotnet test C:\Users\HP\Desktop\portfolio-resilience\dotnet\samples\Samples.App.Tests\Samples.App.Tests.csproj

Expected output ends with a line like:

    Passed!  - Failed: 0, Passed: N, Skipped: 0, Total: N, Duration: ...

Exit code 0 when all tests pass.

## Framework and packages

- xUnit 2.9.2
- FluentAssertions 6.12.1
- Microsoft.NET.Test.Sdk 17.11.1
- Portfolio.Resilience [0.8.0,1.0.0) from nuget.org

FluentAssertions is pinned at 6.x because later major versions changed licensing and free-tier behavior. For an MIT-licensed repository, 6.x is the correct professional choice.

## Test naming

    Method_Scenario_ExpectedBehavior

For example:

    RunAsync_CompletesAndReturnsSuccessMessage
    RenderScenario_Passing_RendersPassMarker
    Equality_SameValues_AreEqual

## What the tests cover

- `ScenarioResultTests` - record shape, equality, hash code, long-valued DurationMs
- `ConsoleRendererTests` - banner, per-scenario block (PASS/FAIL markers), summary block
- `FakeHttpHandlerTests` - scripted responses in order, request recording, WithStatus helper
- `RetryScenarioTests` - scenario 01 runs end-to-end and leaves the expected CallSucceeded event with Attempt = 2

Additional integration tests for scenarios 02 through 10 are being added incrementally.

## Discipline

- No .Result, no .Wait(), no Task.Delay in tests
- Every test asserts on observable behavior, not on implementation details
- Warnings are errors - `TreatWarningsAsErrors=true` is set in the csproj