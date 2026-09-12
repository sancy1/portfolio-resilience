// filepath: tests/Portfolio.Resilience.Tests/ResilienceExceptionMiddlewareBaseTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies the middleware base catches ResilienceException, sets correlation header, and delegates render.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ResilienceExceptionMiddlewareBase (Middleware/ResilienceExceptionMiddlewareBase.cs)
//   Depends on : HttpContext, ResilienceException, xUnit, FluentAssertions
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Middleware;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilienceExceptionMiddlewareBaseTests
{
    private sealed class TestMiddleware : ResilienceExceptionMiddlewareBase
    {
        public ResilienceException? CapturedException { get; private set; }
        public int RenderCallCount { get; private set; }
        public int RenderStatusCode { get; set; } = StatusCodes.Status503ServiceUnavailable;

        public TestMiddleware(RequestDelegate next) : base(next, NullLogger.Instance) { }

        protected override async Task RenderErrorAsync(HttpContext context, ResilienceException exception)
        {
            RenderCallCount++;
            CapturedException = exception;
            context.Response.StatusCode = RenderStatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"{{\"error\":\"{exception.Category}\"}}");
        }
    }

    private static ResilienceException SampleException(
        ResilienceErrorCategory category = ResilienceErrorCategory.Transient,
        string? correlationId = "corr-abc-123")
    {
        return new ResilienceException(
            message: "upstream failed",
            policyName: "auth-service",
            category: category,
            attemptsMade: 3,
            totalDuration: TimeSpan.FromMilliseconds(150),
            correlationId: correlationId);
    }

    [Fact]
    public void Constructor_ThrowsOnNullNext()
    {
        Action act = () => new TestMiddleware(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task InvokeAsync_ThrowsOnNullContext()
    {
        var middleware = new TestMiddleware(_ => Task.CompletedTask);
        Func<Task> act = () => middleware.InvokeAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
    [Fact]
    public async Task InvokeAsync_NoException_PassesThrough()
    {
        var called = false;
        var middleware = new TestMiddleware(_ => { called = true; return Task.CompletedTask; });

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        middleware.RenderCallCount.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_NonResilienceException_Propagates()
    {
        var middleware = new TestMiddleware(_ => throw new InvalidOperationException("not ours"));

        var context = new DefaultHttpContext();
        Func<Task> act = () => middleware.InvokeAsync(context);

        await act.Should().ThrowAsync<InvalidOperationException>();
        middleware.RenderCallCount.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_ResilienceException_CallsRender()
    {
        var middleware = new TestMiddleware(_ => throw SampleException());

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        middleware.RenderCallCount.Should().Be(1);
        middleware.CapturedException.Should().NotBeNull();
        middleware.CapturedException!.PolicyName.Should().Be("auth-service");
    }

    [Fact]
    public async Task InvokeAsync_ResilienceException_SetsCorrelationHeader()
    {
        var middleware = new TestMiddleware(_ => throw SampleException(correlationId: "abc-xyz"));

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("abc-xyz");
    }

    [Fact]
    public async Task InvokeAsync_NoCorrelationIdInException_FallsBackToAmbient()
    {
        // Exception with no correlationId; ambient has one.
        var ex = SampleException(correlationId: null);
        var middleware = new TestMiddleware(_ => throw ex);

        var context = new DefaultHttpContext();

        using (CorrelationContext.Push("ambient-corr-456"))
        {
            await middleware.InvokeAsync(context);
        }

        context.Response.Headers["X-Correlation-Id"].ToString().Should().Be("ambient-corr-456");
    }

    [Fact]
    public async Task InvokeAsync_NoCorrelationAnywhere_DoesNotSetHeader()
    {
        var ex = SampleException(correlationId: null);
        var middleware = new TestMiddleware(_ => throw ex);

        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        context.Response.Headers.ContainsKey("X-Correlation-Id").Should().BeFalse();
    }

    // NOTE: The "response already started" branch (Response.HasStarted == true)
    // is intentionally NOT covered by a unit test. DefaultHttpContext does not
    // reliably signal HasStarted after StartAsync() without a real connection,
    // so this branch is covered by integration tests in the consuming service
    // (Stage H). The production logic is:
    //
    //     if (context.Response.HasStarted) throw;
    //
    // — see ResilienceExceptionMiddlewareBase.InvokeAsync.
}
