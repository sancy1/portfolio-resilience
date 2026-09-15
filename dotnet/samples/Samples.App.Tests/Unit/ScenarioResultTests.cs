// filepath: dotnet/samples/Samples.App.Tests/Unit/ScenarioResultTests.cs
// layer: Unit | package: Samples.App.Tests | since: n/a
// purpose: Verifies the ScenarioResult record's shape, equality, and immutability
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Infrastructure.ScenarioResult
//   Used by    : dotnet test
//   See also   : Infrastructure/ScenarioResult.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Samples.App.Infrastructure;
using Xunit;

namespace Samples.App.Tests.Unit;

/// <summary>Unit tests for <see cref="ScenarioResult"/>.</summary>
public sealed class ScenarioResultTests
{
    /// <summary>Record exposes all five properties with the values passed in.</summary>
    [Fact]
    public void Constructor_PopulatesAllProperties()
    {
        var r = new ScenarioResult("01", "Retry", true, "ok", 42);

        r.Number.Should().Be("01");
        r.Name.Should().Be("Retry");
        r.Passed.Should().BeTrue();
        r.Message.Should().Be("ok");
        r.DurationMs.Should().Be(42);
    }

    /// <summary>Records with identical values are equal (record value equality).</summary>
    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new ScenarioResult("01", "Retry", true, "ok", 42);
        var b = new ScenarioResult("01", "Retry", true, "ok", 42);

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    /// <summary>Records with different Passed values are not equal.</summary>
    [Fact]
    public void Equality_DifferentPassed_AreNotEqual()
    {
        var a = new ScenarioResult("01", "Retry", true, "ok", 42);
        var b = new ScenarioResult("01", "Retry", false, "ok", 42);

        a.Should().NotBe(b);
    }

    /// <summary>DurationMs is a long, accepts large values.</summary>
    [Fact]
    public void DurationMs_IsLong_AcceptsLargeValues()
    {
        var r = new ScenarioResult("01", "Retry", true, "ok", long.MaxValue);
        r.DurationMs.Should().Be(long.MaxValue);
    }
}