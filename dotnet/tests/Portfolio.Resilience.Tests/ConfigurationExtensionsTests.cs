// filepath: tests/Portfolio.Resilience.Tests/ConfigurationExtensionsTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies LoadFromConfiguration binds policies, default policy, and env var overrides.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ConfigurationExtensions (Extensions/ConfigurationExtensions.cs)
//   Depends on : IConfiguration, ResilienceBuilder, xUnit, FluentAssertions
//   See also   : docs/executor.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Extensions;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ConfigurationExtensionsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [Fact]
    public void LoadFromConfiguration_ThrowsOnNullConfiguration()
    {
        var builder = new ResilienceBuilder();
        Action act = () => builder.LoadFromConfiguration(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void LoadFromConfiguration_ThrowsOnNullOrWhitespaceSectionName()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var builder = new ResilienceBuilder();

        Action actNull = () => builder.LoadFromConfiguration(config, null!);
        Action actEmpty = () => builder.LoadFromConfiguration(config, "");
        Action actWhitespace = () => builder.LoadFromConfiguration(config, "   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LoadFromConfiguration_MissingSection_IsNoOp()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var builder = new ResilienceBuilder();

        builder.LoadFromConfiguration(config);

        builder.Options.Policies.Should().BeEmpty();
    }

    [Fact]
    public void LoadFromConfiguration_ReturnsBuilderForChaining()
    {
        var config = BuildConfig(new Dictionary<string, string?>());
        var builder = new ResilienceBuilder();

        var result = builder.LoadFromConfiguration(config);

        result.Should().BeSameAs(builder);
    }
    [Fact]
    public void LoadFromConfiguration_BindsDefaultPolicy()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Resilience:DefaultPolicy:Retry:MaxAttempts"]           = "7",
            ["Resilience:DefaultPolicy:Retry:BaseDelayMs"]           = "150",
            ["Resilience:DefaultPolicy:Circuit:FailureThreshold"]    = "9",
            ["Resilience:DefaultPolicy:Timeout:TimeoutMs"]           = "30000"
        });

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        builder.Options.DefaultPolicy.Retry.MaxAttempts.Should().Be(7);
        builder.Options.DefaultPolicy.Retry.BaseDelayMs.Should().Be(150);
        builder.Options.DefaultPolicy.Circuit.FailureThreshold.Should().Be(9);
        builder.Options.DefaultPolicy.Timeout.TimeoutMs.Should().Be(30000);
    }

    [Fact]
    public void LoadFromConfiguration_BindsNamedPolicies()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Resilience:Policies:auth-service:Retry:MaxAttempts"]   = "2",
            ["Resilience:Policies:auth-service:Timeout:TimeoutMs"]   = "5000",
            ["Resilience:Policies:db:Retry:MaxAttempts"]             = "5",
            ["Resilience:Policies:db:Timeout:TimeoutMs"]             = "10000"
        });

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        builder.Options.Policies.Should().ContainKeys("auth-service", "db");

        var auth = builder.Options.Policies["auth-service"];
        auth.Name.Should().Be("auth-service");
        auth.Retry.MaxAttempts.Should().Be(2);
        auth.Timeout.TimeoutMs.Should().Be(5000);

        var db = builder.Options.Policies["db"];
        db.Retry.MaxAttempts.Should().Be(5);
        db.Timeout.TimeoutMs.Should().Be(10000);
    }

    [Fact]
    public void LoadFromConfiguration_PartialPolicy_KeepsDefaultsForUnspecified()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Resilience:Policies:minimal:Timeout:TimeoutMs"] = "1234"
        });

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        var policy = builder.Options.Policies["minimal"];
        policy.Timeout.TimeoutMs.Should().Be(1234);
        // Unspecified retry uses defaults
        policy.Retry.MaxAttempts.Should().Be(new RetryOptions().MaxAttempts);
    }

    [Fact]
    public void LoadFromConfiguration_CustomSectionName_IsRespected()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["MySection:Policies:svc:Timeout:TimeoutMs"] = "9999"
        });

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config, sectionName: "MySection");

        builder.Options.Policies["svc"].Timeout.TimeoutMs.Should().Be(9999);
    }

    [Fact]
    public void LoadFromConfiguration_ExistingPoliciesAreOverriddenOnCollision()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Resilience:Policies:p:Timeout:TimeoutMs"] = "1111"
        });

        var builder = new ResilienceBuilder();
        builder.AddPolicy("p", pol => pol.Timeout.TimeoutMs = 9999); // set first

        builder.LoadFromConfiguration(config);

        // Configuration wins
        builder.Options.Policies["p"].Timeout.TimeoutMs.Should().Be(1111);
    }

    [Fact]
    public void LoadFromConfiguration_BindsLoggingOptions()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Resilience:Policies:p:Logging:EmitCallStarted"] = "true",
            ["Resilience:Policies:p:Logging:EmitCallFailed"]  = "false"
        });

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        var policy = builder.Options.Policies["p"];
        policy.Logging.EmitCallStarted.Should().BeTrue();
        policy.Logging.EmitCallFailed.Should().BeFalse();
    }

    [Fact]
    public void LoadFromConfiguration_BindsFallbackOptions()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Resilience:Policies:p:Fallback:Enabled"] = "true",
            ["Resilience:Policies:p:Fallback:Reason"]  = "circuit open"
        });

        var builder = new ResilienceBuilder();
        builder.LoadFromConfiguration(config);

        var policy = builder.Options.Policies["p"];
        policy.Fallback.Enabled.Should().BeTrue();
        policy.Fallback.Reason.Should().Be("circuit open");
    }
}
