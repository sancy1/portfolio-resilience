// filepath: tests/Portfolio.Resilience.Tests/ServiceCollectionExtensionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies AddPortfolioResilience registers every service and honors fluent configuration.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ServiceCollectionExtensions (Extensions/ServiceCollectionExtensions.cs)
//                and ResilienceBuilder (Configuration/ResilienceBuilder.cs)
//   Depends on : Microsoft.Extensions.DependencyInjection, xUnit, FluentAssertions
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Extensions;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

    private static ServiceProvider BuildProvider(Action<ResilienceBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddPortfolioResilience_RegistersExecutor()
    {
        using var sp = BuildProvider();
        sp.GetService<IResilienceExecutor>().Should().NotBeNull();
    }

    [Fact]
    public void AddPortfolioResilience_RegistersPolicyRegistry()
    {
        using var sp = BuildProvider();
        sp.GetService<IResiliencePolicyRegistry>().Should().NotBeNull();
    }

    [Fact]
    public void AddPortfolioResilience_RegistersLatencyTracker()
    {
        using var sp = BuildProvider();
        sp.GetService<ILatencyTracker>().Should().NotBeNull();
    }

    [Fact]
    public void AddPortfolioResilience_RegistersCircuitMonitor()
    {
        using var sp = BuildProvider();
        sp.GetService<ICircuitBreakerMonitor>().Should().NotBeNull();
    }

    [Fact]
    public void AddPortfolioResilience_RegistersCorrelationAccessor()
    {
        using var sp = BuildProvider();
        sp.GetService<ICorrelationAccessor>().Should().NotBeNull();
    }
    [Fact]
    public void AddPortfolioResilience_DefaultsToNullLogSink_WhenNoSinkRegistered()
    {
        using var sp = BuildProvider();
        var sink = sp.GetRequiredService<ILogSink>();
        sink.Should().BeSameAs(NullLogSink.Instance);
    }

    [Fact]
    public void AddPortfolioResilience_UsesSingleLogSink_WhenOneRegistered()
    {
        var custom = new CapturingSink();
        using var sp = BuildProvider(r => r.AddLogSink(custom));

        sp.GetRequiredService<ILogSink>().Should().BeSameAs(custom);
    }

    [Fact]
    public void AddPortfolioResilience_ComposesMultipleLogSinks()
    {
        var a = new CapturingSink();
        var b = new CapturingSink();
        using var sp = BuildProvider(r => r.AddLogSink(a).AddLogSink(b));

        var sink = sp.GetRequiredService<ILogSink>();
        sink.Should().BeOfType<CompositeLogSink>();
    }

    [Fact]
    public void AddPortfolioResilience_RegistersPolicy()
    {
        using var sp = BuildProvider(r =>
            r.AddPolicy("test-policy", p =>
            {
                p.Timeout.TimeoutMs = 1234;
                p.Retry.MaxAttempts = 7;
            }));

        var registry = sp.GetRequiredService<IResiliencePolicyRegistry>();
        var def = registry.Resolve("test-policy");

        def.Timeout.TimeoutMs.Should().Be(1234);
        def.Retry.MaxAttempts.Should().Be(7);
    }

    [Fact]
    public async Task AddPortfolioResilience_Executor_EmitsToRegisteredSink()
    {
        var sink = new CapturingSink();
        using var sp = BuildProvider(r =>
        {
            r.AddLogSink(sink);
            r.AddPolicy("p", p =>
            {
                p.Timeout.TimeoutMs = 500;
                p.Retry.MaxAttempts = 0;
            });
        });

        var executor = sp.GetRequiredService<IResilienceExecutor>();
        await executor.ExecuteAsync("p", _ => Task.FromResult(1));

        sink.Events.Should().NotBeEmpty();
        sink.Events.Select(e => e.EventType)
            .Should().Contain(ResilienceEventType.CallSucceeded);
    }

    // ------------------------------------------------------------------------
    // ResilienceBuilder validation
    // ------------------------------------------------------------------------

    [Fact]
    public void ResilienceBuilder_AddPolicy_ReturnsSelfForChaining()
    {
        var builder = new ResilienceBuilder();
        var result = builder.AddPolicy("a", _ => { }).AddPolicy("b", _ => { });

        result.Should().BeSameAs(builder);
        builder.Options.Policies.Should().ContainKeys("a", "b");
    }

    [Fact]
    public void ResilienceBuilder_AddLogSink_ThrowsOnNull()
    {
        var builder = new ResilienceBuilder();
        Action act = () => builder.AddLogSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ResilienceBuilder_AddPolicy_ThrowsOnNullOrWhitespaceName()
    {
        var builder = new ResilienceBuilder();
        Action actNull = () => builder.AddPolicy(null!, _ => { });
        Action actEmpty = () => builder.AddPolicy("", _ => { });
        Action actWhitespace = () => builder.AddPolicy("   ", _ => { });

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResilienceBuilder_AddPolicy_ThrowsOnNullConfigure()
    {
        var builder = new ResilienceBuilder();
        Action act = () => builder.AddPolicy("p", null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
