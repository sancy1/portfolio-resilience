// filepath: src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.7.0
// purpose: Composes the default resilience pipeline (RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout).
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (orchestrator)
//   Depends on : IResiliencePolicy, ResiliencePipeline, RateLimiterPolicyBuilder,
//                BulkheadPolicyBuilder, HedgingPolicyBuilder, RetryPolicyBuilder,
//                CircuitPolicyBuilder, TimeoutPolicyBuilder, PolicyDefinition
//   Used by    : ResilienceExecutor, ServiceCollectionExtensions
//   See also   : docs/executor.md, docs/composition.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an operation through the default resilience pipeline:
/// <code>
/// Caller -> RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Layers are enabled individually via <see cref="PolicyDefinition"/>. The rate
/// limiter, bulkhead, and hedging layers are applied only when their respective
/// <c>Enabled</c> flags are true. Retry, circuit, and timeout are always applied - their
/// behavior is controlled by their own options (for example,
/// <c>Retry.MaxAttempts = 0</c> disables retry).
/// </para>
/// <para>
/// If a policy enables the rate limiter or bulkhead but the corresponding builder
/// was not provided to this instance, execution throws
/// <see cref="InvalidOperationException"/> rather than silently skipping enforcement.
/// </para>
/// <para>
/// For custom pipeline orders, use <see cref="ResiliencePipeline"/> or
/// <see cref="ResiliencePipelineBuilder"/> directly. This type exists to provide
/// the library's default pipeline order.
/// </para>
/// </remarks>
public sealed class CompositePolicyBuilder
{
    private readonly RetryPolicyBuilder _retry;
    private readonly CircuitPolicyBuilder _circuit;
    private readonly TimeoutPolicyBuilder _timeout;
    private readonly RateLimiterPolicyBuilder? _rateLimiter;
    private readonly BulkheadPolicyBuilder? _bulkhead;
    private readonly HedgingPolicyBuilder? _hedging;

    /// <summary>
    /// Creates a composite pipeline.
    /// </summary>
    /// <param name="retry">Retry builder. Defaults to a new instance.</param>
    /// <param name="circuit">Circuit builder. Defaults to a new instance.</param>
    /// <param name="timeout">Timeout builder. Defaults to a new instance.</param>
    /// <param name="rateLimiter">Optional rate limiter builder. Required only if any policy enables the rate limiter.</param>
    /// <param name="bulkhead">Optional bulkhead builder. Required only if any policy enables the bulkhead.</param>
    /// <param name="hedging">Optional hedging builder. Required only if any policy enables hedging.</param>
    public CompositePolicyBuilder(
        RetryPolicyBuilder? retry = null,
        CircuitPolicyBuilder? circuit = null,
        TimeoutPolicyBuilder? timeout = null,
        RateLimiterPolicyBuilder? rateLimiter = null,
        BulkheadPolicyBuilder? bulkhead = null,
        HedgingPolicyBuilder? hedging = null)
    {
        _retry = retry ?? new RetryPolicyBuilder();
        _circuit = circuit ?? new CircuitPolicyBuilder();
        _timeout = timeout ?? new TimeoutPolicyBuilder();
        _rateLimiter = rateLimiter;
        _bulkhead = bulkhead;
        _hedging = hedging;
    }

    /// <summary>Exposes the underlying circuit builder for health snapshots.</summary>
    public CircuitPolicyBuilder Circuit => _circuit;

    /// <summary>
    /// Executes <paramref name="operation"/> through the default pipeline using
    /// the provided <paramref name="definition"/>.
    /// </summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="definition">The policy definition whose options drive each layer.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The operation's result on success.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="operation"/> or <paramref name="definition"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// If the policy enables the rate limiter or bulkhead but the corresponding builder was not provided.
    /// </exception>
    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(definition);

        var policyName = definition.Name;

        // Fail-loud checks: a policy that enables a feature must be given the
        // builder for that feature. Silently skipping enforcement is worse than
        // a clear exception at first call.
        if (definition.RateLimiter.Enabled && _rateLimiter is null)
        {
            throw new InvalidOperationException(
                $"Rate limiter enabled for policy '{policyName}' but no RateLimiterPolicyBuilder was provided to CompositePolicyBuilder.");
        }

        if (definition.Bulkhead.Enabled && _bulkhead is null)
        {
            throw new InvalidOperationException(
                $"Bulkhead enabled for policy '{policyName}' but no BulkheadPolicyBuilder was provided to CompositePolicyBuilder.");
        }

        // Build the effective layer list from what this policy actually enables.
        // Order is outermost-first: rate limiter, bulkhead, retry, circuit, timeout.
        var layers = new List<IResiliencePolicy>(capacity: 5);

        if (definition.RateLimiter.Enabled)
        {
            layers.Add(_rateLimiter!);
        }

        if (definition.Bulkhead.Enabled)
        {
            layers.Add(_bulkhead!);
        }

        if (definition.Hedging.Enabled)
        {
            if (_hedging is null)
            {
                throw new InvalidOperationException(
                    $"Hedging enabled for policy '{policyName}' but no HedgingPolicyBuilder was provided to CompositePolicyBuilder.");
            }
            layers.Add(_hedging);
        }

        layers.Add(_retry);
        layers.Add(_circuit);
        layers.Add(_timeout);

        var pipeline = new ResiliencePipeline(layers.ToArray());

        return pipeline.ExecuteAsync(policyName, operation, definition, ct);
    }
}
