// filepath: dotnet/samples/Samples.App.Tests/Integration/BulkheadScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies BulkheadScenario runs end-to-end with cap, queue, and rejection
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.BulkheadScenario
//   Used by    : dotnet test
//   See also   : Scenarios/05_BulkheadScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="BulkheadScenario"/>.</summary>
public sealed class BulkheadScenarioTests
{
    /// <summary>Bulkhead holds 2 concurrent, queues 1, rejects 1.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsBulkheadBehavior()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("bulkhead-policy", p =>
            {
                p.Retry.MaxAttempts = 1;
                p.Bulkhead.Enabled = true;
                p.Bulkhead.MaxConcurrency = 2;
                p.Bulkhead.MaxQueue = 1;
                p.Bulkhead.QueueTimeoutMs = 5000;
            }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await BulkheadScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("held 2 concurrent");
        message.Should().Contain("queued 1");
        message.Should().Contain("rejected 1 immediately");
    }
}