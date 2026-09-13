// filepath: tests/Portfolio.Resilience.Tests/RateLimitStrategyTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: Verifies the RateLimitStrategy enum has the expected members and distinct values.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : RateLimitStrategy (Configuration/RateLimitStrategy.cs)
//   Depends on : xUnit, FluentAssertions
//   See also   : docs/rate-limiter.md, SPEC.md section 12.2
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class RateLimitStrategyTests
{
    [Fact]
    public void Enum_HasFourMembers()
    {
        var values = Enum.GetValues<RateLimitStrategy>();
        values.Should().HaveCount(4);
    }

    [Fact]
    public void Enum_ContainsAllExpectedStrategies()
    {
        Enum.IsDefined(RateLimitStrategy.TokenBucket).Should().BeTrue();
        Enum.IsDefined(RateLimitStrategy.SlidingWindow).Should().BeTrue();
        Enum.IsDefined(RateLimitStrategy.FixedWindow).Should().BeTrue();
        Enum.IsDefined(RateLimitStrategy.ConcurrencyLimit).Should().BeTrue();
    }

    [Fact]
    public void Enum_ValuesAreDistinct()
    {
        var values = Enum.GetValues<RateLimitStrategy>().Cast<int>().ToArray();
        values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Enum_ValuesAreZeroBased()
    {
        ((int)RateLimitStrategy.TokenBucket).Should().Be(0);
        ((int)RateLimitStrategy.SlidingWindow).Should().Be(1);
        ((int)RateLimitStrategy.FixedWindow).Should().Be(2);
        ((int)RateLimitStrategy.ConcurrencyLimit).Should().Be(3);
    }
}
