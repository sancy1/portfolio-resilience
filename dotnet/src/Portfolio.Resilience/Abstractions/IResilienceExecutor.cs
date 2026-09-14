// filepath: src/Portfolio.Resilience/Abstractions/IResilienceExecutor.cs
// layer: Abstractions | package: Portfolio.Resilience | since: v0.8.0
// purpose: The main entry point. Runs operations through the resilience pipeline with fallback, idempotency, and time-budget support.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (interface)
//   Depends on : CorrelationContext, IdempotencyContext, TimeBudgetContext
//   Used by    : every service that needs resilient calls (via DI)
//   See also   : docs/executor.md, docs/idempotency.md, docs/time-budget.md, SPEC.md section 18, SPEC.md section 20
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// The main entry point. Every external call in every service goes through one of these methods.
/// Retries, circuit breaking, timeout, fallback, idempotency-key propagation, time-budget
/// enforcement, structured logging, and latency tracking all happen inside.
/// </summary>
public interface IResilienceExecutor
{
    /// <summary>Execute an async operation that returns a value.</summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="policyName">The policy name as registered with <c>AddPolicy</c>.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="fallback">Optional per-call fallback invoked when the pipeline fails.</param>
    /// <param name="idempotencyKey">
    /// Optional idempotency key. When provided, every attempt of this call carries the same
    /// key via <see cref="Correlation.IdempotencyContext"/>. When null, the executor generates
    /// one derived from the correlation ID, so retries and hedged attempts remain deduplicable.
    /// </param>
    /// <param name="timeBudgetMs">
    /// Optional total wall-clock budget in milliseconds for the entire pipeline, across all
    /// retries and hedges. When null, behavior is unchanged (each layer uses its own configured
    /// ceiling). When set, each layer caps itself to the remaining budget.
    /// </param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The operation's result on success.</returns>
    Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task<T>>? fallback = null,
        string? idempotencyKey = null,
        int? timeBudgetMs = null,
        CancellationToken ct = default);

    /// <summary>Execute an async operation that returns no value.</summary>
    /// <param name="policyName">The policy name as registered with <c>AddPolicy</c>.</param>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="fallback">Optional per-call fallback invoked when the pipeline fails.</param>
    /// <param name="idempotencyKey">Optional idempotency key; see the generic overload.</param>
    /// <param name="timeBudgetMs">Optional total wall-clock budget in milliseconds; see the generic overload.</param>
    /// <param name="ct">Caller cancellation token.</param>
    Task ExecuteAsync(
        string policyName,
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task>? fallback = null,
        string? idempotencyKey = null,
        int? timeBudgetMs = null,
        CancellationToken ct = default);
}
