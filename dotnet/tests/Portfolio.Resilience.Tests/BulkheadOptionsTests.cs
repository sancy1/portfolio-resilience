// filepath: tests/Portfolio.Resilience.Tests/BulkheadOptionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.6.0
// purpose: Verifies BulkheadOptions defaults and all three validation rules.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : BulkheadOptions (Configuration/BulkheadOptions.cs)
//   Depends on : ResilienceErrorCategory, xUnit, FluentAssertions
//   See also   : docs/bulkhead.md, SPEC.md section 13
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class BulkheadOptionsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var options = new BulkheadOptions();

        options.Enabled.Should().BeFalse();
        options.MaxConcurrency.Should().Be(20);
        options.MaxQueue.Should().Be(100);
        options.QueueTimeoutMs.Should().Be(5_000);
        options.RejectionCategory.Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public void Validate_ThrowsOnNullOrWhitespacePolicyName()
    {
        var options = new BulkheadOptions();

        Action act1 = () => options.Validate(null!);
        Action act2 = () => options.Validate("  ");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_Defaults_ReturnsNoWarnings()
    {
        var options = new BulkheadOptions();
        options.Validate("p").Should().BeEmpty();
    }

    [Fact]
    public void Validate_MaxConcurrencyZeroOrNegative_Warns()
    {
        var zero = new BulkheadOptions { MaxConcurrency = 0 };
        var negative = new BulkheadOptions { MaxConcurrency = -1 };

        zero.Validate("p").Should().HaveCount(1);
        negative.Validate("p").Should().HaveCount(1);
        zero.Validate("p")[0].Should().Contain("MaxConcurrency");
    }

    [Fact]
    public void Validate_NegativeMaxQueue_Warns()
    {
        var options = new BulkheadOptions { MaxQueue = -1 };

        var warnings = options.Validate("p");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("MaxQueue");
    }

    [Fact]
    public void Validate_NegativeQueueTimeout_Warns()
    {
        var options = new BulkheadOptions { QueueTimeoutMs = -1 };

        var warnings = options.Validate("p");
        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("QueueTimeoutMs");
    }

    [Fact]
    public void Validate_MultipleProblems_ReturnsAllWarnings()
    {
        var options = new BulkheadOptions
        {
            MaxConcurrency = 0,
            MaxQueue = -1,
            QueueTimeoutMs = -1
        };

        options.Validate("p").Should().HaveCount(3);
    }
}
