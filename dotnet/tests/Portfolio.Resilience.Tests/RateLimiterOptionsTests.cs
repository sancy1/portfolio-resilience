// filepath: tests/Portfolio.Resilience.Tests/RateLimiterOptionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: Verifies RateLimiterOptions defaults and all five validation rules.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : RateLimiterOptions (Configuration/RateLimiterOptions.cs)
//   Depends on : RateLimitStrategy, ResilienceErrorCategory, xUnit, FluentAssertions
//   See also   : docs/rate-limiter.md, SPEC.md section 12
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class RateLimiterOptionsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var options = new RateLimiterOptions();

        options.Enabled.Should().BeFalse();
        options.Strategy.Should().Be(RateLimitStrategy.SlidingWindow);
        options.PermitLimit.Should().Be(100);
        options.WindowSeconds.Should().Be(60);
        options.QueueLimit.Should().Be(0);
        options.QueueTimeoutMs.Should().Be(5_000);
        options.RejectionCategory.Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public void Validate_ThrowsOnNullOrWhitespacePolicyName()
    {
        var options = new RateLimiterOptions();

        Action act1 = () => options.Validate(null!);
        Action act2 = () => options.Validate("  ");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_Defaults_ReturnsNoWarnings()
    {
        var options = new RateLimiterOptions();
        options.Validate("p").Should().BeEmpty();
    }

    [Fact]
    public void Validate_PermitLimitZeroOrNegative_Warns()
    {
        var zero = new RateLimiterOptions { PermitLimit = 0 };
        var negative = new RateLimiterOptions { PermitLimit = -1 };

        zero.Validate("p").Should().HaveCount(1);
        negative.Validate("p").Should().HaveCount(1);
        zero.Validate("p")[0].Should().Contain("PermitLimit");
    }

    [Fact]
    public void Validate_WindowSecondsZeroOrNegative_WarnsForNonConcurrencyStrategies()
    {
        var options = new RateLimiterOptions
        {
            Strategy = RateLimitStrategy.SlidingWindow,
            WindowSeconds = 0
        };

        var warnings = options.Validate("p");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("WindowSeconds");
    }

    [Fact]
    public void Validate_ConcurrencyLimitWithNonDefaultWindow_Warns()
    {
        var options = new RateLimiterOptions
        {
            Strategy = RateLimitStrategy.ConcurrencyLimit,
            WindowSeconds = 30
        };

        var warnings = options.Validate("p");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("ConcurrencyLimit").And.Contain("WindowSeconds");
    }

    [Fact]
    public void Validate_ConcurrencyLimitWithDefaultWindow_NoWarning()
    {
        var options = new RateLimiterOptions
        {
            Strategy = RateLimitStrategy.ConcurrencyLimit,
            WindowSeconds = 60 // default
        };

        options.Validate("p").Should().BeEmpty();
    }

    [Fact]
    public void Validate_NegativeQueueLimit_Warns()
    {
        var options = new RateLimiterOptions { QueueLimit = -1 };

        var warnings = options.Validate("p");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("QueueLimit");
    }

    [Fact]
    public void Validate_NegativeQueueTimeout_Warns()
    {
        var options = new RateLimiterOptions { QueueTimeoutMs = -1 };

        var warnings = options.Validate("p");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("QueueTimeoutMs");
    }
}
