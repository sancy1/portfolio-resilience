// filepath: tests/Portfolio.Resilience.Tests/ResilienceExecutorTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies ResilienceExecutor pipeline, fallback handling, event emission, and metrics.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ResilienceExecutor (Implementation/ResilienceExecutor.cs)
//   Depends on : IResilienceExecutor, ResiliencePolicyRegistry, InMemoryMetricSink,
//                ResilienceEventEmitter, ResilienceException, xUnit, FluentAssertions
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Policies;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilienceExecutorTests
{
    private sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

    private static ResilienceOptions FastOptions(
        int maxAttempts = 2,
        int circuitThreshold = 5,
        int timeoutMs = 500)
    {
        return new ResilienceOptions
        {
            Policies =
            {
                ["p"] = new PolicyDefinition
                {
                    Name = "p",
                    Retry = new RetryOptions
                    {
                        MaxAttempts = maxAttempts,
                        BaseDelayMs = 1,
                        MaxDelayMs = 5,
                        JitterRatio = 0.0
                    },
                    Circuit = new CircuitOptions
                    {
                        FailureThreshold = circuitThreshold,
                        OpenDurationSeconds = 30,
                        OnlyCountTransient = true
                    },
                    Timeout = new TimeoutOptions { TimeoutMs = timeoutMs }
                }
            }
        };
    }

    private static ResilienceExecutor BuildExecutor(
        CapturingSink sink,
        InMemoryMetricSink metricSink,
        ResilienceOptions options)
    {
        var registry = new ResiliencePolicyRegistry(options);
        var emitter = new ResilienceEventEmitter(sink);
        return new ResilienceExecutor(
            registry: registry,
            emitter: emitter,
            metricSink: metricSink);
    }
    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var ex = BuildExecutor(new CapturingSink(), new InMemoryMetricSink(), FastOptions());

        Func<Task> actNull = () => ex.ExecuteAsync<int>(null!, _ => Task.FromResult(1));
        Func<Task> actEmpty = () => ex.ExecuteAsync<int>("", _ => Task.FromResult(1));
        Func<Task> actWhitespace = () => ex.ExecuteAsync<int>("   ", _ => Task.FromResult(1));

        await actNull.Should().ThrowAsync<ArgumentException>();
        await actEmpty.Should().ThrowAsync<ArgumentException>();
        await actWhitespace.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var ex = BuildExecutor(new CapturingSink(), new InMemoryMetricSink(), FastOptions());
        Func<Task> act = () => ex.ExecuteAsync<int>("p", null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_SuccessPath_ReturnsResult()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions());

        var result = await ex.ExecuteAsync("p", _ => Task.FromResult(42));

        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessPath_EmitsCallStartedAndSucceeded()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions());

        await ex.ExecuteAsync("p", _ => Task.FromResult(42));

        sink.Events.Select(e => e.EventType)
            .Should().Contain(new[]
            {
                ResilienceEventType.CallStarted,
                ResilienceEventType.CallSucceeded
            });
    }

    [Fact]
    public async Task ExecuteAsync_SuccessPath_RecordsMetric()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions());

        await ex.ExecuteAsync("p", _ => Task.FromResult(42));

        var snap = metrics.Get("p");
        snap.Should().NotBeNull();
        snap!.TotalCalls.Should().Be(1);
        snap.FailedCalls.Should().Be(0);
        snap.InFlight.Should().Be(0);
    }
    [Fact]
    public async Task ExecuteAsync_TransientFailure_RetriesAndSucceeds()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions(maxAttempts: 3));

        var attempts = 0;
        var result = await ex.ExecuteAsync("p", _ =>
        {
            attempts++;
            if (attempts < 2) throw new TimeoutException("blip");
            return Task.FromResult("ok");
        });

        result.Should().Be("ok");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustedRetries_ThrowsResilienceException()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions(maxAttempts: 1));

        Func<Task> act = () => ex.ExecuteAsync<int>("p", _ => throw new TimeoutException("permanent"));

        var exception = await act.Should().ThrowAsync<ResilienceException>();
        exception.Which.PolicyName.Should().Be("p");
        exception.Which.Category.Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public async Task ExecuteAsync_WithFallback_ReturnsFallbackValueOnFailure()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions(maxAttempts: 0));

        var result = await ex.ExecuteAsync<int>(
            "p",
            _ => throw new InvalidOperationException("nope"),
            fallback: _ => Task.FromResult(-1));

        result.Should().Be(-1);
    }

    [Fact]
    public async Task ExecuteAsync_WithFallback_EmitsFallbackUsedEvent()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions(maxAttempts: 0));

        await ex.ExecuteAsync<int>(
            "p",
            _ => throw new InvalidOperationException("nope"),
            fallback: _ => Task.FromResult(-1));

        sink.Events.Select(e => e.EventType)
            .Should().Contain(ResilienceEventType.FallbackUsed);
    }

    [Fact]
    public async Task ExecuteAsync_WithFallbackAndFailedFallback_ThrowsAggregateResilienceException()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions(maxAttempts: 0));

        Func<Task> act = () => ex.ExecuteAsync<int>(
            "p",
            _ => throw new InvalidOperationException("primary"),
            fallback: _ => throw new InvalidOperationException("fallback"));

        var exception = await act.Should().ThrowAsync<ResilienceException>();
        exception.Which.Metadata.Should().ContainKey("fallback_error");
    }
    [Fact]
    public async Task ExecuteAsync_PropagatesCorrelationIdToEvents()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions());

        using (CorrelationContext.Push("my-correlation-id"))
        {
            await ex.ExecuteAsync("p", _ => Task.FromResult(1));
        }

        sink.Events.Should().AllSatisfy(e =>
            e.CorrelationId.Should().Be("my-correlation-id"));
    }

    [Fact]
    public async Task ExecuteAsync_NonGeneric_ReturnsVoidTask()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions());

        var called = false;
        await ex.ExecuteAsync("p", _ => { called = true; return Task.CompletedTask; });

        called.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_NonGeneric_FallbackInvokedOnFailure()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions(maxAttempts: 0));

        var fallbackCalled = false;
        await ex.ExecuteAsync(
            "p",
            _ => throw new InvalidOperationException("nope"),
            fallback: _ => { fallbackCalled = true; return Task.CompletedTask; });

        fallbackCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_TracksInFlightDuringExecution()
    {
        var sink = new CapturingSink();
        var metrics = new InMemoryMetricSink();
        var ex = BuildExecutor(sink, metrics, FastOptions());

        var inFlightDuringOperation = -1;
        await ex.ExecuteAsync("p", async _ =>
        {
            inFlightDuringOperation = metrics.Get("p")?.InFlight ?? 0;
            await Task.Delay(10);
            return 0;
        });

        inFlightDuringOperation.Should().Be(1);
        metrics.Get("p")!.InFlight.Should().Be(0);
    }
}
