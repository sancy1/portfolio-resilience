// filepath: dotnet/samples/Samples.App.Tests/Integration/IdempotencyScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies IdempotencyScenario preserves keys across retries
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.IdempotencyScenario
//   Used by    : dotnet test
//   See also   : Scenarios/07_IdempotencyScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="IdempotencyScenario"/>.</summary>
public sealed class IdempotencyScenarioTests
{
    /// <summary>Explicit and auto-derived keys are both preserved across attempts.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsKeyPropagation()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("idempotency-policy", p =>
            {
                p.Retry.MaxAttempts = 3;
                p.Retry.BaseDelayMs = 10;
            }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await IdempotencyScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("Explicit key 'charge-order-42'");
        message.Should().Contain("auto-derived");
        message.Should().Contain("preserved across attempts");
    }
}