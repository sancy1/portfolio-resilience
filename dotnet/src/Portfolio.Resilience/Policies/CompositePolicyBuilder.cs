// filepath: src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.2.0
// purpose: Chains retry + circuit + timeout in the correct order for a single operation.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (orchestrator)
//   Depends on : RetryPolicyBuilder, CircuitPolicyBuilder, TimeoutPolicyBuilder, PolicyDefinition
//   Used by    : ResilienceExecutor (Stage F)
//   See also   : docs/executor.md, SPEC.md §PolicyComposition
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an operation through the full resilience pipeline:
/// <code>
/// Caller -> Retry -> Circuit -> Timeout -> Operation
/// </code>
/// </summary>
/// <remarks>
/// Order rationale:
/// <list type="bullet">
///   <item><b>Retry outermost</b>: each retry attempt gets a fresh circuit check and a fresh timeout budget.</item>
///   <item><b>Circuit middle</b>: when the circuit is Open, we reject before starting a timeout — no wasted ceiling.</item>
///   <item><b>Timeout innermost</b>: bounds a single attempt; retries multiply the total time accordingly.</item>
/// </list>
/// </remarks>
public sealed class CompositePolicyBuilder
{
    private readonly RetryPolicyBuilder _retry;
    private readonly CircuitPolicyBuilder _circuit;
    private readonly TimeoutPolicyBuilder _timeout;

    public CompositePolicyBuilder(
        RetryPolicyBuilder? retry = null,
        CircuitPolicyBuilder? circuit = null,
        TimeoutPolicyBuilder? timeout = null)
    {
        _retry = retry ?? new RetryPolicyBuilder();
        _circuit = circuit ?? new CircuitPolicyBuilder();
        _timeout = timeout ?? new TimeoutPolicyBuilder();
    }

    /// <summary>Exposes the underlying circuit builder for health snapshots.</summary>
    public CircuitPolicyBuilder Circuit => _circuit;
    /// <summary>
    /// Executes <paramref name="operation"/> through the full pipeline using
    /// the provided <paramref name="definition"/>.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(definition);

        var policyName = definition.Name;

        // Outermost: retry loop. Each attempt runs the inner pipeline fresh.
        return await _retry.ExecuteAsync(
            operation: async attemptCt =>
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
                    ct: attemptCt);
            },
            options: definition.Retry,
            ct: ct);
    }
}
