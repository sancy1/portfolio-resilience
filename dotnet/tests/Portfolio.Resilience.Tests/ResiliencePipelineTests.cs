// filepath: tests/Portfolio.Resilience.Tests/ResiliencePipelineTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.7.0
// purpose: Verifies pipeline composition order, guards, and the fluent builder.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : ResiliencePipeline, ResiliencePipelineBuilder (Policies/)
//   Depends on : IResiliencePolicy, PolicyDefinition, xUnit, FluentAssertions
//   See also   : docs/composition.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Policies;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResiliencePipelineTests
{
    // ------------------------------------------------------------------------
    // Test double: records its name when executed, then calls the inner operation.
    // ------------------------------------------------------------------------

    private sealed class RecordingPolicy : IResiliencePolicy
    {
        private readonly List<string> _order;

        public RecordingPolicy(string name, List<string> order)
        {
            Name = name;
            _order = order;
        }

        public string Name { get; }

        public async Task<T> ExecuteAsync<T>(
            string policyName,
            Func<CancellationToken, Task<T>> operation,
            PolicyDefinition definition,
            CancellationToken ct = default)
        {
            _order.Add(Name);
            return await operation(ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------
    // ResiliencePipeline construction
    // ------------------------------------------------------------------------

    [Fact]
    public void Wrap_ThrowsOnNullArray()
    {
        Action act = () => ResiliencePipeline.Wrap(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Wrap_ThrowsOnEmptyArray()
    {
        Action act = () => ResiliencePipeline.Wrap();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Wrap_ThrowsWhenAnyLayerIsNull()
    {
        var order = new List<string>();
        var valid = new RecordingPolicy("valid", order);

        Action act = () => ResiliencePipeline.Wrap(valid, null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Wrap_ExposesLayersInOrder()
    {
        var order = new List<string>();
        var a = new RecordingPolicy("a", order);
        var b = new RecordingPolicy("b", order);
        var c = new RecordingPolicy("c", order);

        var pipeline = ResiliencePipeline.Wrap(a, b, c);

        pipeline.Layers.Should().HaveCount(3);
        pipeline.Layers[0].Should().BeSameAs(a);
        pipeline.Layers[1].Should().BeSameAs(b);
        pipeline.Layers[2].Should().BeSameAs(c);
    }

    // ------------------------------------------------------------------------
    // Execution — order and pass-through
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ExecutesOutermostFirst()
    {
        var order = new List<string>();
        var a = new RecordingPolicy("a", order);
        var b = new RecordingPolicy("b", order);
        var c = new RecordingPolicy("c", order);

        var pipeline = ResiliencePipeline.Wrap(a, b, c);
        var definition = new PolicyDefinition { Name = "p" };

        await pipeline.ExecuteAsync("p", _ => Task.FromResult(42), definition);

        order.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOperationResult()
    {
        var order = new List<string>();
        var pipeline = ResiliencePipeline.Wrap(new RecordingPolicy("x", order));
        var definition = new PolicyDefinition { Name = "p" };

        var result = await pipeline.ExecuteAsync("p", _ => Task.FromResult(99), definition);

        result.Should().Be(99);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOperation()
    {
        var order = new List<string>();
        var pipeline = ResiliencePipeline.Wrap(new RecordingPolicy("x", order));
        var definition = new PolicyDefinition { Name = "p" };

        Func<Task> act = () => pipeline.ExecuteAsync<int>("p", null!, definition);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullDefinition()
    {
        var order = new List<string>();
        var pipeline = ResiliencePipeline.Wrap(new RecordingPolicy("x", order));

        Func<Task> act = () => pipeline.ExecuteAsync("p", _ => Task.FromResult(1), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsOnNullOrWhitespacePolicyName()
    {
        var order = new List<string>();
        var pipeline = ResiliencePipeline.Wrap(new RecordingPolicy("x", order));
        var definition = new PolicyDefinition { Name = "p" };

        Func<Task> act = () => pipeline.ExecuteAsync("  ", _ => Task.FromResult(1), definition);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ------------------------------------------------------------------------
    // Builder
    // ------------------------------------------------------------------------

    [Fact]
    public void Builder_Add_AppendsLayer()
    {
        var order = new List<string>();
        var a = new RecordingPolicy("a", order);
        var b = new RecordingPolicy("b", order);

        var builder = new ResiliencePipelineBuilder().Add(a).Add(b);

        builder.Count.Should().Be(2);
    }

    [Fact]
    public void Builder_AddIf_FalseCondition_SkipsLayer()
    {
        var order = new List<string>();
        var a = new RecordingPolicy("a", order);
        var b = new RecordingPolicy("b", order);

        var builder = new ResiliencePipelineBuilder()
            .Add(a)
            .AddIf(false, b);

        builder.Count.Should().Be(1);
    }

    [Fact]
    public void Builder_AddIf_TrueCondition_AddsLayer()
    {
        var order = new List<string>();
        var a = new RecordingPolicy("a", order);
        var b = new RecordingPolicy("b", order);

        var builder = new ResiliencePipelineBuilder()
            .Add(a)
            .AddIf(true, b);

        builder.Count.Should().Be(2);
    }

    [Fact]
    public void Builder_WithName_StoresName()
    {
        var builder = new ResiliencePipelineBuilder().WithName("my-pipeline");
        builder.Name.Should().Be("my-pipeline");
    }

    [Fact]
    public void Builder_Build_ThrowsWhenEmpty()
    {
        var builder = new ResiliencePipelineBuilder();
        Action act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Builder_Build_ProducesPipelineInAddedOrder()
    {
        var order = new List<string>();
        var a = new RecordingPolicy("a", order);
        var b = new RecordingPolicy("b", order);
        var c = new RecordingPolicy("c", order);

        var pipeline = new ResiliencePipelineBuilder()
            .WithName("test")
            .Add(a)
            .AddIf(true, b)
            .AddIf(false, new RecordingPolicy("skipped", order))
            .Add(c)
            .Build();

        var definition = new PolicyDefinition { Name = "p" };
        await pipeline.ExecuteAsync("p", _ => Task.FromResult(1), definition);

        order.Should().Equal("a", "b", "c");
    }
    // ------------------------------------------------------------------------
    // v0.8.0 - WithPaymentSafeDefaults()
    // ------------------------------------------------------------------------

    [Fact]
    public void WithPaymentSafeDefaults_ReturnsSixLayers()
    {
        var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();

        pipeline.Layers.Should().HaveCount(6);
    }

    [Fact]
    public void WithPaymentSafeDefaults_HasSafeOrder()
    {
        var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();

        pipeline.Layers[0].Should().BeOfType<RateLimiterPolicyBuilder>();
        pipeline.Layers[1].Should().BeOfType<BulkheadPolicyBuilder>();
        pipeline.Layers[2].Should().BeOfType<CircuitPolicyBuilder>();
        pipeline.Layers[3].Should().BeOfType<HedgingPolicyBuilder>();
        pipeline.Layers[4].Should().BeOfType<RetryPolicyBuilder>();
        pipeline.Layers[5].Should().BeOfType<TimeoutPolicyBuilder>();
    }

    [Fact]
    public void WithPaymentSafeDefaults_UsesSuppliedInstances()
    {
        var supplied = new CircuitPolicyBuilder();
        var pipeline = ResiliencePipeline.WithPaymentSafeDefaults(circuit: supplied);

        pipeline.Layers[2].Should().BeSameAs(supplied);
    }

    [Fact]
    public async Task WithPaymentSafeDefaults_AllLayersDisabled_PassesThrough()
    {
        // All six layers present, all features disabled in the definition.
        // The pipeline should call the operation exactly once.
        var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();
        var calls = 0;

        var definition = new PolicyDefinition
        {
            Name = "p",
            RateLimiter = new RateLimiterOptions { Enabled = false },
            Bulkhead = new BulkheadOptions { Enabled = false },
            Hedging = new HedgingOptions { Enabled = false },
            Retry = new RetryOptions { MaxAttempts = 0 },
            Timeout = new TimeoutOptions { TimeoutMs = 0 }
        };

        var result = await pipeline.ExecuteAsync("p", _ =>
        {
            calls++;
            return Task.FromResult(42);
        }, definition);

        result.Should().Be(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task WithPaymentSafeDefaults_HedgingDisabled_DoesNotRace()
    {
        // Even though the hedging layer is in the pipeline, Enabled = false
        // means only one attempt runs. This is the core safety guarantee.
        var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();
        var calls = 0;

        var definition = new PolicyDefinition
        {
            Name = "p",
            RateLimiter = new RateLimiterOptions { Enabled = false },
            Bulkhead = new BulkheadOptions { Enabled = false },
            Hedging = new HedgingOptions { Enabled = false, MaxAttempts = 3 },
            Retry = new RetryOptions { MaxAttempts = 0 },
            Timeout = new TimeoutOptions { TimeoutMs = 0 }
        };

        await pipeline.ExecuteAsync("p", async _ =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(10);
            return "ok";
        }, definition);

        calls.Should().Be(1, "hedging is disabled; no parallel attempts should fire");
    }

    [Fact]
    public void WithPaymentSafeDefaults_NoArgsConstructor_ProducesFreshInstances()
    {
        var first = ResiliencePipeline.WithPaymentSafeDefaults();
        var second = ResiliencePipeline.WithPaymentSafeDefaults();

        first.Layers.Should().NotBeSameAs(second.Layers);
        first.Layers[0].Should().NotBeSameAs(second.Layers[0]);
    }
}
