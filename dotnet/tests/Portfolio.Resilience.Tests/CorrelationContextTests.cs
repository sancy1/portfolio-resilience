// filepath: tests/Portfolio.Resilience.Tests/CorrelationContextTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies CorrelationContext push/pop semantics, ID generation, and async flow.
// RELATIONSHIPS
//   Tests      : CorrelationContext (Correlation/CorrelationContext.cs)
//   Depends on : xUnit, FluentAssertions
//   See also   : docs/correlation.md

using FluentAssertions;
using Portfolio.Resilience.Correlation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CorrelationContextTests
{
    [Fact]
    public void CurrentId_IsNullWhenNoScopeIsActive()
    {
        CorrelationContext.CurrentId.Should().BeNull();
    }

    [Fact]
    public void Push_SetsValueForCurrentScope()
    {
        using (CorrelationContext.Push("abc-123"))
        {
            CorrelationContext.CurrentId.Should().Be("abc-123");
        }
    }

    [Fact]
    public void Push_RestoresPreviousValueOnDispose()
    {
        using (CorrelationContext.Push("outer"))
        {
            CorrelationContext.CurrentId.Should().Be("outer");

            using (CorrelationContext.Push("inner"))
            {
                CorrelationContext.CurrentId.Should().Be("inner");
            }

            CorrelationContext.CurrentId.Should().Be("outer");
        }

        CorrelationContext.CurrentId.Should().BeNull();
    }
    [Fact]
    public void Push_ThrowsOnNullOrWhitespace()
    {
        Action actNull = () => CorrelationContext.Push(null!);
        Action actEmpty = () => CorrelationContext.Push("");
        Action actWhitespace = () => CorrelationContext.Push("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NewId_ReturnsUniqueIds()
    {
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => CorrelationContext.NewId())
            .ToArray();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().AllSatisfy(id => id.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void NewId_Returns32CharacterLowercaseHex()
    {
        var id = CorrelationContext.NewId();

        id.Should().HaveLength(32);
        id.Should().MatchRegex("^[0-9a-f]{32}$");
    }
    [Fact]
    public async Task Push_FlowsAcrossAwaitBoundaries()
    {
        using (CorrelationContext.Push("async-id"))
        {
            await Task.Delay(10);
            CorrelationContext.CurrentId.Should().Be("async-id");

            await Task.Run(() =>
            {
                CorrelationContext.CurrentId.Should().Be("async-id");
            });
        }
    }

    [Fact]
    public async Task Push_IsIsolatedBetweenConcurrentFlows()
    {
        var resultsA = new List<string?>();
        var resultsB = new List<string?>();

        async Task Flow(string id, List<string?> sink)
        {
            using (CorrelationContext.Push(id))
            {
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(5);
                    sink.Add(CorrelationContext.CurrentId);
                }
            }
        }

        await Task.WhenAll(
            Task.Run(() => Flow("A", resultsA)),
            Task.Run(() => Flow("B", resultsB)));

        resultsA.Should().AllBe("A");
        resultsB.Should().AllBe("B");
    }
    [Fact]
    public void Push_NestedScopes_RestoreInCorrectOrder()
    {
        using (CorrelationContext.Push("first"))
        {
            using (CorrelationContext.Push("second"))
            {
                using (CorrelationContext.Push("third"))
                {
                    CorrelationContext.CurrentId.Should().Be("third");
                }
                CorrelationContext.CurrentId.Should().Be("second");
            }
            CorrelationContext.CurrentId.Should().Be("first");
        }
        CorrelationContext.CurrentId.Should().BeNull();
    }
}
