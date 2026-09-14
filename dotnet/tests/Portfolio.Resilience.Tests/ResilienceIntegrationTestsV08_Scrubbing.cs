// filepath: tests/Portfolio.Resilience.Tests/ResilienceIntegrationTestsV08_Scrubbing.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: End-to-end integration test - executor + composite sink + scrubber mask sensitive data before any sink sees it.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : ResilienceExecutor + CompositeLogSink + DefaultPciScrubber + ResilienceEventEmitter
//   Depends on : ResiliencePolicyRegistry, InMemoryMetricSink, xUnit, FluentAssertions
//   See also   : docs/pci-scrubbing.md, SPEC.md section 19
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilienceIntegrationTestsV08_Scrubbing
{
    private sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

    // ------------------------------------------------------------------------
    // Executor + composite sink with scrubber: sensitive error messages are
    // masked on the way to the sink.
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_WithScrubbing_MasksPiiInEmittedEvents()
    {
        var capture = new CapturingSink();
        var composite = new CompositeLogSink(
            new ILogSink[] { capture },
            logger: null,
            scrubber: new DefaultPciScrubber());

        var options = new ResilienceOptions
        {
            Policies =
            {
                ["p"] = new PolicyDefinition
                {
                    Name = "p",
                    Retry = new RetryOptions { MaxAttempts = 0, BaseDelayMs = 1 },
                    Circuit = new CircuitOptions { FailureThreshold = 100 },
                    Timeout = new TimeoutOptions { TimeoutMs = 2000 }
                }
            }
        };

        var executor = new ResilienceExecutor(
            registry: new ResiliencePolicyRegistry(options),
            emitter: new ResilienceEventEmitter(composite),
            metricSink: new InMemoryMetricSink());

        Func<Task> act = () => executor.ExecuteAsync<int>(
            "p",
            _ => throw new InvalidOperationException(
                "Declined: card 4111111111111111 ssn 123-45-6789 cvv 123"));

        await act.Should().ThrowAsync<Exception>();

        // At least one event was captured, and no event carries the raw PAN/SSN/CVV.
        capture.Events.Should().NotBeEmpty();
        foreach (var evt in capture.Events)
        {
            if (evt.ErrorMessage is not null)
            {
                evt.ErrorMessage.Should().NotContain("4111111111111111");
                evt.ErrorMessage.Should().NotContain("123-45-6789");
            }
        }
    }

    // ------------------------------------------------------------------------
    // Without a scrubber, the message passes through unchanged (proof that the
    // scrubber is what's masking, not something else in the pipeline).
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_WithoutScrubbing_PassesMessageThrough()
    {
        var capture = new CapturingSink();
        var composite = new CompositeLogSink(new ILogSink[] { capture });

        var options = new ResilienceOptions
        {
            Policies =
            {
                ["p"] = new PolicyDefinition
                {
                    Name = "p",
                    Retry = new RetryOptions { MaxAttempts = 0, BaseDelayMs = 1 },
                    Circuit = new CircuitOptions { FailureThreshold = 100 },
                    Timeout = new TimeoutOptions { TimeoutMs = 2000 }
                }
            }
        };

        var executor = new ResilienceExecutor(
            registry: new ResiliencePolicyRegistry(options),
            emitter: new ResilienceEventEmitter(composite),
            metricSink: new InMemoryMetricSink());

        Func<Task> act = () => executor.ExecuteAsync<int>(
            "p",
            _ => throw new InvalidOperationException("Declined: card 4111111111111111"));

        await act.Should().ThrowAsync<Exception>();

        capture.Events.Should().NotBeEmpty();
        var failed = capture.Events.FirstOrDefault(e => e.EventType == ResilienceEventType.CallFailed);
        failed.Should().NotBeNull();
        failed!.ErrorMessage.Should().Contain("4111111111111111");
    }
}
