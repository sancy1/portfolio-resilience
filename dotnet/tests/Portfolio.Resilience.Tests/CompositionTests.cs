// filepath: tests/Portfolio.Resilience.Tests/CompositionTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.7.0
// purpose: End-to-end tests for custom pipeline composition using real policy builders.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : ResiliencePipeline, ResiliencePipelineBuilder (Policies/)
//   Depends on : RetryPolicyBuilder, TimeoutPolicyBuilder, CircuitPolicyBuilder,
//                RateLimiterPolicyBuilder, BulkheadPolicyBuilder, xUnit, FluentAssertions
//   See also   : docs/composition.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CompositionTests
{
    // A policy definition with retry/circuit neutralized so composition tests
    // focus on the layers they enable, not on retry storms.
    private static PolicyDefinition Neutral(string name = "test") => new()
    {
        Name = name,
        Retry = new RetryOptions { MaxAttempts = 0 },
        Circuit = new CircuitOptions { FailureThreshold = 100 },
        Timeout = new TimeoutOptions { TimeoutMs = 0 }
    };

    // ------------------------------------------------------------------------
    // Custom pipelines — order and execution
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Wrap_TwoLayers_ExecutesInOrder()
    {
        var pipeline = ResiliencePipeline.Wrap(
            new RateLimiterPolicyBuilder(),
            new RetryPolicyBuilder());

        var definition = Neutral();
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.Strategy = RateLimitStrategy.SlidingWindow;
        definition.RateLimiter.PermitLimit = 2;
        definition.RateLimiter.WindowSeconds = 60;

        // First call passes.
        var r1 = await pipeline.ExecuteAsync("test", _ => Task.FromResult(1), definition);
        r1.Should().Be(1);

        // Second call passes.
        var r2 = await pipeline.ExecuteAsync("test", _ => Task.FromResult(2), definition);
        r2.Should().Be(2);

        // Third call rejected by rate limiter (2 permits).
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => pipeline.ExecuteAsync("test", _ => Task.FromResult(3), definition));

        ex.Metadata!["reason"].Should().Be("rejected_immediately");
    }

    [Fact]
    public async Task Wrap_TimeoutOnly_EnforcesTimeout()
    {
        var pipeline = ResiliencePipeline.Wrap(new TimeoutPolicyBuilder());
        var definition = Neutral();
        definition.Timeout.TimeoutMs = 50;

        Func<Task> act = () => pipeline.ExecuteAsync<string>(
            "test",
            async ct =>
            {
                await Task.Delay(500, ct);
                return "never";
            },
            definition);

        var ex = await act.Should().ThrowAsync<ResilienceException>();
        ex.Which.Category.Should().Be(ResilienceErrorCategory.Timeout);
    }

    [Fact]
    public async Task Wrap_RetryWrapsTimeout_RetriesOnTimeout()
    {
        // Order: retry (outer) wraps timeout (inner). A timeout should trigger a retry.
        var pipeline = ResiliencePipeline.Wrap(
            new RetryPolicyBuilder(delayStrategy: (_, _) => Task.CompletedTask),
            new TimeoutPolicyBuilder());

        var definition = Neutral();
        definition.Retry.MaxAttempts = 1;   // 1 retry
        definition.Timeout.TimeoutMs = 50;

        var attempts = 0;
        var result = await pipeline.ExecuteAsync("test", async ct =>
        {
            attempts++;
            if (attempts == 1)
            {
                await Task.Delay(500, ct);
            }
            return "second-attempt";
        }, definition);

        result.Should().Be("second-attempt");
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task Wrap_FullPipeline_AllFiveLayersCompose()
    {
        var pipeline = ResiliencePipeline.Wrap(
            new RateLimiterPolicyBuilder(),
            new BulkheadPolicyBuilder(),
            new RetryPolicyBuilder(delayStrategy: (_, _) => Task.CompletedTask),
            new CircuitPolicyBuilder(),
            new TimeoutPolicyBuilder());

        var definition = Neutral();
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.PermitLimit = 100;
        definition.RateLimiter.WindowSeconds = 60;
        definition.Bulkhead.Enabled = true;
        definition.Bulkhead.MaxConcurrency = 10;
        definition.Bulkhead.MaxQueue = 5;
        definition.Retry.MaxAttempts = 1;

        var attempts = 0;
        var result = await pipeline.ExecuteAsync("test", _ =>
        {
            attempts++;
            if (attempts == 1)
                throw new TimeoutException("transient");
            return Task.FromResult("ok");
        }, definition);

        result.Should().Be("ok");
        attempts.Should().Be(2);
    }

    // ------------------------------------------------------------------------
    // Builder-produced pipelines
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Builder_Build_PipelineExecutesCorrectly()
    {
        var pipeline = new ResiliencePipelineBuilder()
            .WithName("custom")
            .Add(new RateLimiterPolicyBuilder())
            .Add(new RetryPolicyBuilder(delayStrategy: (_, _) => Task.CompletedTask))
            .Build();

        var definition = Neutral();
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.PermitLimit = 1;
        definition.RateLimiter.WindowSeconds = 60;

        // First call passes.
        var r1 = await pipeline.ExecuteAsync("test", _ => Task.FromResult(1), definition);
        r1.Should().Be(1);

        // Second call rejected by rate limiter.
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => pipeline.ExecuteAsync("test", _ => Task.FromResult(2), definition));

        ex.Metadata!["reason"].Should().Be("rejected_immediately");
    }

    // ------------------------------------------------------------------------
    // Layer ordering — the critical property
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Wrap_RateLimiterRejectsBeforeRetryRuns()
    {
        // Order: rate limiter (outer), retry (inner).
        // A rate-limited call must NOT be retried.
        var pipeline = ResiliencePipeline.Wrap(
            new RateLimiterPolicyBuilder(),
            new RetryPolicyBuilder(delayStrategy: (_, _) => Task.CompletedTask));

        var definition = Neutral();
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.PermitLimit = 1;
        definition.RateLimiter.WindowSeconds = 60;
        definition.Retry.MaxAttempts = 5;   // would retry 5 times if it saw the failure

        // Consume the single permit.
        _ = await pipeline.ExecuteAsync("test", _ => Task.FromResult(0), definition);

        var attempts = 0;
        var ex = await Assert.ThrowsAsync<ResilienceException>(
            () => pipeline.ExecuteAsync("test", _ =>
            {
                attempts++;
                return Task.FromResult(1);
            }, definition));

        // The operation never ran because the rate limiter rejected before retry.
        attempts.Should().Be(0);
        ex.Metadata!["reason"].Should().Be("rejected_immediately");
    }

    [Fact]
    public async Task Wrap_RetryOuterThanRateLimiter_RetryConsumesMultiplePermits()
    {
        // Inverted order: retry (outer), rate limiter (inner).
        // A retry attempt consumes a rate-limit permit each time.
        var pipeline = ResiliencePipeline.Wrap(
            new RetryPolicyBuilder(delayStrategy: (_, _) => Task.CompletedTask),
            new RateLimiterPolicyBuilder());

        var definition = Neutral();
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.PermitLimit = 2;
        definition.RateLimiter.WindowSeconds = 60;
        definition.Retry.MaxAttempts = 2;   // up to 3 total attempts

        var attempts = 0;
        Func<Task> act = () => pipeline.ExecuteAsync<int>("test", _ =>
        {
            attempts++;
            throw new TimeoutException("always fails");
        }, definition);

        // The retry loop consumes both permits, then the 3rd attempt is rate-limited
        // (RateLimitException bubbles up through retry, which does not retry CircuitOpen
        // or the transient category of a rate-limit rejection with only 2 permits).
        var ex = await act.Should().ThrowAsync<Exception>();

        // Two attempts made it past the rate limiter, the third was rejected.
        attempts.Should().Be(2);
    }

    // ------------------------------------------------------------------------
    // Fail-loud — the property we protect
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Wrap_EnabledButMissing_ThrowsAtFirstCall()
    {
        // Wrap without a rate limiter, but enable it in the definition.
        // The layer is not present, so the pipeline just does not run it.
        // This is a deliberate difference from CompositePolicyBuilder, which
        // throws for this case (fail-loud). ResiliencePipeline executes what
        // it is given; CompositePolicyBuilder validates what was configured.
        var pipeline = ResiliencePipeline.Wrap(new RetryPolicyBuilder());
        var definition = Neutral();
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.PermitLimit = 1;
        definition.RateLimiter.WindowSeconds = 60;

        // No exception: the pipeline does not know the rate limiter was desired.
        var result = await pipeline.ExecuteAsync("test", _ => Task.FromResult(1), definition);
        result.Should().Be(1);
    }

    [Fact]
    public async Task CompositePolicyBuilder_EnabledButMissing_StillThrows()
    {
        // Same scenario but through CompositePolicyBuilder, which DOES validate.
        var composite = new CompositePolicyBuilder();   // no rate limiter provided
        var definition = Neutral();
        definition.RateLimiter.Enabled = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.ExecuteAsync(_ => Task.FromResult(1), definition));

        ex.Message.Should().Contain("RateLimiterPolicyBuilder");
    }
}
