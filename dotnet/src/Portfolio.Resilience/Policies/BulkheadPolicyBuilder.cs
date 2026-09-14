// filepath: src/Portfolio.Resilience/Policies/BulkheadPolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.6.0
// purpose: Caps concurrent calls to a resource via a semaphore with a bounded waiter queue.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (concrete builder)
//   Depends on : BulkheadOptions, ResilienceException, ResilienceEventEmitter
//   Used by    : CompositePolicyBuilder
//   See also   : docs/bulkhead.md, SPEC.md section 13
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using System.Collections.Concurrent;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Implementation;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an async operation under a bulkhead. A bulkhead caps the number of
/// operations that may run concurrently against a resource and bounds the
/// number of callers waiting for a slot.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe. State is per-policy. The concurrency cap is a
/// <see cref="SemaphoreSlim"/> held across the operation and released in a
/// <c>finally</c> block, so slots are returned whether the operation succeeds
/// or fails.
/// </para>
/// <para>
/// Rejections emit a <see cref="Events.ResilienceEventType.BulkheadRejected"/>
/// event through the injected emitter and throw <see cref="ResilienceException"/>
/// with the configured <see cref="BulkheadOptions.RejectionCategory"/>.
/// </para>
/// </remarks>
public sealed class BulkheadPolicyBuilder : IResiliencePolicy
{
    private readonly ResilienceEventEmitter _emitter;
    private readonly ConcurrentDictionary<string, BulkheadState> _states
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a bulkhead builder.
    /// </summary>
    /// <param name="emitter">Optional event emitter. Defaults to a silent emitter.</param>
    public BulkheadPolicyBuilder(ResilienceEventEmitter? emitter = null)
    {
        _emitter = emitter ?? new ResilienceEventEmitter();
    }

    /// <summary>
    /// Executes <paramref name="operation"/> under the bulkhead for
    /// <paramref name="policyName"/>.
    /// </summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="policyName">The policy name whose bulkhead gates this call.</param>
    /// <param name="operation">The operation to execute if a slot is acquired.</param>
    /// <param name="options">Bulkhead tuning for this execution.</param>
    /// <param name="ct">Cancellation token propagated to the operation.</param>
    /// <returns>The operation's result on success.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="operation"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
    /// <exception cref="ResilienceException">With the configured <see cref="BulkheadOptions.RejectionCategory"/> when the bulkhead rejects the call.</exception>
    public async Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        BulkheadOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        // Disabled bulkhead: pass-through.
        if (!options.Enabled)
        {
            return await operation(ct).ConfigureAwait(false);
        }

        var state = _states.GetOrAdd(policyName, _ => new BulkheadState(options.MaxConcurrency));

        // Fast path: try to enter immediately without waiting.
        if (state.Semaphore.Wait(0))
        {
            return await RunAndReleaseAsync(state, operation, ct).ConfigureAwait(false);
        }

        // No queue allowed: reject immediately.
        if (options.MaxQueue == 0)
        {
            Reject(policyName, options, queueDepth: 0, reason: "rejected_immediately");
        }

        // Queue allowed: reserve a waiter slot, or reject if the queue is full.
        if (Interlocked.Increment(ref state.WaitingCount) > options.MaxQueue)
        {
            Interlocked.Decrement(ref state.WaitingCount);
            Reject(policyName, options, state.WaitingCount, reason: "queue_full");
        }

        try
        {
            var acquired = await state.Semaphore
                .WaitAsync(TimeSpan.FromMilliseconds(options.QueueTimeoutMs), ct)
                .ConfigureAwait(false);

            if (!acquired)
            {
                Reject(policyName, options, state.WaitingCount, reason: "queue_timeout");
            }

            return await RunAndReleaseAsync(state, operation, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref state.WaitingCount);
        }
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private static async Task<T> RunAndReleaseAsync<T>(
        BulkheadState state,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    private void Reject(string policyName, BulkheadOptions options, int queueDepth, string reason)
    {
        _emitter.EmitBulkheadRejected(
            policyName: policyName,
            maxConcurrency: options.MaxConcurrency,
            maxQueue: options.MaxQueue,
            queueDepth: queueDepth,
            reason: reason);

        throw new ResilienceException(
            message: $"Bulkhead rejected call for policy '{policyName}': {reason}.",
            policyName: policyName,
            category: options.RejectionCategory,
            attemptsMade: 0,
            totalDuration: TimeSpan.Zero,
            metadata: new Dictionary<string, object?>
            {
                ["max_concurrency"] = options.MaxConcurrency,
                ["max_queue"] = options.MaxQueue,
                ["queue_depth"] = queueDepth,
                ["reason"] = reason
            });
    }

    // ------------------------------------------------------------------------
    // IResiliencePolicy
    // ------------------------------------------------------------------------

    /// <inheritdoc />
    Task<T> IResiliencePolicy.ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct)
        => ExecuteAsync(policyName, operation, definition.Bulkhead, ct);

    // ------------------------------------------------------------------------
    // Types
    // ------------------------------------------------------------------------

    /// <summary>
    /// Per-policy bulkhead state: a semaphore sized to the concurrency cap,
    /// plus a counter of active waiters.
    /// </summary>
    private sealed class BulkheadState
    {
        public SemaphoreSlim Semaphore { get; }
        public int WaitingCount;

        public BulkheadState(int maxConcurrency)
        {
            Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }
    }
}
