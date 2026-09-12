// filepath: tests/Portfolio.Resilience.Tests/ResiliencePolicyRegistryTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies policy resolution, unknown-name fallback, and clone isolation.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ResiliencePolicyRegistry (Implementation/ResiliencePolicyRegistry.cs)
//   Depends on : ResilienceOptions, PolicyDefinition, xUnit, FluentAssertions
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Implementation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ResiliencePolicyRegistryTests
{
    private static ResilienceOptions BuildOptions()
    {
        return new ResilienceOptions
        {
            Policies =
            {
                ["auth-service"] = new PolicyDefinition
                {
                    Name = "auth-service",
                    Retry = new RetryOptions { MaxAttempts = 2, BaseDelayMs = 100 },
                    Circuit = new CircuitOptions { FailureThreshold = 3 },
                    Timeout = new TimeoutOptions { TimeoutMs = 5000 }
                },
                ["notification-service"] = new PolicyDefinition
                {
                    Name = "notification-service",
                    Retry = new RetryOptions { MaxAttempts = 5, BaseDelayMs = 200 },
                    Circuit = new CircuitOptions { FailureThreshold = 10 },
                    Timeout = new TimeoutOptions { TimeoutMs = 10000 }
                }
            },
            DefaultPolicy = new PolicyDefinition
            {
                Name = "default",
                Retry = new RetryOptions { MaxAttempts = 3, BaseDelayMs = 50 },
                Circuit = new CircuitOptions { FailureThreshold = 5 },
                Timeout = new TimeoutOptions { TimeoutMs = 2000 }
            }
        };
    }

    [Fact]
    public void Resolve_ThrowsOnNullOrWhitespacePolicyName()
    {
        var registry = new ResiliencePolicyRegistry();
        Action actNull = () => registry.Resolve(null!);
        Action actEmpty = () => registry.Resolve("");
        Action actWhitespace = () => registry.Resolve("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_KnownPolicy_ReturnsDefinition()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        var def = registry.Resolve("auth-service");

        def.Name.Should().Be("auth-service");
        def.Retry.MaxAttempts.Should().Be(2);
        def.Retry.BaseDelayMs.Should().Be(100);
        def.Circuit.FailureThreshold.Should().Be(3);
        def.Timeout.TimeoutMs.Should().Be(5000);
    }

    [Fact]
    public void Resolve_KnownPolicy_IsCaseInsensitive()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        var def = registry.Resolve("AUTH-SERVICE");

        def.Retry.MaxAttempts.Should().Be(2); // matches the "auth-service" entry
    }

    [Fact]
    public void Resolve_UnknownPolicy_FallsBackToDefault()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        var def = registry.Resolve("unknown-policy");

        // Default's values, but Name reflects the requested policy name.
        def.Name.Should().Be("unknown-policy");
        def.Retry.MaxAttempts.Should().Be(3);   // default's value
        def.Circuit.FailureThreshold.Should().Be(5);
        def.Timeout.TimeoutMs.Should().Be(2000);
    }

    [Fact]
    public void Resolve_ReturnsClone_MutationDoesNotAffectRegistry()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        var first = registry.Resolve("auth-service");
        first.Retry.MaxAttempts = 999;   // mutate the clone

        var second = registry.Resolve("auth-service");

        second.Retry.MaxAttempts.Should().Be(2);   // unchanged in the registry
    }

    [Fact]
    public void Resolve_TwoCalls_ReturnDistinctInstances()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        var a = registry.Resolve("auth-service");
        var b = registry.Resolve("auth-service");

        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void KnownPolicies_ReturnsExplicitlyRegisteredNames()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        registry.KnownPolicies.Should().BeEquivalentTo(
            new[] { "auth-service", "notification-service" });
    }

    [Fact]
    public void KnownPolicies_DoesNotIncludeDefault()
    {
        var registry = new ResiliencePolicyRegistry(BuildOptions());

        registry.KnownPolicies.Should().NotContain("default");
    }

    [Fact]
    public void Constructor_WithNoOptions_UsesEmptyRegistryAndDefaultPolicy()
    {
        var registry = new ResiliencePolicyRegistry();

        registry.KnownPolicies.Should().BeEmpty();

        // Unknown name resolves via the empty default
        var def = registry.Resolve("anything");
        def.Name.Should().Be("anything");
        def.Retry.MaxAttempts.Should().Be(new RetryOptions().MaxAttempts);
    }
}
