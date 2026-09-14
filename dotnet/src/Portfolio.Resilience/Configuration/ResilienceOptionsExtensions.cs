// filepath: src/Portfolio.Resilience/Configuration/ResilienceOptionsExtensions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.8.0
// purpose: Convenience queries over ResilienceOptions - used by DI wiring to decide whether to enable scrubbing.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : ResilienceOptions, PolicyDefinition, LoggingOptions
//   Used by    : ServiceCollectionExtensions
//   See also   : docs/pci-scrubbing.md, SPEC.md section 19
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Read-only helpers over <see cref="ResilienceOptions"/>.
/// </summary>
public static class ResilienceOptionsExtensions
{
    /// <summary>
    /// Returns true when the default policy or any named policy has
    /// <see cref="LoggingOptions.ScrubSensitiveData"/> set to true.
    /// </summary>
    /// <param name="options">The options to inspect. Must not be null.</param>
    /// <returns>True if any policy opts in to sensitive-data scrubbing.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="options"/> is null.</exception>
    public static bool AnyPolicyScrubsSensitiveData(this ResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.DefaultPolicy.Logging.ScrubSensitiveData)
        {
            return true;
        }

        foreach (var policy in options.Policies.Values)
        {
            if (policy.Logging.ScrubSensitiveData)
            {
                return true;
            }
        }

        return false;
    }
}
