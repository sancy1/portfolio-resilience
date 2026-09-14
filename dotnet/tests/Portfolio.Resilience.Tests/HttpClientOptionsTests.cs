// filepath: tests/Portfolio.Resilience.Tests/HttpClientOptionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: Verifies HttpClientOptions default header name and validation warnings.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : HttpClientOptions (Configuration/HttpClientOptions.cs)
//   Depends on : xUnit, FluentAssertions
//   See also   : docs/http-integration.md, docs/idempotency.md
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class HttpClientOptionsTests
{
    [Fact]
    public void DefaultHeaderName_IsIdempotencyKey()
    {
        HttpClientOptions.DefaultHeaderName.Should().Be("Idempotency-Key");
    }

    [Fact]
    public void NewInstance_UsesDefaultHeaderName()
    {
        var options = new HttpClientOptions();

        options.IdempotencyHeaderName.Should().Be(HttpClientOptions.DefaultHeaderName);
    }

    [Fact]
    public void IdempotencyHeaderName_IsSettable()
    {
        var options = new HttpClientOptions
        {
            IdempotencyHeaderName = "X-Custom-Idempotency"
        };

        options.IdempotencyHeaderName.Should().Be("X-Custom-Idempotency");
    }

    [Fact]
    public void Validate_DefaultInstance_ReturnsNoWarnings()
    {
        var options = new HttpClientOptions();

        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_NullHeaderName_ReturnsWarning()
    {
        var options = new HttpClientOptions { IdempotencyHeaderName = null! };

        var warnings = options.Validate();

        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain("IdempotencyHeaderName");
    }

    [Fact]
    public void Validate_EmptyHeaderName_ReturnsWarning()
    {
        var options = new HttpClientOptions { IdempotencyHeaderName = "" };

        options.Validate().Should().HaveCount(1);
    }

    [Fact]
    public void Validate_WhitespaceHeaderName_ReturnsWarning()
    {
        var options = new HttpClientOptions { IdempotencyHeaderName = "   " };

        options.Validate().Should().HaveCount(1);
    }
}
