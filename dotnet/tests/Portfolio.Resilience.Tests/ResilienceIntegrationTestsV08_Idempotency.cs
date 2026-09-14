// filepath: tests/Portfolio.Resilience.Tests/ResilienceIntegrationTestsV08_Idempotency.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: End-to-end integration test - executor + HTTP handler + retry pipeline all propagate the same idempotency key.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : ResilienceExecutor + ResilientHttpMessageHandler + IdempotencyContext
//   Depends on : ResiliencePolicyRegistry, InMemoryMetricSink, ResilienceEventEmitter,
//                HttpMessageHandler, xUnit, FluentAssertions
//   See also   : docs/idempotency.md, docs/http-integration.md, SPEC.md section 18
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.HttpClient;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilienceIntegrationTestsV08_Idempotency
{
    // ------------------------------------------------------------------------
    // Scripted inner handler: returns 503 once, then 200.
    // ------------------------------------------------------------------------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Received { get; } = new();

        public ScriptedHandler(params HttpResponseMessage[] script)
        {
            _responses = new Queue<HttpResponseMessage>(script);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent($"status {(int)code}") };

    private static ResilienceExecutor BuildExecutor(int maxAttempts)
    {
        var options = new ResilienceOptions
        {
            Policies =
            {
                ["stripe-charge"] = new PolicyDefinition
                {
                    Name = "stripe-charge",
                    Retry = new RetryOptions { MaxAttempts = maxAttempts, BaseDelayMs = 1, JitterRatio = 0.0 },
                    Circuit = new CircuitOptions { FailureThreshold = 100, OpenDurationSeconds = 30 },
                    Timeout = new TimeoutOptions { TimeoutMs = 2000 }
                }
            }
        };

        var registry = new ResiliencePolicyRegistry(options);
        return new ResilienceExecutor(
            registry: registry,
            emitter: new ResilienceEventEmitter(new NullLogSink()),
            metricSink: new InMemoryMetricSink());
    }

    // ------------------------------------------------------------------------
    // End-to-end: retry on 503, both attempts carry the same caller-supplied key
    // ------------------------------------------------------------------------

    [Fact]
    public async Task HttpHandler_WithRetry_CarriesSameIdempotencyKeyOnEveryAttempt()
    {
        var inner = new ScriptedHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.OK));
        var executor = BuildExecutor(maxAttempts: 2);
        var handler = new ResilientHttpMessageHandler(executor, "stripe-charge")
        {
            InnerHandler = inner
        };
        var http = new System.Net.Http.HttpClient(handler);

        var response = await executor.ExecuteAsync(
            "stripe-charge",
            async ct =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://psp.example.test/charge")
                {
                    Content = new StringContent("{\"amount\":100}")
                };
                return await http.SendAsync(req, ct);
            },
            idempotencyKey: "order-12345");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Received.Should().HaveCount(2);

        foreach (var req in inner.Received)
        {
            req.Headers.TryGetValues("Idempotency-Key", out var values).Should().BeTrue();
            values!.First().Should().Be("order-12345");
        }
    }

    // ------------------------------------------------------------------------
    // Ambient key is honored end-to-end when set before ExecuteAsync
    // ------------------------------------------------------------------------

    [Fact]
    public async Task HttpHandler_WithAmbientKey_PreservesAmbientThroughPipeline()
    {
        var inner = new ScriptedHandler(Status(HttpStatusCode.OK));
        var executor = BuildExecutor(maxAttempts: 0);
        var handler = new ResilientHttpMessageHandler(executor, "stripe-charge")
        {
            InnerHandler = inner
        };
        var http = new System.Net.Http.HttpClient(handler);

        using (IdempotencyContext.Push("ambient-key-777"))
        {
            await executor.ExecuteAsync(
                "stripe-charge",
                ct => http.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "https://psp.example.test/charge"),
                    ct));
        }

        inner.Received[0].Headers.TryGetValues("Idempotency-Key", out var values).Should().BeTrue();
        values!.First().Should().Be("ambient-key-777");
    }

    // ------------------------------------------------------------------------
    // Correlation-derived fallback key propagates end-to-end
    // ------------------------------------------------------------------------

    [Fact]
    public async Task HttpHandler_WithoutKey_DerivesFromCorrelationEndToEnd()
    {
        var inner = new ScriptedHandler(Status(HttpStatusCode.OK));
        var executor = BuildExecutor(maxAttempts: 0);
        var handler = new ResilientHttpMessageHandler(executor, "stripe-charge")
        {
            InnerHandler = inner
        };
        var http = new System.Net.Http.HttpClient(handler);

        using (CorrelationContext.Push("req-abc"))
        {
            await executor.ExecuteAsync(
                "stripe-charge",
                ct => http.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "https://psp.example.test/charge"),
                    ct));
        }

        inner.Received[0].Headers.TryGetValues("Idempotency-Key", out var values).Should().BeTrue();
        values!.First().Should().Be("idem-req-abc");
    }

    // ------------------------------------------------------------------------
    // Custom header name propagates through the full pipeline
    // ------------------------------------------------------------------------

    [Fact]
    public async Task HttpHandler_WithCustomHeaderName_UsesItEndToEnd()
    {
        var inner = new ScriptedHandler(Status(HttpStatusCode.OK));
        var executor = BuildExecutor(maxAttempts: 0);
        var options = new HttpClientOptions { IdempotencyHeaderName = "X-Idempotency" };
        var handler = new ResilientHttpMessageHandler(executor, "stripe-charge", options)
        {
            InnerHandler = inner
        };
        var http = new System.Net.Http.HttpClient(handler);

        await executor.ExecuteAsync(
            "stripe-charge",
            ct => http.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "https://psp.example.test/charge"),
                ct),
            idempotencyKey: "custom-key-42");

        var req = inner.Received[0];
        req.Headers.TryGetValues("X-Idempotency", out var values).Should().BeTrue();
        values!.First().Should().Be("custom-key-42");
        req.Headers.Contains("Idempotency-Key").Should().BeFalse();
    }
}
