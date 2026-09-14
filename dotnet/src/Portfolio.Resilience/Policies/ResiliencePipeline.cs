// filepath: src/Portfolio.Resilience/Policies/ResiliencePipeline.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.7.0
// purpose: Composes an ordered sequence of IResiliencePolicy layers into a single pipeline.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (composition container)
//   Depends on : IResiliencePolicy, PolicyDefinition
//   Used by    : ResilienceExecutor, CompositePolicyBuilder, ResiliencePipelineBuilder
//   See also   : docs/composition.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// An ordered sequence of <see cref="IResiliencePolicy"/> layers, executed
/// outermost-first. Each layer wraps the next, and the innermost layer invokes
/// the caller's operation.
/// </summary>
/// <remarks>
/// <para>
/// The order of layers is significant. A pipeline built as
/// <c>Wrap(rateLimiter, bulkhead, retry)</c> executes the rate limiter first,
/// then the bulkhead, then retry, then the operation. If the rate limiter
/// rejects the call, the bulkhead and retry are never invoked.
/// </para>
/// <para>
/// Instances are immutable after construction. The same pipeline may be
/// executed concurrently for different policy names.
/// </para>
/// </remarks>
public sealed class ResiliencePipeline
{
    private readonly IResiliencePolicy[] _layers;

    /// <summary>
    /// Creates a pipeline from the given ordered layers. The first layer is
    /// outermost.
    /// </summary>
    /// <param name="layers">
    /// The ordered policy layers. Must contain at least one layer.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="layers"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="layers"/> is empty or contains a null element.</exception>
    public ResiliencePipeline(params IResiliencePolicy[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        if (layers.Length == 0)
        {
            throw new ArgumentException(
                "A pipeline requires at least one layer.",
                nameof(layers));
        }

        for (var i = 0; i < layers.Length; i++)
        {
            if (layers[i] is null)
            {
                throw new ArgumentException(
                    $"Pipeline layer at index {i} is null.",
                    nameof(layers));
            }
        }

        _layers = (IResiliencePolicy[])layers.Clone();
    }

    /// <summary>
    /// Creates a pipeline from the given ordered layers. Alias for the
    /// constructor, matching the fluent naming convention used by other
    /// resilience libraries.
    /// </summary>
    /// <param name="layers">The ordered policy layers. The first is outermost.</param>
    /// <returns>A new <see cref="ResiliencePipeline"/>.</returns>
    public static ResiliencePipeline Wrap(params IResiliencePolicy[] layers)
        => new(layers);

    /// <summary>
    /// Creates a pipeline with the payment-safe layer order:
    /// <code>
    /// RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preset enforces <b>order</b>, not enablement. Each layer honors the
    /// corresponding flag in the caller's <see cref="Configuration.PolicyDefinition"/>:
    /// a policy with <c>Hedging.Enabled = false</c> will not hedge even though
    /// the hedging layer is present in the pipeline.
    /// </para>
    /// <para>
    /// <b>Why the order matters.</b> A mis-ordered pipeline such as
    /// <c>Wrap(retry, hedge, circuit, timeout)</c> races multiple retried and
    /// hedged attempts against a charge endpoint - catastrophic for
    /// non-idempotent writes. The preset places the circuit <i>outside</i>
    /// hedging and retry, so a fail-fast rejection from the circuit never
    /// spawns additional attempts. It also places the rate limiter and bulkhead
    /// outermost, so capacity caps apply before retry can multiply load.
    /// </para>
    /// <para>
    /// For most services, the default pipeline
    /// (<c>RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout</c>)
    /// remains the right choice. Use this preset on any critical write path -
    /// charges, orders, refunds, event publishes - where an accidental
    /// multi-attempt race would be harmful.
    /// </para>
    /// </remarks>
    /// <returns>A new pipeline with the safe order and fresh layer instances.</returns>
    public static ResiliencePipeline WithPaymentSafeDefaults()
        => WithPaymentSafeDefaults(
            rateLimiter: new RateLimiterPolicyBuilder(),
            bulkhead: new BulkheadPolicyBuilder(),
            circuit: new CircuitPolicyBuilder(),
            hedging: new HedgingPolicyBuilder(),
            retry: new RetryPolicyBuilder(),
            timeout: new TimeoutPolicyBuilder());

    /// <summary>
    /// Creates a pipeline with the payment-safe layer order, using the
    /// supplied layer instances. Any null argument is replaced with a fresh
    /// instance, so callers may supply only the layers they want to customize.
    /// </summary>
    /// <param name="rateLimiter">Optional rate limiter. Fresh instance if null.</param>
    /// <param name="bulkhead">Optional bulkhead. Fresh instance if null.</param>
    /// <param name="circuit">Optional circuit. Fresh instance if null.</param>
    /// <param name="hedging">Optional hedging. Fresh instance if null.</param>
    /// <param name="retry">Optional retry. Fresh instance if null.</param>
    /// <param name="timeout">Optional timeout. Fresh instance if null.</param>
    /// <returns>A new pipeline with the safe order.</returns>
    public static ResiliencePipeline WithPaymentSafeDefaults(
        RateLimiterPolicyBuilder? rateLimiter = null,
        BulkheadPolicyBuilder? bulkhead = null,
        CircuitPolicyBuilder? circuit = null,
        HedgingPolicyBuilder? hedging = null,
        RetryPolicyBuilder? retry = null,
        TimeoutPolicyBuilder? timeout = null)
        => new(
            rateLimiter ?? new RateLimiterPolicyBuilder(),
            bulkhead ?? new BulkheadPolicyBuilder(),
            circuit ?? new CircuitPolicyBuilder(),
            hedging ?? new HedgingPolicyBuilder(),
            retry ?? new RetryPolicyBuilder(),
            timeout ?? new TimeoutPolicyBuilder());

    /// <summary>The ordered layers, outermost first.</summary>
    public IReadOnlyList<IResiliencePolicy> Layers => _layers;

    /// <summary>
    /// Executes <paramref name="operation"/> through every layer, outermost first.
    /// </summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="policyName">The policy name. Passed to every layer.</param>
    /// <param name="operation">The terminal operation, invoked after all layers.</param>
    /// <param name="definition">The full policy definition. Passed to every layer.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The operation's result, after every layer applied its behavior.</returns>
    /// <exception cref="ArgumentNullException">
    /// If <paramref name="operation"/> or <paramref name="definition"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// If <paramref name="policyName"/> is null or whitespace.
    /// </exception>
    public Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(definition);

        // Build the chain from innermost out: each layer wraps the previous one.
        Func<CancellationToken, Task<T>> chain = operation;

        for (var i = _layers.Length - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            var inner = chain;

            chain = layerCt => layer.ExecuteAsync(
                policyName: policyName,
                operation: inner,
                definition: definition,
                ct: layerCt);
        }

        return chain(ct);
    }
}
