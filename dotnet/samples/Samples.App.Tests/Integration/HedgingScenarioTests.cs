// filepath: dotnet/samples/Samples.App.Tests/Integration/HedgingScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies HedgingScenario runs end-to-end and the hedge wins the race
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.HedgingScenario
//   Used by    : dotnet test
//   See also   : Scenarios/06_HedgingScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="HedgingScenario"/>.</summary>
public sealed class HedgingScenarioTests
{
    /// <summary>The hedge beats the slow primary and the reported savings are positive.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsHedgeWin()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("hedging-disabled-policy", p => { p.Retry.MaxAttempts = 1; p.Timeout.TimeoutMs = 5000; })
            .AddPolicy("hedging-policy", p =>
            {
                p.Retry.MaxAttempts = 1;
                p.Timeout.TimeoutMs = 5000;
                p.Hedging.Enabled = true;
                p.Hedging.MaxAttempts = 2;
                p.Hedging.DelayMs = 50;
                p.Hedging.CancelOnSuccess = true;
            }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await HedgingScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("Hedging beat a 500ms slow primary");
        message.Should().Contain("saved ~");
    }
}