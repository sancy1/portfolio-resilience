// filepath: src/Portfolio.Resilience.Analyzers/HttpClientBypassAnalyzer.cs
// layer: Analyzers | package: Portfolio.Resilience.Analyzers | since: v0.7.0
// purpose: PR0001 - warns when an HttpClient obtained from IHttpClientFactory is used without resilience.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : DiagnosticAnalyzer (Roslyn)
//   Depends on : Microsoft.CodeAnalysis, Microsoft.CodeAnalysis.Diagnostics
//   Used by    : the C# compiler at build time
//   See also   : docs/analyzers.md, SPEC.md section 17
// -----------------------------------------------------------------------------

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Portfolio.Resilience.Analyzers;

/// <summary>
/// PR0001 - Warns when a class obtains an <c>HttpClient</c> from
/// <c>IHttpClientFactory</c> but calls it directly, bypassing the resilience
/// pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The heuristic inspects each class for a field or property of type
/// <c>System.Net.Http.HttpClient</c> assigned from an
/// <c>IHttpClientFactory.CreateClient(...)</c> call - either in a field
/// initializer or in a constructor body. When such a class calls
/// <c>SendAsync</c>, <c>GetAsync</c>, <c>PostAsync</c>, <c>PutAsync</c>,
/// <c>DeleteAsync</c>, <c>PatchAsync</c>, or <c>GetStringAsync</c> on that
/// field, and the class does not have an <c>IResilienceExecutor</c> field or
/// parameter, the analyzer emits a warning.
/// </para>
/// <para>
/// <b>False positives are possible.</b> A class may use an HttpClient for a
/// purpose that does not need resilience (a health probe, a metrics scrape).
/// Users may suppress the rule locally with
/// <c>#pragma warning disable PR0001</c>, or globally via <c>.editorconfig</c>.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HttpClientBypassAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID for the bypass rule.</summary>
    public const string DiagnosticId = "PR0001";

    private const string Category = "Reliability";

    private static readonly LocalizableString Title =
        "HttpClient used without the resilience pipeline";

    private static readonly LocalizableString MessageFormat =
        "HttpClient '{0}' is called directly. Register it with AddResilientHandler() or wrap calls with IResilienceExecutor to apply the resilience pipeline.";

    private static readonly LocalizableString Description =
        "Calls made on an HttpClient obtained from IHttpClientFactory bypass " +
        "the resilience pipeline. Register the client with " +
        "AddResilientHandler(policyName), or wrap the call in " +
        "IResilienceExecutor.ExecuteAsync(...). See docs/http-integration.md.";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    // ------------------------------------------------------------------------
    // Analysis
    // ------------------------------------------------------------------------

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;

        // Does the class have an IResilienceExecutor field, property, or
        // constructor parameter? If so, the developer knows about resilience;
        // we assume they use it.
        if (HasResilienceExecutor(classDecl, context.SemanticModel))
        {
            return;
        }

        // Find HttpClient fields/properties assigned from CreateClient(...).
        var httpFields = FindHttpClientFields(classDecl, context.SemanticModel);
        if (httpFields.Count == 0)
        {
            return;
        }

        // Find invocations on those fields.
        foreach (var member in classDecl.Members)
        {
            foreach (var invocation in member.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                {
                    continue;
                }

                var receiverName = GetReceiverName(memberAccess);
                if (receiverName is null || !httpFields.Contains(receiverName))
                {
                    continue;
                }

                var methodName = memberAccess.Name.Identifier.ValueText;
                if (!IsHttpClientMethod(methodName))
                {
                    continue;
                }

                // Ensure the receiver is typed as System.Net.Http.HttpClient.
                // Checking the receiver (not the invocation symbol) also accepts
                // extension methods such as HttpClientJsonExtensions.GetFromJsonAsync.
                var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                if (!IsHttpClientType(receiverType))
                {
                    continue;
                }

                var diagnostic = Diagnostic.Create(
                    Rule,
                    invocation.GetLocation(),
                    receiverName);

                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private static bool HasResilienceExecutor(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel)
    {
        // Check constructor parameters.
        foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var param in ctor.ParameterList.Parameters)
            {
                if (param.Type is null) continue;
                var typeInfo = semanticModel.GetTypeInfo(param.Type).Type;
                if (typeInfo is not null && IsResilienceExecutorType(typeInfo))
                {
                    return true;
                }
            }
        }

        // Check fields and properties.
        foreach (var member in classDecl.Members)
        {
            TypeSyntax? typeSyntax = member switch
            {
                FieldDeclarationSyntax f when f.Declaration.Variables.Count > 0 => f.Declaration.Type,
                PropertyDeclarationSyntax p => p.Type,
                _ => null
            };

            if (typeSyntax is null) continue;

            var typeInfo = semanticModel.GetTypeInfo(typeSyntax).Type;
            if (typeInfo is not null && IsResilienceExecutorType(typeInfo))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the type is (or implements)
    /// Portfolio.Resilience.Abstractions.IResilienceExecutor.
    /// Compares by symbol Name and namespace.
    /// </summary>
    private static bool IsResilienceExecutorType(ITypeSymbol type)
    {
        if (type.Name == "IResilienceExecutor"
            && type.ContainingNamespace?.ToDisplayString() == "Portfolio.Resilience.Abstractions")
        {
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name == "IResilienceExecutor"
                && iface.ContainingNamespace?.ToDisplayString() == "Portfolio.Resilience.Abstractions")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when the type is System.Net.Http.HttpClient.
    /// Compares by symbol Name and namespace instead of ToDisplayString,
    /// which can add nullability annotations or use different formatting.
    /// </summary>
    private static bool IsHttpClientType(ITypeSymbol? type)
    {
        if (type is null) return false;
        return type.Name == "HttpClient"
            && type.ContainingNamespace?.ToDisplayString() == "System.Net.Http";
    }

    private static HashSet<string> FindHttpClientFields(
        ClassDeclarationSyntax classDecl,
        SemanticModel semanticModel)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: collect every HttpClient-typed field and property name.
        var httpTypedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in classDecl.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                var typeInfo = semanticModel.GetTypeInfo(field.Declaration.Type).Type;
                if (!IsHttpClientType(typeInfo)) continue;

                foreach (var variable in field.Declaration.Variables)
                {
                    httpTypedNames.Add(variable.Identifier.ValueText);
                }
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                var typeInfo = semanticModel.GetTypeInfo(prop.Type).Type;
                if (!IsHttpClientType(typeInfo)) continue;
                httpTypedNames.Add(prop.Identifier.ValueText);
            }
        }

        if (httpTypedNames.Count == 0)
        {
            return result;
        }

        // Pass 2: recognize CreateClient(...) on the field/property initializer.
        foreach (var member in classDecl.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    if (httpTypedNames.Contains(variable.Identifier.ValueText) &&
                        IsCreateClientInitializer(variable.Initializer?.Value, semanticModel))
                    {
                        result.Add(variable.Identifier.ValueText);
                    }
                }
            }
            else if (member is PropertyDeclarationSyntax prop)
            {
                if (httpTypedNames.Contains(prop.Identifier.ValueText) &&
                    prop.Initializer is not null &&
                    IsCreateClientInitializer(prop.Initializer.Value, semanticModel))
                {
                    result.Add(prop.Identifier.ValueText);
                }
            }
        }

        // Pass 3: recognize constructor-body assignments such as
        //   _http = factory.CreateClient("auth-service");
        foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (ctor.Body is null) continue;

            foreach (var assignment in ctor.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var left = assignment.Left switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax ma when ma.Name is IdentifierNameSyntax id =>
                        id.Identifier.ValueText,
                    _ => null
                };

                if (left is null || !httpTypedNames.Contains(left)) continue;

                if (IsCreateClientInitializer(assignment.Right, semanticModel))
                {
                    result.Add(left);
                }
            }
        }

        return result;
    }

    private static bool IsCreateClientInitializer(
        ExpressionSyntax? initializer,
        SemanticModel semanticModel)
    {
        if (initializer is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        if (memberAccess.Name.Identifier.ValueText != "CreateClient")
        {
            return false;
        }

        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is null) return false;

        var containingType = symbol.ContainingType;
        if (containingType is null) return false;
        if (containingType.Name != "IHttpClientFactory") return false;

        // IHttpClientFactory lives in Microsoft.Extensions.Http in most
        // package versions and in System.Net.Http in some .NET layouts.
        // Accept either namespace so the rule works across runtimes.
        var ns = containingType.ContainingNamespace?.ToDisplayString();
        return ns == "Microsoft.Extensions.Http" || ns == "System.Net.Http";
    }

    private static string? GetReceiverName(MemberAccessExpressionSyntax memberAccess)
    {
        return memberAccess.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma when ma.Name is IdentifierNameSyntax id =>
                id.Identifier.ValueText,
            ThisExpressionSyntax => null,
            _ => null
        };
    }

    private static bool IsHttpClientMethod(string name)
    {
        return name is "SendAsync"
            or "GetAsync"
            or "PostAsync"
            or "PutAsync"
            or "DeleteAsync"
            or "PatchAsync"
            or "GetStringAsync"
            or "GetByteArrayAsync"
            or "GetStreamAsync"
            or "GetFromJsonAsync"
            or "PostAsJsonAsync"
            or "PutAsJsonAsync"
            or "DeleteFromJsonAsync";
    }
}
