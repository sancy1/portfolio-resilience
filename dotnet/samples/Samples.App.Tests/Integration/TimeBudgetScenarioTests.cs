// filepath: dotnet/samples/Samples.App.Tests/Integration/TimeBudgetScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies TimeBudgetScenario runs end-to-end and reports the budget behavior
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.TimeBudgetScenario
//   Used by    : dotnet test
//   See also   : Scenarios/09_TimeBudgetScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="TimeBudgetScenario"/>.</summary>
public sealed class TimeBudgetScenarioTests
{
    /// <summary>The scenario runs and reports the budget capping the retry sequence.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsBudgetBehavior()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("budget-unbounded-policy", p =>
            {
                p.Retry.MaxAttempts = 5;
                p.Retry.BaseDelayMs = 100;
                p.Circuit.FailureThreshold = 100;
                p.Timeout.TimeoutMs = 5000;
            })
            .AddPolicy("budget-bounded-policy", p =>
            {
                p.Retry.MaxAttempts = 5;
                p.Retry.BaseDelayMs = 100;
                p.Circuit.FailureThreshold = 100;
                p.Timeout.TimeoutMs = 5000;
            }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await TimeBudgetScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("Budget capped retries at");
        message.Should().Contain("unbounded");
    }
}