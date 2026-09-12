// filepath: tests/Portfolio.Resilience.Tests/AsyncLocalCorrelationAccessorTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies the DI adapter delegates correctly to the ambient CorrelationContext.
// RELATIONSHIPS
//   Tests      : AsyncLocalCorrelationAccessor (Correlation/AsyncLocalCorrelationAccessor.cs)
//   Depends on : CorrelationContext, xUnit, FluentAssertions
//   See also   : docs/correlation.md

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Correlation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class AsyncLocalCorrelationAccessorTests
{
    private readonly ICorrelationAccessor _accessor = new AsyncLocalCorrelationAccessor();

    [Fact]
    public void Current_IsNullWhenNoScopeIsActive()
    {
        _accessor.Current.Should().BeNull();
    }

    [Fact]
    public void Current_ReflectsValueSetViaPrimitivePush()
    {
        using (CorrelationContext.Push("from-primitive"))
        {
            _accessor.Current.Should().Be("from-primitive");
        }
    }
    [Fact]
    public void Push_SetsValueVisibleToPrimitiveRead()
    {
        using (_accessor.Push("from-accessor"))
        {
            CorrelationContext.CurrentId.Should().Be("from-accessor");
            _accessor.Current.Should().Be("from-accessor");
        }
    }

    [Fact]
    public void Push_ThrowsOnNullOrWhitespace()
    {
        Action actNull = () => _accessor.Push(null!);
        Action actEmpty = () => _accessor.Push("");
        Action actWhitespace = () => _accessor.Push("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Push_RestoresPreviousValueOnDispose()
    {
        using (_accessor.Push("outer"))
        {
            _accessor.Current.Should().Be("outer");

            using (_accessor.Push("inner"))
            {
                _accessor.Current.Should().Be("inner");
            }

            _accessor.Current.Should().Be("outer");
        }

        _accessor.Current.Should().BeNull();
    }
}
