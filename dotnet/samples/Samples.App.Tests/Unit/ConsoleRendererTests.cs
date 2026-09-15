// filepath: dotnet/samples/Samples.App.Tests/Unit/ConsoleRendererTests.cs
// layer: Unit | package: Samples.App.Tests | since: n/a
// purpose: Verifies the ConsoleRenderer output format is stable
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Infrastructure.ConsoleRenderer, ScenarioResult
//   Used by    : dotnet test
//   See also   : Infrastructure/ConsoleRenderer.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Samples.App.Infrastructure;
using Xunit;

namespace Samples.App.Tests.Unit;

/// <summary>Golden-format tests for <see cref="ConsoleRenderer"/>.</summary>
public sealed class ConsoleRendererTests
{
    /// <summary>Banner contains the version and runtime lines.</summary>
    [Fact]
    public void RenderBanner_ContainsVersionAndRuntime()
    {
        using var sw = new StringWriter();
        ConsoleRenderer.RenderBanner(sw, "0.8.0", ".NET 10.0.10");
        var text = sw.ToString();

        text.Should().Contain("Samples.App");
        text.Should().Contain("Portfolio.Resilience 0.8.0");
        text.Should().Contain(".NET 10.0.10");
    }

    /// <summary>Passing scenario renders [PASS] marker and the message.</summary>
    [Fact]
    public void RenderScenario_Passing_RendersPassMarker()
    {
        using var sw = new StringWriter();
        var r = new ScenarioResult("01", "Retry", true, "everything ok", 15);
        ConsoleRenderer.RenderScenario(sw, r, 10);
        var text = sw.ToString();

        text.Should().Contain("[01/10]");
        text.Should().Contain("RETRY");
        text.Should().Contain("everything ok");
        text.Should().Contain("[PASS]");
        text.Should().NotContain("[FAIL]");
    }

    /// <summary>Failing scenario renders [FAIL] marker.</summary>
    [Fact]
    public void RenderScenario_Failing_RendersFailMarker()
    {
        using var sw = new StringWriter();
        var r = new ScenarioResult("02", "Circuit", false, "boom", 33);
        ConsoleRenderer.RenderScenario(sw, r, 10);
        var text = sw.ToString();

        text.Should().Contain("[02/10]");
        text.Should().Contain("CIRCUIT");
        text.Should().Contain("boom");
        text.Should().Contain("[FAIL]");
    }

    /// <summary>Summary with all passing reports ALL PASSED.</summary>
    [Fact]
    public void RenderSummary_AllPassing_ReportsAllPassed()
    {
        using var sw = new StringWriter();
        var list = new List<ScenarioResult>
        {
            new("01", "A", true, "ok", 10),
            new("02", "B", true, "ok", 20),
        };
        ConsoleRenderer.RenderSummary(sw, list);
        var text = sw.ToString();

        text.Should().Contain("Scenarios run:     2");
        text.Should().Contain("Passed:            2");
        text.Should().Contain("Failed:            0");
        text.Should().Contain("Total duration:    30ms");
        text.Should().Contain("ALL PASSED");
    }

    /// <summary>Summary with any failing reports FAILURES PRESENT.</summary>
    [Fact]
    public void RenderSummary_AnyFailing_ReportsFailuresPresent()
    {
        using var sw = new StringWriter();
        var list = new List<ScenarioResult>
        {
            new("01", "A", true, "ok", 10),
            new("02", "B", false, "boom", 20),
        };
        ConsoleRenderer.RenderSummary(sw, list);
        var text = sw.ToString();

        text.Should().Contain("Passed:            1");
        text.Should().Contain("Failed:            1");
        text.Should().Contain("FAILURES PRESENT");
    }
}