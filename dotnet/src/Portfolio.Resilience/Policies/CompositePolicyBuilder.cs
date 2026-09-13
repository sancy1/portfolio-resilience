// filepath: src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.6.0
// purpose: Chains RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout for a single operation.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (orchestrator)
//   Depends on : RateLimiterPolicyBuilder, BulkheadPolicyBuilder, RetryPolicyBuilder,
//                CircuitPolicyBuilder, TimeoutPolicyBuilder, PolicyDefinition
//   Used by    : ResilienceExecutor (Stage F)
//   See also   : docs/executor.md, docs/rate-limiter.md, docs/bulkhead.md, SPEC.md section 12, SPEC.md section 13
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an operation through the full resilience pipeline:
/// <code>
/// Caller -> RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout -> Operation
/// </code>
/// </summary>
/// <remarks>
/// Order rationale:
/// <list type="bullet">
///   <item><b>RateLimiter outermost</b>: reject before burning a retry slot or a concurrency slot.</item>
///   <item><b>Bulkhead next</b>: cap concurrency before the retry loop multiplies load.</item>
///   <item><b>Retry</b>: each retry attempt gets a fresh circuit check and a fresh timeout budget.</item>
///   <item><b>Circuit</b>: when the circuit is Open, reject before starting a timeout - no wasted ceiling.</item>
///   <item><b>Timeout innermost</b>: bounds a single attempt; retries multiply the total time accordingly.</item>
/// </list>
/// The rate limiter and bulkhead layers are only applied when their respective
/// <c>Enabled</c> flags are true. When a layer is enabled but the corresponding
/// builder was not provided to this instance, execution throws
/// <see cref="InvalidOperationException"/> rather than silently skipping enforcement.
/// </remarks>
public sealed class CompositePolicyBuilder
{
    private readonly RetryPolicyBuilder _retry;
    private readonly CircuitPolicyBuilder _circuit;
    private readonly TimeoutPolicyBuilder _timeout;
    private readonly RateLimiterPolicyBuilder? _rateLimiter;
    private readonly BulkheadPolicyBuilder? _bulkhead;

    /// <summary>
    /// Creates a composite pipeline.
    /// </summary>
    /// <param name="retry">Retry builder. Defaults to a new instance.</param>
    /// <param name="circuit">Circuit builder. Defaults to a new instance.</param>
    /// <param name="timeout">Timeout builder. Defaults to a new instance.</param>
    /// <param name="rateLimiter">Optional rate limiter builder. Required only if any policy enables the rate limiter.</param>
    /// <param name="bulkhead">Optional bulkhead builder. Required only if any policy enables the bulkhead.</param>
    public CompositePolicyBuilder(
        RetryPolicyBuilder? retry = null,
        CircuitPolicyBuilder? circuit = null,
        TimeoutPolicyBuilder? timeout = null,
        RateLimiterPolicyBuilder? rateLimiter = null,
        BulkheadPolicyBuilder? bulkhead = null)
    {
        _retry = retry ?? new RetryPolicyBuilder();
        _circuit = circuit ?? new CircuitPolicyBuilder();
        _timeout = timeout ?? new TimeoutPolicyBuilder();
        _rateLimiter = rateLimiter;
        _bulkhead = bulkhead;
    }

    /// <summary>Exposes the underlying circuit builder for health snapshots.</summary>
    public CircuitPolicyBuilder Circuit => _circuit;

    /// <summary>
    /// Executes <paramref name="operation"/> through the full pipeline using
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
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(definition);

        var policyName = definition.Name;

        // Build the pipeline from the inside out.
        // Innermost: retry -> circuit -> timeout -> operation.
        Func<CancellationToken, Task<T>> pipelined = async attemptCt =>
        {
            // Outermost of the existing three: retry loop. Each attempt runs the inner pipeline fresh.
            return await _retry.ExecuteAsync(
                operation: async retryCt =>
                {
                    // Middle: circuit gate. May throw ResilienceException(CircuitOpen) before the operation runs.
                    return await _circuit.ExecuteAsync(
                        policyName: policyName,
                        operation: async gateCt =>
                        {
                            // Innermost: timeout ceiling on the single attempt.
                            return await _timeout.ExecuteAsync(
                                operation: operation,
                                options: definition.Timeout,
                                policyName: policyName,
                                ct: gateCt);
                        },
                        options: definition.Circuit,
                        ct: retryCt);
                },
                options: definition.Retry,
                ct: attemptCt);
        };

        // Bulkhead sits between the rate limiter and retry.
        if (definition.Bulkhead.Enabled)
        {
            if (_bulkhead is null)
            {
                throw new InvalidOperationException(
                    $"Bulkhead enabled for policy '{policyName}' but no BulkheadPolicyBuilder was provided to CompositePolicyBuilder.");
            }

            var inner = pipelined;
            pipelined = async wrappedCt =>
                await _bulkhead.ExecuteAsync(
                    policyName: policyName,
                    operation: inner,
                    options: definition.Bulkhead,
                    ct: wrappedCt);
        }

        // Rate limiter sits outermost: reject before burning a concurrency slot or retry slot.
        if (definition.RateLimiter.Enabled)
        {
            if (_rateLimiter is null)
            {
                throw new InvalidOperationException(
                    $"Rate limiter enabled for policy '{policyName}' but no RateLimiterPolicyBuilder was provided to CompositePolicyBuilder.");
            }

            var inner = pipelined;
            pipelined = async wrappedCt =>
                await _rateLimiter.ExecuteAsync(
                    policyName: policyName,
                    operation: inner,
                    options: definition.RateLimiter,
                    ct: wrappedCt);
        }

        return await pipelined(ct).ConfigureAwait(false);
    }
}
