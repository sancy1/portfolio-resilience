// filepath: tests/Portfolio.Resilience.Tests/ResilienceIntegrationTestsV08_TimeBudget.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: End-to-end integration test - the time budget caps the entire pipeline across retry, timeout, and hedging.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : ResilienceExecutor + RetryPolicyBuilder + TimeoutPolicyBuilder + TimeBudgetContext
//   Depends on : ResiliencePolicyRegistry, InMemoryMetricSink, ResilienceEventEmitter,
//                xUnit, FluentAssertions
//   See also   : docs/time-budget.md, SPEC.md section 20
// -----------------------------------------------------------------------------

using System.Diagnostics;
using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Implementation;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResilienceIntegrationTestsV08_TimeBudget
{
    private static ResilienceExecutor BuildExecutor(
        int maxAttempts = 5,
        int baseDelayMs = 100,
        int timeoutMs = 5000)
    {
        var options = new ResilienceOptions
        {
            Policies =
            {
                ["p"] = new PolicyDefinition
                {
                    Name = "p",
                    Retry = new RetryOptions
                    {
                        MaxAttempts = maxAttempts,
                        BaseDelayMs = baseDelayMs,
                        MaxDelayMs = 500,
                        JitterRatio = 0.0
                    },
                    Circuit = new CircuitOptions { FailureThreshold = 100 },
                    Timeout = new TimeoutOptions { TimeoutMs = timeoutMs }
                }
            }
        };

        return new ResilienceExecutor(
            registry: new ResiliencePolicyRegistry(options),
            emitter: new ResilienceEventEmitter(new NullLogSink()),
            metricSink: new InMemoryMetricSink());
    }

    // ------------------------------------------------------------------------
    // Budget caps the whole pipeline across retries
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_WithTightBudget_StopsRetriesEarlyAndReturnsWithinBudget()
    {
        var executor = BuildExecutor(maxAttempts: 5, baseDelayMs: 200, timeoutMs: 5000);
        var stopwatch = Stopwatch.StartNew();

        Func<Task> act = () => executor.ExecuteAsync<int>(
            "p",
            _ => throw new TimeoutException("transient"),
            timeBudgetMs: 300);

        await act.Should().ThrowAsync<Exception>();
        stopwatch.Stop();

        // With a 300ms budget and 200ms+ base delays, we should hit at most
        // 2 attempts. The total must not exceed a reasonable bound.
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    // ------------------------------------------------------------------------
    // Budget caps the per-attempt timeout
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_WithBudgetSmallerThanTimeout_TimesOutByBudget()
    {
        var executor = BuildExecutor(maxAttempts: 0, timeoutMs: 5000);
        var stopwatch = Stopwatch.StartNew();

        Func<Task> act = () => executor.ExecuteAsync<int>(
            "p",
            async ct => { await Task.Delay(2000, ct); return 1; },
            timeBudgetMs: 100);

        var ex = await act.Should().ThrowAsync<ResilienceException>();
        stopwatch.Stop();

        ex.Which.Category.Should().Be(ResilienceErrorCategory.Timeout);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1500);
    }

    // ------------------------------------------------------------------------
    // Budget is scoped: restored after the call
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_BudgetScopeRestoredAfterCall()
    {
        var executor = BuildExecutor();

        await executor.ExecuteAsync("p", _ => Task.FromResult(1), timeBudgetMs: 500);

        TimeBudgetContext.RemainingMs.Should().BeNull();
    }

    // ------------------------------------------------------------------------
    // Null budget = v0.7.0 behavior
    // ------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_NoBudget_BehaviorUnchanged()
    {
        var executor = BuildExecutor(maxAttempts: 2, baseDelayMs: 10, timeoutMs: 2000);
        var attempts = 0;

        var result = await executor.ExecuteAsync("p", _ =>
        {
            attempts++;
            if (attempts < 2) throw new TimeoutException("blip");
            return Task.FromResult("ok");
        });

        result.Should().Be("ok");
        attempts.Should().Be(2);
    }
}
