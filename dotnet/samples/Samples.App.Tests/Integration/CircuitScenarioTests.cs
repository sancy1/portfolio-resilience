// filepath: dotnet/samples/Samples.App.Tests/Integration/CircuitScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies CircuitScenario runs end-to-end and returns the expected message
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.CircuitScenario, ICircuitBreakerMonitor
//   Used by    : dotnet test
//   See also   : Scenarios/02_CircuitScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="CircuitScenario"/>.</summary>
public sealed class CircuitScenarioTests
{
    /// <summary>Scenario runs to completion and reports the recovery message.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReturnsRecoveryMessage()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("circuit-policy", p =>
            {
                p.Retry.MaxAttempts = 1;
                p.Circuit.FailureThreshold = 2;
                p.Circuit.OpenDurationSeconds = 1;
                p.Circuit.SuccessThreshold = 1;
            }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();
        var monitor = provider.GetService<ICircuitBreakerMonitor>();

        var message = await CircuitScenario.RunAsync(executor, sink, monitor, CancellationToken.None);

        message.Should().Contain("opened after 2 transient failures");
        message.Should().Contain("recovered via HalfOpen probe");
    }
}