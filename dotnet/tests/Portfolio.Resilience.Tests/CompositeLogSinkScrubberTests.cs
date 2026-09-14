// filepath: tests/Portfolio.Resilience.Tests/CompositeLogSinkScrubberTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: Verifies CompositeLogSink runs the scrubber once before any child, and DI wiring respects ScrubSensitiveData.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : CompositeLogSink + DefaultPciScrubber + ServiceCollectionExtensions
//   Depends on : ILogSink, IEventScrubber, ResilienceEvent, xUnit, FluentAssertions
//   See also   : docs/pci-scrubbing.md, SPEC.md section 19
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Extensions;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CompositeLogSinkScrubberTests
{
    private sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

    private static ResilienceEvent FailedEvent(string message) => new()
    {
        EventType = ResilienceEventType.CallFailed,
        PolicyName = "p",
        ErrorMessage = message
    };

    // ------------------------------------------------------------------------
    // CompositeLogSink with scrubber
    // ------------------------------------------------------------------------

    [Fact]
    public void Emit_WithScrubber_MasksBeforeAnyChildSeesEvent()
    {
        var child1 = new CapturingSink();
        var child2 = new CapturingSink();
        var composite = new CompositeLogSink(
            new ILogSink[] { child1, child2 },
            logger: null,
            scrubber: new DefaultPciScrubber());

        composite.Emit(FailedEvent("card 4111111111111111 declined"));

        child1.Events[0].ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
        child2.Events[0].ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
        child1.Events[0].ErrorMessage.Should().NotContain("4111111111111111");
    }

    [Fact]
    public void Emit_WithoutScrubber_PassesEventUnchanged()
    {
        var child = new CapturingSink();
        var composite = new CompositeLogSink(new ILogSink[] { child });

        composite.Emit(FailedEvent("card 4111111111111111 declined"));

        child.Events[0].ErrorMessage.Should().Be("card 4111111111111111 declined");
    }

    [Fact]
    public void HasScrubber_ReflectsConstruction()
    {
        new CompositeLogSink(new ILogSink[] { new CapturingSink() }).HasScrubber.Should().BeFalse();
        new CompositeLogSink(new ILogSink[] { new CapturingSink() }, null, new DefaultPciScrubber())
            .HasScrubber.Should().BeTrue();
    }

    // ------------------------------------------------------------------------
    // ResilienceOptionsExtensions
    // ------------------------------------------------------------------------

    [Fact]
    public void AnyPolicyScrubsSensitiveData_NoPolicies_ReturnsFalse()
    {
        var options = new ResilienceOptions();

        options.AnyPolicyScrubsSensitiveData().Should().BeFalse();
    }

    [Fact]
    public void AnyPolicyScrubsSensitiveData_DefaultPolicyOptedIn_ReturnsTrue()
    {
        var options = new ResilienceOptions();
        options.DefaultPolicy.Logging.ScrubSensitiveData = true;

        options.AnyPolicyScrubsSensitiveData().Should().BeTrue();
    }

    [Fact]
    public void AnyPolicyScrubsSensitiveData_NamedPolicyOptedIn_ReturnsTrue()
    {
        var options = new ResilienceOptions();
        options.Policies["p"] = new PolicyDefinition { Name = "p" };
        options.Policies["p"].Logging.ScrubSensitiveData = true;

        options.AnyPolicyScrubsSensitiveData().Should().BeTrue();
    }

    // ------------------------------------------------------------------------
    // DI wiring respects ScrubSensitiveData
    // ------------------------------------------------------------------------

    [Fact]
    public void DI_ScrubSensitiveDataFalse_SingleChildNotWrapped()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r =>
        {
            r.AddLogSink(new CapturingSink());
        });

        using var sp = services.BuildServiceProvider();
        var sink = sp.GetRequiredService<ILogSink>();

        sink.Should().BeOfType<CapturingSink>();
    }

    [Fact]
    public void DI_ScrubSensitiveDataTrue_SingleChildWrappedInCompositeWithScrubber()
    {
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r =>
        {
            r.AddLogSink(new CapturingSink());
            r.AddPolicy("p", p =>
            {
                p.Logging.ScrubSensitiveData = true;
            });
        });

        using var sp = services.BuildServiceProvider();
        var sink = sp.GetRequiredService<ILogSink>();

        sink.Should().BeOfType<CompositeLogSink>();
        ((CompositeLogSink)sink).HasScrubber.Should().BeTrue();
    }

    [Fact]
    public void DI_ScrubSensitiveDataTrue_EventsAreScrubbedBeforeReachingChild()
    {
        var capture = new CapturingSink();
        var services = new ServiceCollection();
        services.AddPortfolioResilience(r =>
        {
            r.AddLogSink(capture);
            r.AddPolicy("p", p =>
            {
                p.Logging.ScrubSensitiveData = true;
            });
        });

        using var sp = services.BuildServiceProvider();
        var sink = sp.GetRequiredService<ILogSink>();

        sink.Emit(FailedEvent("card 4111111111111111 declined"));

        capture.Events[0].ErrorMessage.Should().NotContain("4111111111111111");
        capture.Events[0].ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
    }
}
