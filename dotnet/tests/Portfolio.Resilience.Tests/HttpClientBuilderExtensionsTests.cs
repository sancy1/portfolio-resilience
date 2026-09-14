// filepath: tests/Portfolio.Resilience.Tests/HttpClientBuilderExtensionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.7.0
// purpose: Verifies AddResilientHandler and AddStandardResilienceHandler wiring.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : HttpClientBuilderExtensions (HttpClient/)
//   Depends on : ServiceCollection, HttpClient, StandardPolicy, xUnit, FluentAssertions
//   See also   : docs/http-integration.md, SPEC.md section 3
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Extensions;
using Portfolio.Resilience.HttpClient;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class HttpClientBuilderExtensionsTests
{
    // A no-op primary handler so HttpClient does not attempt real network I/O.
    private sealed class NoopHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("ok")
            });
        }
    }

    // ------------------------------------------------------------------------
    // AddResilientHandler — existing tests, preserved
    // ------------------------------------------------------------------------

    [Fact]
    public void AddResilientHandler_ThrowsOnNullBuilder()
    {
        Microsoft.Extensions.DependencyInjection.IHttpClientBuilder? builder = null;
        Action act = () => Portfolio.Resilience.HttpClient.HttpClientBuilderExtensions.AddResilientHandler(builder!, "p");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddResilientHandler_ThrowsOnNullOrWhitespacePolicyName()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience();
        var builder = services.AddHttpClient("test");

        Action actNull = () => builder.AddResilientHandler(null!);
        Action actEmpty = () => builder.AddResilientHandler("  ");

        actNull.Should().Throw<ArgumentNullException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task AddResilientHandler_AttachesHandlerToPipeline_RequestSucceeds()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddPolicy("test", p =>
            {
                p.Retry.MaxAttempts = 0;
                p.Timeout.TimeoutMs = 0;
                p.Circuit.FailureThreshold = 100;
            }));

        services
            .AddHttpClient("test")
            .AddResilientHandler("test")
            .ConfigurePrimaryHttpMessageHandler(() => new NoopHandler());

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = factory.CreateClient("test");

        var response = await client.GetAsync("http://test.local/");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddResilientHandler_RoutesThroughExecutor_PolicyRegistered()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddPolicy("my-policy", p =>
            {
                p.Retry.MaxAttempts = 0;
                p.Timeout.TimeoutMs = 0;
                p.Circuit.FailureThreshold = 100;
            }));

        services
            .AddHttpClient("test")
            .AddResilientHandler("my-policy")
            .ConfigurePrimaryHttpMessageHandler(() => new NoopHandler());

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IResiliencePolicyRegistry>();
        registry.KnownPolicies.Should().Contain("my-policy");

        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = factory.CreateClient("test");
        var response = await client.GetAsync("http://test.local/");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public void AddResilientHandler_ReturnsSameBuilderForChaining()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience();
        var builder = services.AddHttpClient("test");

        var result = builder.AddResilientHandler("test");

        result.Should().BeSameAs(builder);
    }

    // ------------------------------------------------------------------------
    // AddStandardResilienceHandler — new tests
    // ------------------------------------------------------------------------

    [Fact]
    public void AddStandardResilienceHandler_NoExistingStandardPolicy_RegistersDefault()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience();
        services.AddHttpClient("test").AddStandardResilienceHandler();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IResiliencePolicyRegistry>();

        var policy = registry.Resolve(StandardPolicy.Name);

        policy.Name.Should().Be(StandardPolicy.Name);
        policy.Retry.MaxAttempts.Should().Be(3);
        policy.Retry.BaseDelayMs.Should().Be(100);
        policy.Circuit.FailureThreshold.Should().Be(5);
        policy.Timeout.TimeoutMs.Should().Be(30_000);
        policy.RateLimiter.Enabled.Should().BeFalse();
        policy.Bulkhead.Enabled.Should().BeFalse();
        policy.Hedging.Enabled.Should().BeFalse();
    }

    [Fact]
    public void AddStandardResilienceHandler_UserStandardPolicyExists_UserWins()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddPolicy(StandardPolicy.Name, p =>
            {
                p.Retry.MaxAttempts = 99;
                p.Timeout.TimeoutMs = 1_234;
            }));

        services.AddHttpClient("test").AddStandardResilienceHandler();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IResiliencePolicyRegistry>();
        var policy = registry.Resolve(StandardPolicy.Name);

        policy.Retry.MaxAttempts.Should().Be(99);
        policy.Timeout.TimeoutMs.Should().Be(1_234);
    }

    [Fact]
    public void AddStandardResilienceHandler_WithConfigure_AppliesConfiguration()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience();

        services.AddHttpClient("test").AddStandardResilienceHandler(p =>
        {
            p.Retry.MaxAttempts = 5;
            p.Timeout.TimeoutMs = 10_000;
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IResiliencePolicyRegistry>();
        var policy = registry.Resolve(StandardPolicy.Name);

        policy.Retry.MaxAttempts.Should().Be(5);
        policy.Timeout.TimeoutMs.Should().Be(10_000);
    }

    [Fact]
    public void AddStandardResilienceHandler_ConfigureIgnoredWhenUserPolicyExists()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r => r
            .AddPolicy(StandardPolicy.Name, p =>
            {
                p.Retry.MaxAttempts = 2;
            }));

        // Even with a configure callback, the user's policy wins and configure is skipped.
        services.AddHttpClient("test").AddStandardResilienceHandler(p =>
        {
            p.Retry.MaxAttempts = 99;
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IResiliencePolicyRegistry>();
        var policy = registry.Resolve(StandardPolicy.Name);

        policy.Retry.MaxAttempts.Should().Be(2);
    }

    [Fact]
    public async Task AddStandardResilienceHandler_ResolvesHttpClient_WithStandardPolicyHandler()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience();

        services
            .AddHttpClient("test")
            .AddStandardResilienceHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new NoopHandler());

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = factory.CreateClient("test");

        var response = await client.GetAsync("http://test.local/");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [Fact]
    public void AddStandardResilienceHandler_WorksWithAnyCallOrder()
    {
        // Register the standard handler BEFORE AddPortfolioResilience. This
        // exercises the lazy IEnumerable<Action<ResilienceOptions>> resolution.
        var services = new ServiceCollection();

        services.AddHttpClient("test").AddStandardResilienceHandler();

        services.AddPortfolioResilience();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IResiliencePolicyRegistry>();
        var policy = registry.Resolve(StandardPolicy.Name);

        policy.Retry.MaxAttempts.Should().Be(3);
        policy.Timeout.TimeoutMs.Should().Be(30_000);
    }
}
