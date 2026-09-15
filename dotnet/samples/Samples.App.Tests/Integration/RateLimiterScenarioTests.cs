// filepath: dotnet/samples/Samples.App.Tests/Integration/RateLimiterScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies RateLimiterScenario runs end-to-end and enforces all four strategies
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.RateLimiterScenario
//   Used by    : dotnet test
//   See also   : Scenarios/04_RateLimiterScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="RateLimiterScenario"/>.</summary>
public sealed class RateLimiterScenarioTests
{
    /// <summary>All four strategies are enforced and reported.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsAllFourStrategies()
    {
        var sink = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddLogSink(sink)
            .AddPolicy("rate-limiter-token-bucket", p => { p.Retry.MaxAttempts = 1; p.RateLimiter.Enabled = true; p.RateLimiter.Strategy = RateLimitStrategy.TokenBucket; p.RateLimiter.PermitLimit = 3; p.RateLimiter.WindowSeconds = 60; })
            .AddPolicy("rate-limiter-sliding-window", p => { p.Retry.MaxAttempts = 1; p.RateLimiter.Enabled = true; p.RateLimiter.Strategy = RateLimitStrategy.SlidingWindow; p.RateLimiter.PermitLimit = 3; p.RateLimiter.WindowSeconds = 60; })
            .AddPolicy("rate-limiter-fixed-window", p => { p.Retry.MaxAttempts = 1; p.RateLimiter.Enabled = true; p.RateLimiter.Strategy = RateLimitStrategy.FixedWindow; p.RateLimiter.PermitLimit = 3; p.RateLimiter.WindowSeconds = 60; })
            .AddPolicy("rate-limiter-concurrency", p => { p.Retry.MaxAttempts = 1; p.RateLimiter.Enabled = true; p.RateLimiter.Strategy = RateLimitStrategy.ConcurrencyLimit; p.RateLimiter.PermitLimit = 3; }));
        var provider = services.BuildServiceProvider();
        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var message = await RateLimiterScenario.RunAsync(executor, sink, CancellationToken.None);

        message.Should().Contain("TokenBucket(3 ok, 2 rejected)");
        message.Should().Contain("SlidingWindow(3 ok, 2 rejected)");
        message.Should().Contain("FixedWindow(3 ok, 2 rejected)");
        message.Should().Contain("ConcurrencyLimit(3 concurrent, 2 rejected)");
    }
}