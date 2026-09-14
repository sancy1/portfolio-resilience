// filepath: src/Portfolio.Resilience.Analyzers/MisconfigurationAnalyzer.cs
// layer: Analyzers | package: Portfolio.Resilience.Analyzers | since: v0.7.0
// purpose: PR0002 - warns when a policy enables a feature with a numeric value that cannot be valid.
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
/// PR0002 - Warns when a policy configured via <c>AddPolicy</c> enables a
/// feature with a numeric setting that cannot be valid.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer inspects the lambda passed to
/// <c>ResilienceBuilder.AddPolicy(name, configure)</c>. It tracks the literal
/// values assigned to specific options in the lambda body. When a feature is
/// enabled and a companion setting is <c>0</c> or negative, a warning is
/// emitted at the offending assignment.
/// </para>
/// <para>
/// The rule only fires when <c>Enabled = true</c> and the misconfigured value
/// appear in the <b>same</b> lambda. A placeholder value such as
/// <c>PermitLimit = 0</c> alone does not warn - it only matters once the
/// feature is enabled.
/// </para>
/// <para>
/// The rule catches literal numeric values only. Values loaded from
/// configuration files are validated at startup by the library's
/// <c>Validate</c> methods, not by this analyzer.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MisconfigurationAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID for the misconfiguration rule.</summary>
    public const string DiagnosticId = "PR0002";

    private const string Category = "Reliability";

    private static readonly LocalizableString Title =
        "Resilience policy is misconfigured";

    private static readonly LocalizableString MessageFormat =
        "Policy enables {0} but sets {1} to {2}. {0} requires {1} > 0 when enabled.";

    private static readonly LocalizableString Description =
        "Enabling a resilience feature with an invalid companion value is a " +
        "configuration error. The library will refuse to operate correctly. " +
        "See the corresponding options type for valid ranges.";

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

        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    // ------------------------------------------------------------------------
    // Analysis
    // ------------------------------------------------------------------------

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Shape: <something>.AddPolicy("name", <lambda>)
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        if (memberAccess.Name.Identifier.ValueText != "AddPolicy")
        {
            return;
        }


        // Confirm the receiver is a ResilienceBuilder.
        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;


        if (receiverType?.Name != "ResilienceBuilder"
            || receiverType.ContainingNamespace?.ToDisplayString() != "Portfolio.Resilience.Configuration")
        {
            return;
        }

        // Find the lambda argument.
        var lambda = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<LambdaExpressionSyntax>()
            .FirstOrDefault();

        if (lambda is null)
        {
            return;
        }

        // Collect all assignments of the form  <something>.<Chain> = <literal>
        // where <Chain> looks like  "RateLimiter.Enabled" etc.
        var assignments = CollectPolicyAssignments(lambda);


        CheckEnabledWithNonPositive(
            context,
            assignments,
            featureName: "RateLimiter",
            enabledKey: "RateLimiter.Enabled",
            valueKey: "RateLimiter.PermitLimit",
            valueName: "PermitLimit");

        CheckEnabledWithNonPositive(
            context,
            assignments,
            featureName: "Bulkhead",
            enabledKey: "Bulkhead.Enabled",
            valueKey: "Bulkhead.MaxConcurrency",
            valueName: "MaxConcurrency");

        CheckEnabledWithNonPositive(
            context,
            assignments,
            featureName: "Hedging",
            enabledKey: "Hedging.Enabled",
            valueKey: "Hedging.MaxAttempts",
            valueName: "MaxAttempts");

        CheckNegativeOnly(
            context,
            assignments,
            key: "Timeout.TimeoutMs",
            valueName: "Timeout.TimeoutMs");

        CheckNegativeOnly(
            context,
            assignments,
            key: "Retry.MaxAttempts",
            valueName: "Retry.MaxAttempts");
    }

    private static List<PolicyAssignment> CollectPolicyAssignments(LambdaExpressionSyntax lambda)
    {
        var result = new List<PolicyAssignment>();
        var body = (SyntaxNode?)lambda.Body;
        if (body is null) return result;

        foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var key = ExtractChain(assignment.Left);
            if (key is null) continue;

            var literal = ExtractLiteralLong(assignment.Right);
            if (literal is null) continue;

            result.Add(new PolicyAssignment(key, literal.Value, assignment.GetLocation()));
        }

        return result;
    }

    /// <summary>
    /// Extracts a dotted chain such as "RateLimiter.Enabled" from an expression
    /// like <c>p.RateLimiter.Enabled</c> or <c>policy.RateLimiter.Enabled</c>.
    /// Returns null if the chain does not have exactly the expected shape.
    /// </summary>
    private static string? ExtractChain(ExpressionSyntax expr)
    {
        var parts = new List<string>();
        var current = expr;

        while (current is MemberAccessExpressionSyntax ma)
        {
            parts.Insert(0, ma.Name.Identifier.ValueText);
            current = ma.Expression;
        }

        if (current is IdentifierNameSyntax)
        {
            // Ignore the root identifier (the parameter name). We only want
            // the sub-chain such as RateLimiter.Enabled.
            return parts.Count >= 2 ? string.Join(".", parts) : null;
        }

        return null;
    }

    /// <summary>
    /// Extracts a numeric value from a literal expression. Boolean literals are
    /// represented as 1 (true) and 0 (false) so callers can compare with a
    /// single numeric check.
    /// </summary>
    private static long? ExtractLiteralLong(ExpressionSyntax expr)
    {
        if (expr is LiteralExpressionSyntax literal)
        {
            if (literal.Token.Value is bool b) return b ? 1 : 0;
            if (literal.Token.Value is int i) return i;
            if (literal.Token.Value is long l) return l;
        }

        // Handle prefix unary minus on a numeric literal.
        if (expr is PrefixUnaryExpressionSyntax unary
            && unary.IsKind(SyntaxKind.UnaryMinusExpression)
            && unary.Operand is LiteralExpressionSyntax inner
            && inner.Token.Value is int innerInt)
        {
            return -innerInt;
        }

        return null;
    }

    private static void CheckEnabledWithNonPositive(
        SyntaxNodeAnalysisContext context,
        List<PolicyAssignment> assignments,
        string featureName,
        string enabledKey,
        string valueKey,
        string valueName)
    {
        var enabled = assignments.FirstOrDefault(a => a.Key == enabledKey);
        if (enabled is null) return;
        if (enabled.Value != 1) return;   // literal true == 1

        var setting = assignments.FirstOrDefault(a => a.Key == valueKey);
        if (setting is null) return;
        if (setting.Value > 0) return;

        var diagnostic = Diagnostic.Create(
            Rule,
            setting.Location,
            featureName,
            valueName,
            setting.Value);

        context.ReportDiagnostic(diagnostic);
    }

    private static void CheckNegativeOnly(
        SyntaxNodeAnalysisContext context,
        List<PolicyAssignment> assignments,
        string key,
        string valueName)
    {
        var setting = assignments.FirstOrDefault(a => a.Key == key);
        if (setting is null) return;
        if (setting.Value >= 0) return;

        var diagnostic = Diagnostic.Create(
            Rule,
            setting.Location,
            "the policy",
            valueName,
            setting.Value);

        context.ReportDiagnostic(diagnostic);
    }

    // netstandard2.0 does not define IsExternalInit, so records are not
    // usable in the analyzer project. A plain class serves the same purpose.
    private sealed class PolicyAssignment
    {
        public PolicyAssignment(string key, long value, Location location)
        {
            Key = key;
            Value = value;
            Location = location;
        }

        public string Key { get; }
        public long Value { get; }
        public Location Location { get; }
    }
}
