// filepath: tests/Portfolio.Resilience.Tests/HttpClientBuilderExtensionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.4.0
// purpose: Verifies AddResilientHandler attaches the handler to the HttpClient pipeline.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : HttpClientBuilderExtensions (HttpClient/HttpClientBuilderExtensions.cs)
//   Depends on : IHttpClientFactory, IResilienceExecutor, xUnit, FluentAssertions
//   See also   : docs/http-integration.md
// ─────────────────────────────────────────────────────────────────────────────

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Extensions;
using Portfolio.Resilience.HttpClient;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class HttpClientBuilderExtensionsTests
{
    // ------------------------------------------------------------------------
    // Fake inner handler — returns 200 OK always
    // ------------------------------------------------------------------------

    private sealed class OkInnerHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddPortfolioResilience(r =>
        {
            r.AddPolicy("http-test", p =>
            {
                p.Retry.MaxAttempts = 0;
                p.Timeout.TimeoutMs = 2000;
                p.Circuit.FailureThreshold = 100;
            });
        });

        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }
    [Fact]
    public void AddResilientHandler_ThrowsOnNullBuilder()
    {
        IHttpClientBuilder? builder = null;
        Action act = () => builder!.AddResilientHandler("p");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddResilientHandler_ThrowsOnNullOrWhitespacePolicyName()
    {
        using var sp = BuildProvider(services =>
            services.AddHttpClient("test", c => c.BaseAddress = new Uri("https://example.test")));

        var factory = sp.GetRequiredService<IHttpClientFactory>();

        // Can't easily get the IHttpClientBuilder back; test policy name validation via reflection is overkill.
        // Instead, verify the extension method rejects bad input by attempting to build with the fluent API directly.
        var services = new ServiceCollection();
        services.AddPortfolioResilience();
        var builder = services.AddHttpClient("test");

        Action actNull = () => builder.AddResilientHandler(null!);
        Action actEmpty = () => builder.AddResilientHandler("");
        Action actWhitespace = () => builder.AddResilientHandler("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task AddResilientHandler_AttachesHandlerToPipeline_RequestSucceeds()
    {
        using var sp = BuildProvider(services =>
            services.AddHttpClient("test", c => c.BaseAddress = new Uri("https://example.test"))
                .AddResilientHandler("http-test")
                .ConfigurePrimaryHttpMessageHandler(() => new OkInnerHandler()));

        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        var response = await http.GetAsync("/api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task AddResilientHandler_RoutesThroughExecutor_PolicyRegistered()
    {
        using var sp = BuildProvider(services =>
            services.AddHttpClient("test", c => c.BaseAddress = new Uri("https://example.test"))
                .AddResilientHandler("http-test")
                .ConfigurePrimaryHttpMessageHandler(() => new OkInnerHandler()));

        var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        await http.GetAsync("/api");

        // The executor records a metric under the policy name.
        var tracker = sp.GetRequiredService<Portfolio.Resilience.Abstractions.ILatencyTracker>();
        var snap = tracker.Get("http-test");
        snap.Should().NotBeNull();
        snap!.TotalCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void AddResilientHandler_ReturnsSameBuilderForChaining()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience();

        var httpBuilder = services.AddHttpClient("test");

        var result = httpBuilder.AddResilientHandler("p");

        result.Should().BeSameAs(httpBuilder);
    }
}
