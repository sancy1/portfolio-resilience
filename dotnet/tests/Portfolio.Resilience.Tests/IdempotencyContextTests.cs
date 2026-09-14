// filepath: tests/Portfolio.Resilience.Tests/IdempotencyContextTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: Verifies IdempotencyContext push/pop semantics, key generation, and async isolation.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : IdempotencyContext (Correlation/IdempotencyContext.cs)
//   Depends on : CorrelationContext, xUnit, FluentAssertions
//   See also   : docs/idempotency.md, SPEC.md section 18
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Correlation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class IdempotencyContextTests
{
    [Fact]
    public void CurrentKey_IsNullWhenNoScopeIsActive()
    {
        IdempotencyContext.CurrentKey.Should().BeNull();
    }

    [Fact]
    public void Push_SetsKeyForCurrentScope()
    {
        using (IdempotencyContext.Push("order-12345"))
        {
            IdempotencyContext.CurrentKey.Should().Be("order-12345");
        }
    }

    [Fact]
    public void Push_RestoresPreviousValueOnDispose()
    {
        using (IdempotencyContext.Push("outer"))
        {
            IdempotencyContext.CurrentKey.Should().Be("outer");

            using (IdempotencyContext.Push("inner"))
            {
                IdempotencyContext.CurrentKey.Should().Be("inner");
            }

            IdempotencyContext.CurrentKey.Should().Be("outer");
        }

        IdempotencyContext.CurrentKey.Should().BeNull();
    }

    [Fact]
    public void Push_NestedScopes_RestoreInCorrectOrder()
    {
        using (IdempotencyContext.Push("first"))
        {
            using (IdempotencyContext.Push("second"))
            {
                using (IdempotencyContext.Push("third"))
                {
                    IdempotencyContext.CurrentKey.Should().Be("third");
                }
                IdempotencyContext.CurrentKey.Should().Be("second");
            }
            IdempotencyContext.CurrentKey.Should().Be("first");
        }
        IdempotencyContext.CurrentKey.Should().BeNull();
    }

    [Fact]
    public void Push_ThrowsOnNullOrWhitespace()
    {
        Action actNull = () => IdempotencyContext.Push(null!);
        Action actEmpty = () => IdempotencyContext.Push("");
        Action actWhitespace = () => IdempotencyContext.Push("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GenerateFromCorrelation_UsesCorrelationIdWhenPresent()
    {
        using (CorrelationContext.Push("abc-123"))
        {
            IdempotencyContext.GenerateFromCorrelation().Should().Be("idem-abc-123");
        }
    }

    [Fact]
    public void GenerateFromCorrelation_FallsBackToGuidWhenCorrelationAbsent()
    {
        var key = IdempotencyContext.GenerateFromCorrelation();

        key.Should().StartWith("idem-");
        key.Should().MatchRegex("^idem-[0-9a-f]{32}$");
    }

    [Fact]
    public void GenerateFromCorrelation_ReturnsUniqueKeysWhenCorrelationAbsent()
    {
        var keys = Enumerable.Range(0, 100)
            .Select(_ => IdempotencyContext.GenerateFromCorrelation())
            .ToArray();

        keys.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Push_FlowsAcrossAwaitBoundaries()
    {
        using (IdempotencyContext.Push("async-key"))
        {
            await Task.Delay(10);
            IdempotencyContext.CurrentKey.Should().Be("async-key");

            await Task.Run(() =>
            {
                IdempotencyContext.CurrentKey.Should().Be("async-key");
            });
        }
    }

    [Fact]
    public async Task Push_IsIsolatedBetweenConcurrentFlows()
    {
        var resultsA = new List<string?>();
        var resultsB = new List<string?>();

        async Task Flow(string key, List<string?> sink)
        {
            using (IdempotencyContext.Push(key))
            {
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(5);
                    sink.Add(IdempotencyContext.CurrentKey);
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
    public void Push_DoesNotInterfereWithCorrelationContext()
    {
        using (CorrelationContext.Push("corr-1"))
        using (IdempotencyContext.Push("idem-1"))
        {
            CorrelationContext.CurrentId.Should().Be("corr-1");
            IdempotencyContext.CurrentKey.Should().Be("idem-1");
        }

        CorrelationContext.CurrentId.Should().BeNull();
        IdempotencyContext.CurrentKey.Should().BeNull();
    }
}
