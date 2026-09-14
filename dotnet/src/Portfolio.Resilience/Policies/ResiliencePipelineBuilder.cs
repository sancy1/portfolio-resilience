// filepath: src/Portfolio.Resilience/Policies/ResiliencePipelineBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.7.0
// purpose: Fluent builder for composing an ordered ResiliencePipeline.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (mutable builder)
//   Depends on : IResiliencePolicy, ResiliencePipeline
//   Used by    : user code and ServiceCollectionExtensions for custom pipelines
//   See also   : docs/composition.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Fluent builder for a <see cref="ResiliencePipeline"/>. Layers are added in
/// order; the first added is outermost.
/// </summary>
/// <remarks>
/// <para>
/// The builder is mutable and not thread-safe. Create one, configure it, call
/// <see cref="Build"/>, and use the resulting immutable pipeline. Do not share
/// a builder instance across threads.
/// </para>
/// <para>
/// Typical use:
/// <code>
/// var pipeline = new ResiliencePipelineBuilder()
///     .WithName("external-api-pipeline")
///     .Add(rateLimiter)
///     .Add(bulkhead)
///     .AddIf(isProduction, retry)
///     .Add(circuit)
///     .Add(timeout)
///     .Build();
/// </code>
/// </para>
/// </remarks>
public sealed class ResiliencePipelineBuilder
{
    private readonly List<IResiliencePolicy> _layers = new();
    private string? _name;

    /// <summary>
    /// Sets a human-readable name for the pipeline. Used for diagnostics and
    /// error messages. Optional.
    /// </summary>
    /// <param name="name">The pipeline name. Must be non-empty if provided.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">If <paramref name="name"/> is null or whitespace.</exception>
    public ResiliencePipelineBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        return this;
    }

    /// <summary>
    /// Appends a policy layer to the pipeline. Layers are applied outermost-first
    /// in the order they are added.
    /// </summary>
    /// <param name="policy">The layer to add. Must not be null.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="policy"/> is null.</exception>
    public ResiliencePipelineBuilder Add(IResiliencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _layers.Add(policy);
        return this;
    }

    /// <summary>
    /// Appends a policy layer only when <paramref name="condition"/> is true.
    /// Useful for configuration-driven pipelines that vary by environment.
    /// </summary>
    /// <param name="condition">When false, the layer is not added.</param>
    /// <param name="policy">The layer to add when <paramref name="condition"/> is true. Must not be null.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="policy"/> is null.</exception>
    public ResiliencePipelineBuilder AddIf(bool condition, IResiliencePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (condition)
        {
            _layers.Add(policy);
        }
        return this;
    }

    /// <summary>
    /// The current number of layers in the builder.
    /// </summary>
    public int Count => _layers.Count;

    /// <summary>
    /// The name set via <see cref="WithName"/>, or null if not set.
    /// </summary>
    public string? Name => _name;

    /// <summary>
    /// Builds an immutable <see cref="ResiliencePipeline"/> from the configured layers.
    /// </summary>
    /// <returns>A new pipeline instance.</returns>
    /// <exception cref="InvalidOperationException">If no layers have been added.</exception>
    public ResiliencePipeline Build()
    {
        if (_layers.Count == 0)
        {
            throw new InvalidOperationException(
                _name is null
                    ? "Cannot build a pipeline with no layers."
                    : $"Cannot build pipeline '{_name}' with no layers.");
        }

        return new ResiliencePipeline(_layers.ToArray());
    }
}
