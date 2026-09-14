// filepath: tests/Portfolio.Resilience.Analyzers.Tests/HttpClientBypassAnalyzerTests.cs
// layer: Tests | package: Portfolio.Resilience.Analyzers.Tests | since: v0.7.0
// purpose: Verifies the PR0001 HttpClientBypassAnalyzer rules.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : HttpClientBypassAnalyzer (Analyzers/)
//   Depends on : Microsoft.CodeAnalysis.CSharp, xUnit, FluentAssertions
//   See also   : docs/analyzers.md, SPEC.md section 17
// -----------------------------------------------------------------------------

using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Analyzers;
using Xunit;

namespace Portfolio.Resilience.Analyzers.Tests;

public sealed class HttpClientBypassAnalyzerTests
{
    // ------------------------------------------------------------------------
    // Test infrastructure
    // ------------------------------------------------------------------------

    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddPath(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && seen.Add(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        void AddDirectory(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                AddPath(dll);
            }
        }

        var dotnetRoot = ReferenceAssemblies.GetDotnetPacksRoot();

        // Reference assemblies for the base framework (System.Runtime, etc.).
        AddDirectoryFromPack(Path.Combine(dotnetRoot, "Microsoft.NETCore.App.Ref"), "net10.0");

        // Reference assemblies for the ASP.NET Core / Extensions surface
        // (Microsoft.Extensions.Http, Microsoft.Extensions.DependencyInjection, ...).
        AddDirectoryFromPack(Path.Combine(dotnetRoot, "Microsoft.AspNetCore.App.Ref"), "net10.0");

        // The library under test and the resilience abstractions, from the
        // project's build output (a real runtime assembly is fine here).
        AddPath(typeof(IResilienceExecutor).Assembly.Location);

        return refs;

        void AddDirectoryFromPack(string packRoot, string tfm)
        {
            if (!Directory.Exists(packRoot)) return;

            // Find the highest-versioned pack folder (e.g. 10.0.10).
            var versionDir = Directory.GetDirectories(packRoot)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (versionDir is null) return;

            var refDir = Path.Combine(versionDir, "ref", tfm);
            AddDirectory(refDir);
        }
    }

    private static async Task<IReadOnlyList<Diagnostic>> RunAnalyzerAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTestAssembly",
            syntaxTrees: new[] { syntaxTree },
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Ensure the compilation itself is clean; otherwise analyzer diagnostics
        // are meaningless.
        var compilationErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (compilationErrors.Length > 0)
        {
            throw new InvalidOperationException(
                "Test source did not compile: " +
                string.Join("; ", compilationErrors.Select(e => e.GetMessage())));
        }

        var analyzer = new HttpClientBypassAnalyzer();
        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));

        return await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    // ------------------------------------------------------------------------
    // Test sources
    // ------------------------------------------------------------------------

    private const string SourceWithFactoryClientAndNoExecutor = @"
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;

namespace TestNs
{
    public sealed class MyService
    {
        private readonly HttpClient _http;

        public MyService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient(""auth-service"");
        }

        public Task<string> GetAsync() => _http.GetStringAsync(""http://example.com"");
    }
}
";

    private const string SourceWithFactoryClientAndExecutor = @"
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;
using Portfolio.Resilience.Abstractions;

namespace TestNs
{
    public sealed class MyService
    {
        private readonly HttpClient _http;
        private readonly IResilienceExecutor _resilience;

        public MyService(IHttpClientFactory factory, IResilienceExecutor resilience)
        {
            _http = factory.CreateClient(""auth-service"");
            _resilience = resilience;
        }

        public Task<string> GetAsync() =>
            _resilience.ExecuteAsync(""auth-service"", ct => _http.GetStringAsync(""http://example.com""));
    }
}
";

    private const string SourceWithNewHttpClient = @"
using System.Net.Http;
using System.Threading.Tasks;

namespace TestNs
{
    public sealed class MyService
    {
        private readonly HttpClient _http = new HttpClient();

        public Task<string> GetAsync() => _http.GetStringAsync(""http://example.com"");
    }
}
";

    private const string SourceWithFactoryClientButNoCalls = @"
using System.Net.Http;
using Microsoft.Extensions.Http;

namespace TestNs
{
    public sealed class MyService
    {
        private readonly HttpClient _http;

        public MyService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient(""auth-service"");
        }
    }
}
";

    private const string SourceWithFactoryClientAndNonHttpMethodCall = @"
using System.Net.Http;
using Microsoft.Extensions.Http;

namespace TestNs
{
    public sealed class MyService
    {
        private readonly HttpClient _http;

        public MyService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient(""auth-service"");
        }

        public void Shutdown() => _http.Dispose();
    }
}
";

    private const string SourceWithTwoCalls = @"
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;

namespace TestNs
{
    public sealed class MyService
    {
        private readonly HttpClient _http;

        public MyService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient(""auth-service"");
        }

        public async Task<string> FirstAsync() => await _http.GetStringAsync(""http://a.com"");
        public async Task<string> SecondAsync() => await _http.GetStringAsync(""http://b.com"");
    }
}
";

    private const string SourceWithProperty = @"
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Http;

namespace TestNs
{
    public sealed class MyService
    {
        private HttpClient Client { get; }

        public MyService(IHttpClientFactory factory)
        {
            Client = factory.CreateClient(""auth-service"");
        }

        public Task<string> GetAsync() => Client.GetStringAsync(""http://example.com"");
    }
}
";

    // ------------------------------------------------------------------------
    // Descriptor
    // ------------------------------------------------------------------------

    [Fact]
    public void DiagnosticDescriptor_IsCorrectlyConfigured()
    {
        var analyzer = new HttpClientBypassAnalyzer();

        analyzer.SupportedDiagnostics.Should().HaveCount(1);
        var rule = analyzer.SupportedDiagnostics[0];

        rule.Id.Should().Be("PR0001");
        rule.Category.Should().Be("Reliability");
        rule.DefaultSeverity.Should().Be(DiagnosticSeverity.Warning);
        rule.IsEnabledByDefault.Should().BeTrue();
    }

    // ------------------------------------------------------------------------
    // The rule fires
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ClassWithHttpClientFromFactory_AndNoExecutor_Warns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithFactoryClientAndNoExecutor);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0001");
        diagnostics[0].GetMessage().Should().Contain("_http");
    }

    [Fact]
    public async Task MultipleCallsOnSameClient_MultipleDiagnostics()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithTwoCalls);

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Id == "PR0001");
    }

    [Fact]
    public async Task PropertyBasedHttpClient_AlsoWarns()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithProperty);

        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be("PR0001");
        diagnostics[0].GetMessage().Should().Contain("Client");
    }

    // ------------------------------------------------------------------------
    // The rule does NOT fire (guard conditions)
    // ------------------------------------------------------------------------

    [Fact]
    public async Task ClassWithExecutor_NoWarning()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithFactoryClientAndExecutor);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ClassWithHttpClientFromNew_NoWarning()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithNewHttpClient);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ClassWithHttpClientButNoCalls_NoWarning()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithFactoryClientButNoCalls);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task ClassWithHttpClientAndNonHttpMethodCall_NoWarning()
    {
        var diagnostics = await RunAnalyzerAsync(SourceWithFactoryClientAndNonHttpMethodCall);

        diagnostics.Should().BeEmpty();
    }
}

