// filepath: src/Portfolio.Resilience/Abstractions/IResiliencePolicyRegistry.cs
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// Resolves the effective <see cref="PolicyDefinition"/> for a given name.
/// Unknown names fall back to the DefaultPolicy.
/// </summary>
public interface IResiliencePolicyRegistry
{
    PolicyDefinition Resolve(string policyName);
    IReadOnlyCollection<string> KnownPolicies { get; }
}
