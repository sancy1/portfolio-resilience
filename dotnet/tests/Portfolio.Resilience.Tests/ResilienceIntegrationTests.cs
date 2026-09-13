// filepath: tests/Portfolio.Resilience.Tests/ResilienceIntegrationTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: End-to-end tests through the real DI container and IResilienceExecutor.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : ServiceCollectionExtensions.AddPortfolioResilience, IResilienceExecutor,
//                ConfigurationExtensions.LoadFromConfiguration
//   Depends on : Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Configuration,
//                xUnit, FluentAssertions
//   See also   : docs/executor.md, docs/rate-limiter.md, docs/bulkhead.md
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Extensions;
using Xunit;

namespace Portfolio.Resilience.Tests;

/// <summary>
/// End-to-end tests that wire the library through the real DI container the way
/// a consumer would: register via <c>AddPortfolioResilience</c>, resolve
/// <see cref="IResilienceExecutor"/>, and call it.
/// </summary>
/// <remarks>
/// These tests exist to catch bugs that unit tests cannot: missing DI
/// registrations, wrong service lifetimes, configuration-binding failures, and
/// integration gaps between the executor and the individual policy builders.
/// </remarks>
public sealed class ResilienceIntegrationTests
{
    private static ServiceProvider BuildProvider(Action<ResilienceBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(configure);
        return services.BuildServiceProvider();
    }

    // ------------------------------------------------------------------------
    // DI wiring
    // ------------------------------------------------------------------------

    [Fact]
    public void AddPortfolioResilience_RegistersAllRequiredServices()
    {
        using var provider = BuildProvider();

        provider.GetService<IResilienceExecutor>().Should().NotBeNull();
        provider.GetService<IResiliencePolicyRegistry>().Should().NotBeNull();
        provider.GetService<ILatencyTracker>().Should().NotBeNull();
        provider.GetService<ICircuitBreakerMonitor>().Should().NotBeNull();
        provider.GetService<ICorrelationAccessor>().Should().NotBeNull();
    }

    // ------------------------------------------------------------------------
    // End-to-end rate limiter
    // ------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_RateLimiter_RejectsAfterPermitExhausted()
    {
        using var provider = BuildProvider(r => r
            .AddPolicy("test", p =>
            {
                p.RateLimiter.Enabled       = true;
                p.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;
                p.RateLimiter.PermitLimit   = 2;
                p.RateLimiter.WindowSeconds = 60;

                // Neutralize retry/circuit/timeout for a deterministic test.
                p.Retry.MaxAttempts          = 0;
                p.Timeout.TimeoutMs          = 0;
                p.Circuit.FailureThreshold   = 100;
            }));

        var executor = provider.GetRequiredService<IResilienceExecutor>();

        (await executor.ExecuteAsync("test", _ => Task.FromResult(1))).Should().Be(1);
        (await executor.ExecuteAsync("test", _ => Task.FromResult(2))).Should().Be(2);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => executor.ExecuteAsync("test", _ => Task.FromResult(3)));

        ex.Metadata!["reason"].Should().Be("rejected_immediately");
        ex.Metadata!["strategy"].Should().Be("SlidingWindow");
    }

    // ------------------------------------------------------------------------
    // End-to-end bulkhead
    // ------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_Bulkhead_RejectsWhenFull()
    {
        using var provider = BuildProvider(r => r
            .AddPolicy("test", p =>
            {
                p.Bulkhead.Enabled        = true;
                p.Bulkhead.MaxConcurrency = 1;
                p.Bulkhead.MaxQueue       = 0;

                p.Retry.MaxAttempts       = 0;
                p.Timeout.TimeoutMs       = 0;
            }));

        var executor = provider.GetRequiredService<IResilienceExecutor>();

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        var first = executor.ExecuteAsync("test", async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        });

        await firstEntered.Task;

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => executor.ExecuteAsync("test", _ => Task.FromResult(2)));

        ex.Metadata!["reason"].Should().Be("rejected_immediately");
        ex.Metadata!["max_concurrency"].Should().Be(1);

        releaseFirst.SetResult();
        (await first).Should().Be(1);
    }

    // ------------------------------------------------------------------------
    // Fallback integration
    // ------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_RateLimiter_FallbackRunsOnRejection()
    {
        using var provider = BuildProvider(r => r
            .AddPolicy("test", p =>
            {
                p.RateLimiter.Enabled       = true;
                p.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;
                p.RateLimiter.PermitLimit   = 1;
                p.RateLimiter.WindowSeconds = 60;

                p.Retry.MaxAttempts    = 0;
                p.Timeout.TimeoutMs    = 0;
            }));

        var executor = provider.GetRequiredService<IResilienceExecutor>();

        // Consume the only permit.
        (await executor.ExecuteAsync("test", _ => Task.FromResult("primary"))).Should().Be("primary");

        // Second call is rate-limited; fallback runs and produces a value.
        var result = await executor.ExecuteAsync(
            "test",
            _ => Task.FromResult("primary"),
            fallback: _ => Task.FromResult("fallback"));

        result.Should().Be("fallback");
    }

    // ------------------------------------------------------------------------
    // Configuration binding
    // ------------------------------------------------------------------------

    [Fact]
    public void EndToEnd_LoadFromConfiguration_BindsRateLimiter()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Policies:test:RateLimiter:Enabled"]        = "true",
                ["Resilience:Policies:test:RateLimiter:Strategy"]       = "TokenBucket",
                ["Resilience:Policies:test:RateLimiter:PermitLimit"]    = "42",
                ["Resilience:Policies:test:RateLimiter:WindowSeconds"]  = "30",
            })
            .Build();

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        builder.Options.Policies.Should().ContainKey("test");
        var policy = builder.Options.Policies["test"];
        policy.RateLimiter.Enabled.Should().BeTrue();
        policy.RateLimiter.Strategy.Should().Be(RateLimitStrategy.TokenBucket);
        policy.RateLimiter.PermitLimit.Should().Be(42);
        policy.RateLimiter.WindowSeconds.Should().Be(30);
    }

    [Fact]
    public void EndToEnd_LoadFromConfiguration_BindsBulkhead()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Policies:test:Bulkhead:Enabled"]        = "true",
                ["Resilience:Policies:test:Bulkhead:MaxConcurrency"] = "15",
                ["Resilience:Policies:test:Bulkhead:MaxQueue"]       = "50",
            })
            .Build();

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        var policy = builder.Options.Policies["test"];
        policy.Bulkhead.Enabled.Should().BeTrue();
        policy.Bulkhead.MaxConcurrency.Should().Be(15);
        policy.Bulkhead.MaxQueue.Should().Be(50);
    }

    // ------------------------------------------------------------------------
    // Pipeline order
    // ------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_PipelineOrder_RateLimiterFiresBeforeBulkhead()
    {
        // Both features enabled on the same policy. The rate limiter has 1 permit.
        // If the rate limiter fires first, the 2nd call should fail with a
        // rate-limited reason. If bulkhead fired first, we might see a
        // bulkhead-rejection reason instead.
        using var provider = BuildProvider(r => r
            .AddPolicy("test", p =>
            {
                p.RateLimiter.Enabled       = true;
                p.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;
                p.RateLimiter.PermitLimit   = 1;
                p.RateLimiter.WindowSeconds = 60;

                p.Bulkhead.Enabled        = true;
                p.Bulkhead.MaxConcurrency = 10; // plenty of room

                p.Retry.MaxAttempts    = 0;
                p.Timeout.TimeoutMs    = 0;
            }));

        var executor = provider.GetRequiredService<IResilienceExecutor>();

        // 1st passes both.
        _ = await executor.ExecuteAsync("test", _ => Task.FromResult(1));

        // 2nd: rate limiter should reject it before bulkhead even sees it.
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => executor.ExecuteAsync("test", _ => Task.FromResult(2)));

        // Metadata carries the rate-limiter strategy — proving rate limiter fired.
        ex.Metadata!["strategy"].Should().Be("SlidingWindow");
        ex.Metadata!["reason"].Should().Be("rejected_immediately");
    }

    // ------------------------------------------------------------------------
    // Backwards compatibility
    // ------------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_DisabledFeatures_BehavesLikeV05()
    {
        // Default policy: rate limiter and bulkhead both Enabled = false.
        using var provider = BuildProvider(r => r
            .AddPolicy("test", p =>
            {
                p.Retry.MaxAttempts = 2;
                p.Retry.BaseDelayMs = 1;
                p.Retry.JitterRatio = 0.0;
                p.Timeout.TimeoutMs = 500;
            }));

        var executor = provider.GetRequiredService<IResilienceExecutor>();
        var attempts = 0;

        var result = await executor.ExecuteAsync("test", _ =>
        {
            attempts++;
            if (attempts < 2)
                throw new TimeoutException("transient");
            return Task.FromResult("ok");
        });

        result.Should().Be("ok");
        attempts.Should().Be(2);
    }
}
