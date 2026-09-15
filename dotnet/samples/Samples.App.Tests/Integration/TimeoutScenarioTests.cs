// filepath: dotnet/samples/Samples.App.Tests/Integration/TimeoutScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies TimeoutScenario runs end-to-end and returns the expected message
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.TimeoutScenario
//   Used by    : dotnet test
//   See also   : Scenarios/03_TimeoutScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="TimeoutScenario"/>.</summary>
public sealed class TimeoutScenarioTests
{
    /// <summary>Scenario runs to completion and reports the timeout and cancellation results.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReturnsTimeoutMessage()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("timeout-policy", p =>
            {
                p.Retry.MaxAttempts = 1;
                p.Timeout.TimeoutMs = 100;
            }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await TimeoutScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("Timeout ceiling cancelled");
        message.Should().Contain("Caller cancellation propagated");
    }
}