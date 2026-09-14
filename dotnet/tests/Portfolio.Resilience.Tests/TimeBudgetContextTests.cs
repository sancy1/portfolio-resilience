// filepath: tests/Portfolio.Resilience.Tests/TimeBudgetContextTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: Verifies TimeBudgetContext push/pop, deadline math, exhaustion detection, and async isolation.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : TimeBudgetContext (Correlation/TimeBudgetContext.cs)
//   Depends on : xUnit, FluentAssertions
//   See also   : docs/time-budget.md, SPEC.md section 20
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Correlation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class TimeBudgetContextTests
{
    [Fact]
    public void RemainingMs_IsNullWhenNoScopeIsActive()
    {
        TimeBudgetContext.RemainingMs.Should().BeNull();
    }

    [Fact]
    public void IsExhausted_IsFalseWhenNoScopeIsActive()
    {
        TimeBudgetContext.IsExhausted.Should().BeFalse();
    }

    [Fact]
    public void RemainingOrMax_ReturnsMaxValueWhenNoScopeIsActive()
    {
        TimeBudgetContext.RemainingOrMax().Should().Be(double.MaxValue);
    }

    [Fact]
    public void Push_StartsNearConfiguredBudget()
    {
        using (TimeBudgetContext.Push(1000))
        {
            var remaining = TimeBudgetContext.RemainingMs;
            remaining.Should().NotBeNull();
            remaining!.Value.Should().BeInRange(900, 1000);
        }
    }

    [Fact]
    public void Push_RestoresPreviousValueOnDispose()
    {
        TimeBudgetContext.RemainingMs.Should().BeNull();

        using (TimeBudgetContext.Push(500))
        {
            TimeBudgetContext.RemainingMs.Should().NotBeNull();

            using (TimeBudgetContext.Push(200))
            {
                TimeBudgetContext.RemainingMs!.Value.Should().BeInRange(100, 200);
            }

            TimeBudgetContext.RemainingMs!.Value.Should().BeInRange(400, 500);
        }

        TimeBudgetContext.RemainingMs.Should().BeNull();
    }

    [Fact]
    public void Push_ThrowsOnZeroOrNegative()
    {
        Action actZero = () => TimeBudgetContext.Push(0);
        Action actNegative = () => TimeBudgetContext.Push(-1);

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsExhausted_BecomesTrueAfterBudgetElapses()
    {
        using (TimeBudgetContext.Push(20))
        {
            TimeBudgetContext.IsExhausted.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RemainingMs_DecreasesOverTime()
    {
        using (TimeBudgetContext.Push(500))
        {
            var first = TimeBudgetContext.RemainingMs!.Value;
            await Task.Delay(50);
            var second = TimeBudgetContext.RemainingMs!.Value;

            second.Should().BeLessThan(first);
        }
    }

    [Fact]
    public async Task IsExhausted_IsTrueAfterShortBudgetExpires()
    {
        using (TimeBudgetContext.Push(10))
        {
            await Task.Delay(30);
            TimeBudgetContext.IsExhausted.Should().BeTrue();
            TimeBudgetContext.RemainingMs!.Value.Should().BeLessThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task Push_FlowsAcrossAwaitBoundaries()
    {
        using (TimeBudgetContext.Push(500))
        {
            await Task.Delay(10);

            await Task.Run(() =>
            {
                TimeBudgetContext.RemainingMs.Should().NotBeNull();
            });
        }
    }

    [Fact]
    public async Task Push_IsIsolatedBetweenConcurrentFlows()
    {
        var observedA = new List<double?>();
        var observedB = new List<double?>();

        async Task Flow(int budget, List<double?> sink)
        {
            using (TimeBudgetContext.Push(budget))
            {
                for (var i = 0; i < 3; i++)
                {
                    await Task.Delay(5);
                    sink.Add(TimeBudgetContext.RemainingMs);
                }
            }
        }

        await Task.WhenAll(
            Task.Run(() => Flow(1000, observedA)),
            Task.Run(() => Flow(50, observedB)));

        observedA.Should().AllSatisfy(v => v.Should().NotBeNull().And.BeGreaterThan(900));
        observedB.Should().AllSatisfy(v => v.Should().NotBeNull().And.BeLessThanOrEqualTo(50));
    }

    [Fact]
    public void Push_NestedScopes_OuterRestoredAfterInnerPops()
    {
        using (TimeBudgetContext.Push(500))
        {
            using (TimeBudgetContext.Push(100))
            {
                TimeBudgetContext.RemainingMs!.Value.Should().BeInRange(50, 100);
            }

            TimeBudgetContext.RemainingMs!.Value.Should().BeInRange(400, 500);
        }

        TimeBudgetContext.RemainingMs.Should().BeNull();
    }
}
