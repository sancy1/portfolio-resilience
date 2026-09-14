// filepath: src/Portfolio.Resilience/Policies/TimeoutPolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.7.0
// purpose: Wraps an async operation with a timeout ceiling that throws a typed ResilienceException.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : IResiliencePolicy (v0.7.0)
//   Depends on : TimeoutOptions, ResilienceException, ResilienceErrorCategory, PolicyDefinition
//   Used by    : CompositePolicyBuilder, ResilienceExecutor, ResiliencePipeline
//   See also   : docs/timeout.md, docs/composition.md, SPEC.md section 4
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an async operation with a timeout ceiling. If the operation does not
/// complete within the ceiling, its cancellation token is signaled and a
/// <see cref="ResilienceException"/> with category
/// <see cref="ResilienceErrorCategory.Timeout"/> is thrown.
/// </summary>
/// <remarks>
/// A ceiling of zero or negative disables the timeout - the operation runs to
/// completion or until the caller's own token fires. This is deliberate: services
/// that want no timeout configure <c>TimeoutMs = 0</c>.
/// </remarks>
public sealed class TimeoutPolicyBuilder : IResiliencePolicy
{
    /// <summary>
    /// Executes <paramref name="operation"/> with the timeout configured in
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="options">Timeout tuning. <c>TimeoutMs &lt;= 0</c> disables the ceiling.</param>
    /// <param name="policyName">Policy name for logging and error reporting.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The operation's result if it completes in time.</returns>
    /// <exception cref="ArgumentNullException">If operation or options is null.</exception>
    /// <exception cref="ResilienceException">
    /// With category <see cref="ResilienceErrorCategory.Timeout"/> if the ceiling fires.
    /// </exception>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeoutOptions options,
        string policyName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        // Determine the effective ceiling. When a time-budget scope is active,
        // the effective ceiling is min(configured, remaining budget). When no
        // budget is active, the configured value is used as-is (v0.7.0 behavior).
        var remaining = TimeBudgetContext.RemainingMs;
        int effectiveTimeoutMs;

        if (remaining is not null)
        {
            // Budget is active. Cap by remaining budget. When the configured
            // timeout is positive, take the smaller value; when it is disabled
            // (<= 0), the budget becomes the caller's true SLA.
            var configured = options.TimeoutMs > 0 ? options.TimeoutMs : int.MaxValue;
            var remainingMs = (int)Math.Min(int.MaxValue, Math.Max(0, remaining.Value));

            if (remainingMs <= 0)
            {
                // Budget already exhausted - no room for the operation.
                throw new ResilienceException(
                    message: $"Time budget exhausted before operation could run (policy '{policyName}').",
                    policyName: policyName,
                    category: ResilienceErrorCategory.Timeout,
                    attemptsMade: 0,
                    totalDuration: TimeSpan.Zero,
                    metadata: new Dictionary<string, object?>
                    {
                        ["remaining_ms"] = remaining.Value
                    });
            }

            effectiveTimeoutMs = Math.Min(configured, remainingMs);
        }
        else if (options.TimeoutMs <= 0)
        {
            // Timeout disabled and no budget - run directly with the caller's token.
            return await operation(ct).ConfigureAwait(false);
        }
        else
        {
            effectiveTimeoutMs = options.TimeoutMs;
        }

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        timeoutCts.CancelAfter(effectiveTimeoutMs);

        var startedAt = DateTime.UtcNow;

        try
        {
            return await operation(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Caller's token fired first - real cancellation, rethrow.
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // Our timer fired - surface a typed timeout.
            var elapsed = DateTime.UtcNow - startedAt;

            throw new ResilienceException(
                message: $"Operation exceeded timeout of {effectiveTimeoutMs}ms (policy '{policyName}').",
                policyName: policyName,
                category: ResilienceErrorCategory.Timeout,
                attemptsMade: 1,
                totalDuration: elapsed,
                metadata: new Dictionary<string, object?>
                {
                    ["timeout_ms"] = effectiveTimeoutMs,
                    ["elapsed_ms"] = elapsed.TotalMilliseconds
                });
        }
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
        => ExecuteAsync(operation, definition.Timeout, policyName, ct);
}
