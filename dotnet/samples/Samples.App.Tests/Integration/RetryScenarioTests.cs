// filepath: dotnet/samples/Samples.App.Tests/Integration/RetryScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies RetryScenario runs end-to-end and returns Passed=true
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.RetryScenario, IResilienceExecutor, CapturingSink
//   Used by    : dotnet test
//   See also   : Scenarios/01_RetryScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="RetryScenario"/>.</summary>
public sealed class RetryScenarioTests
{
    /// <summary>Scenario runs to completion and reports a success message.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReturnsSuccessMessage()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("retry-policy", p =>
            {
                p.Retry.MaxAttempts = 3;
                p.Retry.BaseDelayMs = 10;
            }));

        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await RetryScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("succeeded on attempt 2");
    }

    /// <summary>Scenario body leaves exactly one CallSucceeded event with Attempt=2.</summary>
    [Fact]
    public async Task RunAsync_LeavesSucceededEventWithAttemptTwo()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("retry-policy", p =>
            {
                p.Retry.MaxAttempts = 3;
                p.Retry.BaseDelayMs = 10;
            }));

        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        await RetryScenario.RunAsync(executor, sink, CancellationToken.None);

        var succeeded = sink.Events.Where(e => e.EventType.ToString() == "CallSucceeded").ToList();
        succeeded.Should().HaveCount(1);
        succeeded[0].Attempt.Should().Be(2);
    }
}