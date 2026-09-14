// filepath: tests/Portfolio.Resilience.Analyzers.Tests/MisconfigurationAnalyzerTests.cs
// layer: Tests | package: Portfolio.Resilience.Analyzers.Tests | since: v0.7.0
// purpose: Verifies the PR0002 MisconfigurationAnalyzer rules.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : MisconfigurationAnalyzer (Analyzers/)
//   Depends on : Microsoft.CodeAnalysis.CSharp, xUnit, FluentAssertions
//   See also   : docs/analyzers.md, SPEC.md section 17
// -----------------------------------------------------------------------------

using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Analyzers;
using Xunit;

namespace Portfolio.Resilience.Analyzers.Tests;

public sealed class MisconfigurationAnalyzerTests
{
    // ------------------------------------------------------------------------
    // Test infrastructure
    // ------------------------------------------------------------------------

    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        // Base .NET 10 reference assemblies from the Basic.Reference.Assemblies
        // package. Ships as ordinary NuGet assets - no OS-specific path resolution.
        var refs = new List<MetadataReference>(Basic.Reference.Assemblies.Net100.References.All);

        // The Microsoft.Extensions.* assemblies are not covered by
        // Basic.Reference.Assemblies. They come from the Microsoft.AspNetCore.App
        // framework reference on the test project.
        refs.Add(MetadataReference.CreateFromFile(
            typeof(System.Net.Http.IHttpClientFactory).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(
            typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));

        // The library under test, from the build output.
        refs.Add(MetadataReference.CreateFromFile(typeof(Portfolio.Resilience.Configuration.ResilienceBuilder).Assembly.Location));

        return refs;
    }

    private static async Task<IReadOnlyList<Diagnostic>> RunAnalyzerAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            "MisconfigTestAssembly",
            new[] { syntaxTree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (compileErrors.Length > 0)
        {
            throw new InvalidOperationException(
                "Test source did not compile: " +
                string.Join("; ", compileErrors.Select(e => e.GetMessage())));
        }

        var analyzer = new MisconfigurationAnalyzer();
        var cwa = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        return await cwa.GetAnalyzerDiagnosticsAsync();
    }

    // ------------------------------------------------------------------------
    // Test sources
    // ------------------------------------------------------------------------

    private const string SourceWithValidRateLimiter = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = 100;
        });
    }
}
";

    private const string SourceWithZeroPermitLimit = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = 0;
        });
    }
}
";

    private const string SourceWithNegativePermitLimit = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = -5;
        });
    }
}
";

    private const string SourceWithPermitLimitZeroButDisabled = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.RateLimiter.Enabled = false;
            p.RateLimiter.PermitLimit = 0;
        });
    }
}
";

    private const string SourceWithZeroBulkheadConcurrency = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.Bulkhead.Enabled = true;
            p.Bulkhead.MaxConcurrency = 0;
        });
    }
}
";

    private const string SourceWithZeroHedgingAttempts = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.Hedging.Enabled = true;
            p.Hedging.MaxAttempts = 0;
        });
    }
}
";

    private const string SourceWithNegativeTimeout = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.Timeout.TimeoutMs = -1;
        });
    }
}
";

    private const string SourceWithNegativeRetryAttempts = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.Retry.MaxAttempts = -2;
        });
    }
}
";

    private const string SourceWithMultipleMisconfigurations = @"
using Portfolio.Resilience.Configuration;

public static class Setup
{
    public static void Configure(ResilienceBuilder r)
    {
        r.AddPolicy(""api"", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = 0;
            p.Bulkhead.Enabled = true;
            p.Bulkhead.MaxConcurrency = 0;
        });
    }
}
";

    // ------------------------------------------------------------------------
    // Descriptor
    // ------------------------------------------------------------------------

    [Fact]
    public void DiagnosticDescriptor_IsCorrectlyConfigured()
    {
        var analyzer = new MisconfigurationAnalyzer();

        analyzer.SupportedDiagnostics.Should().HaveCount(1);
        var rule = analyzer.SupportedDiagnostics[0];

        rule.Id.Should().Be("PR0002");
        rule.Category.Should().Be("Reliability");
        rule.DefaultSeverity.Should().Be(DiagnosticSeverity.Warning);
        rule.IsEnabledByDefault.Should().BeTrue();
    }

    // ------------------------------------------------------------------------
    // The rule fires
    // ------------------------------------------------------------------------

    [Fact]
    public async Task RateLimiterEnabledWithZeroPermitLimit_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithZeroPermitLimit);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0002");
        diagnostics[0].GetMessage().Should().Contain("PermitLimit");
    }

    [Fact]
    public async Task RateLimiterEnabledWithNegativePermitLimit_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithNegativePermitLimit);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0002");
    }

    [Fact]
    public async Task BulkheadEnabledWithZeroMaxConcurrency_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithZeroBulkheadConcurrency);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0002");
        diagnostics[0].GetMessage().Should().Contain("MaxConcurrency");
    }

    [Fact]
    public async Task HedgingEnabledWithZeroMaxAttempts_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithZeroHedgingAttempts);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0002");
    }

    [Fact]
    public async Task NegativeTimeout_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithNegativeTimeout);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0002");
    }

    [Fact]
    public async Task NegativeRetryAttempts_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithNegativeRetryAttempts);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0002");
    }

    [Fact]
    public async Task MultipleMisconfigurations_ProduceMultipleDiagnostics()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithMultipleMisconfigurations);

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Id == "PR0002");
    }

    // ------------------------------------------------------------------------
    // The rule does NOT fire (guard conditions)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ValidConfiguration_NoWarning()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithValidRateLimiter);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task PermitLimitZeroButDisabled_NoWarning()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithPermitLimitZeroButDisabled);

        diagnostics.Should().BeEmpty();
    }
}




