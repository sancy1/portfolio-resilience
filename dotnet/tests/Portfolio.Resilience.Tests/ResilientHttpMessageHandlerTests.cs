// filepath: tests/Portfolio.Resilience.Tests/ResilientHttpMessageHandlerTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.4.0
// purpose: Verifies the HTTP handler routes through the executor, retries on 5xx, and clones requests per attempt.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ResilientHttpMessageHandler (HttpClient/ResilientHttpMessageHandler.cs)
//   Depends on : IResilienceExecutor, HttpMessageHandler, xUnit, FluentAssertions
//   See also   : docs/http-integration.md
// ─────────────────────────────────────────────────────────────────────────────

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.HttpClient;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Policies;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilientHttpMessageHandlerTests
{
    // ------------------------------------------------------------------------
    // Fake inner handler that returns a scripted sequence of responses
    // ------------------------------------------------------------------------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public int CallCount { get; private set; }
        public List<HttpRequestMessage> ReceivedRequests { get; } = new();

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(script);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            ReceivedRequests.Add(request);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedHandler exhausted");
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private static HttpResponseMessage Ok(string content = "ok") =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static HttpResponseMessage Status(HttpStatusCode code) =>
        new(code) { Content = new StringContent($"status {(int)code}") };

    // ------------------------------------------------------------------------
    // Executor with a permissive policy for tests
    // ------------------------------------------------------------------------

    private static ResilienceExecutor BuildExecutor(
        int maxAttempts = 2,
        int timeoutMs = 2000)
    {
        var options = new ResilienceOptions
        {
            Policies =
            {
                ["http-test"] = new PolicyDefinition
                {
                    Name = "http-test",
                    Retry = new RetryOptions { MaxAttempts = maxAttempts, BaseDelayMs = 1, JitterRatio = 0.0 },
                    Circuit = new CircuitOptions { FailureThreshold = 100, OpenDurationSeconds = 30 },
                    Timeout = new TimeoutOptions { TimeoutMs = timeoutMs }
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
    // Validation
    // ------------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsOnNullExecutor()
    {
        Action act = () => new ResilientHttpMessageHandler(null!, "p");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWhitespacePolicyName()
    {
        var ex = BuildExecutor();
        Action actNull = () => new ResilientHttpMessageHandler(ex, null!);
        Action actEmpty = () => new ResilientHttpMessageHandler(ex, "");
        Action actWhitespace = () => new ResilientHttpMessageHandler(ex, "   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PolicyName_IsExposed()
    {
        var ex = BuildExecutor();
        var handler = new ResilientHttpMessageHandler(ex, "auth-service");
        handler.PolicyName.Should().Be("auth-service");
    }

    // ------------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_200_SuccessReturnsResponse()
    {
        var inner = new ScriptedHandler(_ => Ok("hello"));
        var ex = BuildExecutor();
        var handler = new ResilientHttpMessageHandler(ex, "http-test") { InnerHandler = inner };
        var http = new System.Net.Http.HttpClient(handler);

        var response = await http.GetAsync("https://example.test/api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("hello");
        inner.CallCount.Should().Be(1);
    }

    // ------------------------------------------------------------------------
    // Retry on transient HTTP failures
    // ------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_503Then200_RetriesAndSucceeds()
    {
        var inner = new ScriptedHandler(
            _ => Status(HttpStatusCode.ServiceUnavailable),
            _ => Ok("recovered"));
        var ex = BuildExecutor(maxAttempts: 2);
        var handler = new ResilientHttpMessageHandler(ex, "http-test") { InnerHandler = inner };
        var http = new System.Net.Http.HttpClient(handler);

        var response = await http.GetAsync("https://example.test/api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task SendAsync_All5xx_ThrowsResilienceException()
    {
        var inner = new ScriptedHandler(
            _ => Status(HttpStatusCode.BadGateway),
            _ => Status(HttpStatusCode.BadGateway),
            _ => Status(HttpStatusCode.BadGateway));
        var ex = BuildExecutor(maxAttempts: 1);
        var handler = new ResilientHttpMessageHandler(ex, "http-test") { InnerHandler = inner };
        var http = new System.Net.Http.HttpClient(handler);

        Func<Task> act = () => http.GetAsync("https://example.test/api");

        var exception = await act.Should().ThrowAsync<ResilienceException>();
        exception.Which.Category.Should().Be(ResilienceErrorCategory.Transient);
        inner.CallCount.Should().Be(2);  // 1 initial + 1 retry
    }

    [Fact]
    public async Task SendAsync_404_DoesNotRetry()
    {
        var inner = new ScriptedHandler(_ => Status(HttpStatusCode.NotFound));
        var ex = BuildExecutor(maxAttempts: 3);
        var handler = new ResilientHttpMessageHandler(ex, "http-test") { InnerHandler = inner };
        var http = new System.Net.Http.HttpClient(handler);

        // 404 is not transient, so no retry. But: the caller sees a 404 response,
        // not an exception, because we only call EnsureSuccessStatusCode for
        // transient HTTP failures.
        var response = await http.GetAsync("https://example.test/api");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        inner.CallCount.Should().Be(1);
    }

    // ------------------------------------------------------------------------
    // Request cloning across attempts
    // ------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_OnRetry_RequestIsRebuiltWithSameMethodAndUri()
    {
        var inner = new ScriptedHandler(
            _ => Status(HttpStatusCode.ServiceUnavailable),
            _ => Ok("ok"));
        var ex = BuildExecutor(maxAttempts: 2);
        var handler = new ResilientHttpMessageHandler(ex, "http-test") { InnerHandler = inner };
        var http = new System.Net.Http.HttpClient(handler);

        await http.GetAsync("https://example.test/path?x=1");

        inner.ReceivedRequests.Should().HaveCount(2);
        inner.ReceivedRequests[0].Method.Should().Be(HttpMethod.Get);
        inner.ReceivedRequests[0].RequestUri.Should().Be(new Uri("https://example.test/path?x=1"));
        inner.ReceivedRequests[1].RequestUri.Should().Be(new Uri("https://example.test/path?x=1"));
        inner.ReceivedRequests[0].Should().NotBeSameAs(inner.ReceivedRequests[1]);
    }

    [Fact]
    public async Task SendAsync_PostWithBody_RetainsBodyAcrossRetries()
    {
        var inner = new ScriptedHandler(
            _ => Status(HttpStatusCode.BadGateway),
            _ => Ok("ok"));
        var ex = BuildExecutor(maxAttempts: 2);
        var handler = new ResilientHttpMessageHandler(ex, "http-test") { InnerHandler = inner };
        var http = new System.Net.Http.HttpClient(handler);

        var payload = new StringContent("{\"x\":1}");
        payload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/api")
        {
            Content = payload
        };

        await http.SendAsync(request);

        inner.ReceivedRequests.Should().HaveCount(2);

        // Both attempts must have the body
        foreach (var r in inner.ReceivedRequests)
        {
            r.Content.Should().NotBeNull();
            var body = await r.Content!.ReadAsStringAsync();
            body.Should().Be("{\"x\":1}");
        }
    }
}
