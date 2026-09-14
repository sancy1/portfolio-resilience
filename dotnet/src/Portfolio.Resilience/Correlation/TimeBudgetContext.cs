// filepath: src/Portfolio.Resilience/Correlation/TimeBudgetContext.cs
// layer: Correlation | package: Portfolio.Resilience | since: v0.8.0
// purpose: Ambient async-safe storage for the wall-clock deadline of the current pipeline call.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Provides   : Ambient total wall-clock budget via AsyncLocal<T>
//   Depends on : nothing (static primitive)
//   Used by    : ResilienceExecutor, RetryPolicyBuilder, TimeoutPolicyBuilder,
//                HedgingPolicyBuilder
//   See also   : docs/time-budget.md, SPEC.md section 20
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Correlation;

/// <summary>
/// Holds the total wall-clock deadline for the current pipeline call.
/// <para>
/// Unlike per-attempt timeouts, the time budget spans the <b>entire</b>
/// operation - every retry attempt, every hedge, and the initial call share
/// one ceiling. Layers consult <see cref="RemainingMs"/> to decide whether
/// they can start another attempt, wait for a delay, or run a hedge.
/// </para>
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the value flows through <c>await</c>
/// boundaries and is isolated between concurrent calls.
/// </para>
/// <para>
/// This class is static and <b>not</b> intended to be injected. The executor
/// sets it; the policy builders read it.
/// </para>
/// </summary>
public static class TimeBudgetContext
{
    private static readonly AsyncLocal<DateTime?> DeadlineUtc = new();

    /// <summary>
    /// The remaining wall-clock budget in milliseconds, or <c>null</c> if no
    /// budget scope is active. Can be zero or negative when the budget has
    /// already been exhausted.
    /// </summary>
    public static double? RemainingMs
    {
        get
        {
            var deadline = DeadlineUtc.Value;
            if (deadline is null)
            {
                return null;
            }

            return (deadline.Value - DateTime.UtcNow).TotalMilliseconds;
        }
    }

    /// <summary>
    /// True when a budget scope is active and the remaining time is at or
    /// below zero. False when no scope is active (budget is unbounded).
    /// </summary>
    public static bool IsExhausted
    {
        get
        {
            var remaining = RemainingMs;
            return remaining is not null && remaining.Value <= 0;
        }
    }

    /// <summary>
    /// Returns the remaining budget when a scope is active, or
    /// <see cref="double.MaxValue"/> when no scope is active. Convenience for
    /// layers that need a numeric ceiling without null checks.
    /// </summary>
    public static double RemainingOrMax() => RemainingMs ?? double.MaxValue;

    /// <summary>
    /// Sets a total wall-clock budget for the current async flow. The budget
    /// is converted to an absolute deadline at push time. Returns a disposable
    /// that restores the previous deadline on dispose - use in <c>using</c> blocks.
    /// </summary>
    /// <param name="budgetMs">Total budget in milliseconds. Must be positive.</param>
    /// <returns>A disposable that restores the previous deadline.</returns>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="budgetMs"/> is zero or negative.</exception>
    public static IDisposable Push(int budgetMs)
    {
        if (budgetMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budgetMs),
                budgetMs,
                "Time budget must be positive milliseconds.");
        }

        var previous = DeadlineUtc.Value;
        DeadlineUtc.Value = DateTime.UtcNow.AddMilliseconds(budgetMs);

        return new PopScope(previous);
    }

    private sealed class PopScope : IDisposable
    {
        private readonly DateTime? _previous;

        public PopScope(DateTime? previous) => _previous = previous;

        public void Dispose() => DeadlineUtc.Value = _previous;
    }
}
