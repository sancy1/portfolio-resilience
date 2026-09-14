// filepath: src/Portfolio.Resilience/Abstractions/IResiliencePolicy.cs
// layer: Abstractions | package: Portfolio.Resilience | since: v0.7.0
// purpose: The contract every policy builder implements, enabling custom pipeline composition.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (interface)
//   Depends on : PolicyDefinition
//   Used by    : ResiliencePipeline, every policy builder, CompositePolicyBuilder
//   See also   : docs/composition.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// A single layer in a resilience pipeline. Every policy builder implements
/// this so pipelines can be composed in any order via <c>ResiliencePipeline.Wrap</c>.
/// </summary>
/// <remarks>
/// <para>
/// Implementations read their own options from the passed
/// <see cref="PolicyDefinition"/>. When a feature is disabled in the definition
/// (for example, <c>RateLimiter.Enabled == false</c>), the implementation must
/// pass through to the operation without adding behavior.
/// </para>
/// <para>
/// Implementations must be thread-safe. The same policy instance may be invoked
/// concurrently for different policy names.
/// </para>
/// <para>
/// This interface is implemented by every builder shipped with the library. Users
/// who want a custom layer may implement it themselves and include it in a
/// composed pipeline.
/// </para>
/// </remarks>
public interface IResiliencePolicy
{
    /// <summary>
    /// Executes <paramref name="operation"/> through this policy layer.
    /// </summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="policyName">
    /// The policy name. Used for logging, metrics, and error reporting.
    /// </param>
    /// <param name="operation">The next layer in the pipeline, or the terminal operation.</param>
    /// <param name="definition">
    /// The full policy definition. Implementations read the options relevant to
    /// their concern from this object.
    /// </param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The result of the operation, after this layer applied its behavior.</returns>
    /// <exception cref="ArgumentNullException">
    /// If <paramref name="operation"/> or <paramref name="definition"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// If <paramref name="policyName"/> is null or whitespace.
    /// </exception>
    Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct = default);
}
