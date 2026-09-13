// filepath: src/Portfolio.Resilience/Abstractions/IResiliencePolicyRegistry.cs
// layer: Abstractions | package: Portfolio.Resilience | since: v0.6.0
// purpose: Resolves the effective PolicyDefinition for a given name.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (interface)
//   Depends on : PolicyDefinition
//   Used by    : ResilienceExecutor, tests
//   See also   : docs/executor.md, SPEC.md section 2
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// Resolves the effective <see cref="PolicyDefinition"/> for a given name.
/// Unknown names fall back to the DefaultPolicy, with the Name overridden to
/// the requested name. Implementations must be case-insensitive.
/// </summary>
public interface IResiliencePolicyRegistry
{
    /// <summary>
    /// Returns the effective policy for <paramref name="policyName"/>.
    /// The returned definition is a clone — callers may mutate it freely.
    /// </summary>
    PolicyDefinition Resolve(string policyName);

    /// <summary>Names of all explicitly-registered policies.</summary>
    IReadOnlyCollection<string> KnownPolicies { get; }
}
