// filepath: tests/Portfolio.Resilience.Tests/BackwardCompatibilityV07Tests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: Proves a v0.7.0-era call site still compiles and behaves identically under v0.8.0.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : backward compatibility of IResilienceExecutor, ResiliencePipeline,
//                HttpClientBuilderExtensions, LoggingOptions under v0.8.0 additions
//   Depends on : ResilienceExecutor, ResiliencePolicyRegistry, InMemoryMetricSink,
//                ResilienceEventEmitter, xUnit, FluentAssertions
//   See also   : CHANGELOG.md v0.8.0 Compatibility section
// -----------------------------------------------------------------------------
//
// PURPOSE
//
// Every method in this file is written EXACTLY as a v0.7.0 consumer would have
// written it. No idempotencyKey, no timeBudgetMs, no ScrubSensitiveData, no
// WithPaymentSafeDefaults. If any of these tests fail to compile or behave
// differently after v0.8.0, backward compatibility has been broken.
//
// The v0.8.0 additions are all optional parameters, new overloads, or new
// opt-in flags. This file is the executable proof of that claim.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.HttpClient;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class BackwardCompatibilityV07Tests
{
    private static ResilienceExecutor BuildV07Executor(int maxAttempts = 2)
    {
        // Exactly the shape a v0.7.0 consumer would construct.
        var options = new ResilienceOptions
        {
            Policies =
            {
                ["v07-test"] = new PolicyDefinition
                {
                    Name = "v07-test",
                    Retry = new RetryOptions { MaxAttempts = maxAttempts, BaseDelayMs = 1, JitterRatio = 0.0 },
                    Circuit = new CircuitOptions { FailureThreshold = 100, OpenDurationSeconds = 30 },
                    Timeout = new TimeoutOptions { TimeoutMs = 2000 }
                }
            }
        };

        return new ResilienceExecutor(
            registry: new ResiliencePolicyRegistry(options),
            emitter: new ResilienceEventEmitter(new NullLogSink()),
            metricSink: new InMemoryMetricSink());
    }

    // ------------------------------------------------------------------------
    // v0.7.0 ExecuteAsync call shapes
    // ------------------------------------------------------------------------

    [Fact]
    public async Task V07Executor_GenericCall_WithoutAnyV08Parameters_StillWorks()
    {
        // The exact signature a v0.7.0 consumer used.
        var executor = BuildV07Executor();

        var result = await executor.ExecuteAsync(
            "v07-test",
            ct => Task.FromResult(42));

        result.Should().Be(42);
    }

    [Fact]
    public async Task V07Executor_GenericCall_WithFallback_StillWorks()
    {
        var executor = BuildV07Executor(maxAttempts: 0);

        var result = await executor.ExecuteAsync<int>(
            "v07-test",
            _ => throw new InvalidOperationException("nope"),
            fallback: _ => Task.FromResult(-1));

        result.Should().Be(-1);
    }

    [Fact]
    public async Task V07Executor_NonGenericCall_StillWorks()
    {
        var executor = BuildV07Executor();

        var called = false;
        await executor.ExecuteAsync(
            "v07-test",
            _ => { called = true; return Task.CompletedTask; });

        called.Should().BeTrue();
    }

    [Fact]
    public async Task V07Executor_NamedCtArgument_StillWorks()
    {
        // v0.7.0 consumers often passed ct: explicitly by name.
        var executor = BuildV07Executor();
        using var cts = new CancellationTokenSource();

        var result = await executor.ExecuteAsync(
            "v07-test",
            ct => Task.FromResult("ok"),
            fallback: null,
            ct: cts.Token);

        result.Should().Be("ok");
    }

    // ------------------------------------------------------------------------
    // v0.7.0 ResiliencePipeline.Wrap call shape
    // ------------------------------------------------------------------------

    [Fact]
    public async Task V07Pipeline_WrapWithThreeLayers_StillWorks()
    {
        // A v0.7.0 consumer composing a custom pipeline.
        var pipeline = Policies.ResiliencePipeline.Wrap(
            new Policies.RetryPolicyBuilder(),
            new Policies.CircuitPolicyBuilder(),
            new Policies.TimeoutPolicyBuilder());

        var definition = new PolicyDefinition
        {
            Name = "v07-test",
            Retry = new RetryOptions { MaxAttempts = 0 },
            Circuit = new CircuitOptions { FailureThreshold = 100 },
            Timeout = new TimeoutOptions { TimeoutMs = 2000 }
        };

        var result = await pipeline.ExecuteAsync(
            "v07-test",
            _ => Task.FromResult(99),
            definition);

        result.Should().Be(99);
    }

    // ------------------------------------------------------------------------
    // v0.7.0 HttpClient handler + AddResilientHandler call shape
    // ------------------------------------------------------------------------

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Received { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }
    }

    [Fact]
    public async Task V07Handler_TwoArgConstructor_StillWorks()
    {
        // The v0.7.0 constructor: (executor, policyName). No HttpClientOptions.
        var executor = BuildV07Executor();
        var inner = new CapturingHandler();
        var handler = new ResilientHttpMessageHandler(executor, "v07-test")
        {
            InnerHandler = inner
        };
        var http = new System.Net.Http.HttpClient(handler);

        var response = await http.GetAsync("https://example.test/api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Received.Should().HaveCount(1);
        // The header is emitted only when an idempotency key is active.
        // In a v0.7.0-style call, the executor generates one from the
        // correlation ID, so the header will be present - but this test does
        // not assert on it, because that assertion belongs in the v0.8.0 tests.
    }

    // ------------------------------------------------------------------------
    // v0.7.0 LoggingOptions shape - no ScrubSensitiveData set
    // ------------------------------------------------------------------------

    [Fact]
    public void V07LoggingOptions_DefaultConstruction_ScrubbingIsOff()
    {
        // A v0.7.0 consumer who never set ScrubSensitiveData must see the
        // same default (off) as before.
        var logging = new LoggingOptions();

        logging.ScrubSensitiveData.Should().BeFalse();
        logging.EmitCallStarted.Should().BeFalse();
        logging.EmitRetryAttempted.Should().BeTrue();
        logging.EmitCallSucceeded.Should().BeTrue();
        logging.EmitCallFailed.Should().BeTrue();
        logging.EmitCircuitEvents.Should().BeTrue();
        logging.EmitFallbackUsed.Should().BeTrue();
        logging.EmitTimeoutBreached.Should().BeTrue();
        logging.EmitRateLimited.Should().BeTrue();
        logging.EmitBulkheadRejected.Should().BeTrue();
        logging.EmitHedgeEvents.Should().BeTrue();
    }

    // ------------------------------------------------------------------------
    // v0.7.0 CompositePolicyBuilder default order
    // ------------------------------------------------------------------------

    [Fact]
    public async Task V07CompositePolicyBuilder_DefaultOrder_StillExcludesHedgingWhenDisabled()
    {
        // A v0.7.0 consumer with a policy where hedging is not enabled:
        // the pipeline must still pass through cleanly with no hedging layer.
        var executor = BuildV07Executor(maxAttempts: 0);
        var calls = 0;

        await executor.ExecuteAsync(
            "v07-test",
            ct => { calls++; return Task.FromResult(1); });

        calls.Should().Be(1);
    }
}