// filepath: dotnet/samples/Samples.App.Tests/Integration/ScrubbingScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies ScrubbingScenario masks PAN/CVV/SSN and reports the flag behavior
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.ScrubbingScenario
//   Used by    : dotnet test
//   See also   : Scenarios/08_ScrubbingScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="ScrubbingScenario"/>.</summary>
public sealed class ScrubbingScenarioTests
{
    /// <summary>Scrubbing masks PAN/CVV/SSN in both policy configurations.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsMasking()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("scrub-policy", p => { p.Retry.MaxAttempts = 1; p.Logging.ScrubSensitiveData = true; })
            .AddPolicy("no-scrub-policy", p => { p.Retry.MaxAttempts = 1; p.Logging.ScrubSensitiveData = false; }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await ScrubbingScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("Scrubbing masked PAN/CVV/SSN");
        message.Should().Contain("the flag is not observable in 0.8.0");
    }
}