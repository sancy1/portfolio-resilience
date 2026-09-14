// filepath: src/Portfolio.Resilience/Policies/HedgingPolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.7.0
// purpose: Runs parallel hedged attempts with a stagger delay; first success wins.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : IResiliencePolicy
//   Depends on : HedgingOptions, ResilienceException, ResilienceEventEmitter, PolicyDefinition
//   Used by    : CompositePolicyBuilder, ResilienceExecutor, ResiliencePipeline
//   See also   : docs/hedging.md, SPEC.md section 16
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Implementation;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs parallel hedged attempts of the same operation with a stagger delay
/// between them. The first attempt to succeed wins the race; the others are
/// cancelled (or allowed to complete, depending on
/// <see cref="HedgingOptions.CancelOnSuccess"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a latency optimization, not a reliability one.</b> Use retry for
/// transient failures. Hedging is for cases where the p99 tail is unacceptable
/// and a duplicate attempt is cheaper than waiting.
/// </para>
/// <para>
/// <b>Hedged attempts fire only if no prior attempt has already succeeded.</b>
/// The primary starts immediately. Each hedge waits its stagger delay, then
/// checks whether the primary (or an earlier hedge) has already won. If so,
/// the hedge never fires.
/// </para>
/// <para>
/// <b>Not safe for non-idempotent operations.</b> A hedged <c>POST /charge</c>
/// can create two charges if both attempts reach the server. Use on idempotent
/// reads, or on writes with an idempotency key.
/// </para>
/// <para>
/// When all attempts fail, the exception thrown is the one from the first
/// attempt. Other attempts' failures are attached to the exception's
/// <see cref="Exception.Data"/> dictionary under keys of the form
/// <c>hedge_attempt_N</c> and <c>hedge_attempt_N_exception</c>.
/// </para>
/// <para>
/// Thread-safe. Each call creates its own state; there is no per-policy state
/// to protect.
/// </para>
/// </remarks>
public sealed class HedgingPolicyBuilder : IResiliencePolicy
{
    /// <summary>
    /// Delay function. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Overridable for tests so they do not actually sleep.
    /// </summary>
    public delegate Task DelayStrategy(TimeSpan delay, CancellationToken ct);

    private readonly ResilienceEventEmitter _emitter;
    private readonly DelayStrategy _delay;

    /// <summary>
    /// Creates a hedging builder.
    /// </summary>
    /// <param name="emitter">Optional event emitter. Defaults to a silent emitter.</param>
    /// <param name="delayStrategy">Optional delay function. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public HedgingPolicyBuilder(
        ResilienceEventEmitter? emitter = null,
        DelayStrategy? delayStrategy = null)
    {
        _emitter = emitter ?? new ResilienceEventEmitter();
        _delay = delayStrategy ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// Executes <paramref name="operation"/> with hedging per
    /// <paramref name="options"/>.
    /// </summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="policyName">The policy name, used for events.</param>
    /// <param name="operation">The operation to execute (may be invoked multiple times in parallel).</param>
    /// <param name="options">Hedging tuning for this execution.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>The result of the first successful attempt.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="operation"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
    /// <exception cref="Exception">The first attempt's failure when all attempts fail.</exception>
    public async Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        HedgingOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        // Disabled or single attempt: no race.
        // When hedging is enabled with MaxAttempts == 1, still report the single
        // attempt as the winner if EmitAttemptEvents is set.
        if (!options.Enabled || options.MaxAttempts <= 1)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await operation(ct).ConfigureAwait(false);
            sw.Stop();

            if (options.Enabled && options.EmitAttemptEvents)
            {
                _emitter.EmitHedgeWon(
                    policyName,
                    attemptNumber: 1,
                    durationMs: sw.Elapsed.TotalMilliseconds);
            }

            return result;
        }

        // Shared cancellation for loser attempts.
        using var losersCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, losersCts.Token);

        var attempts = new Task<T>?[options.MaxAttempts];
        var startTimes = new DateTime[options.MaxAttempts];
        var attemptInvoked = new bool[options.MaxAttempts];

        // Fire attempt 0 immediately.
        attempts[0] = StartAttempt(0, operation, startTimes, attemptInvoked, options.AttemptTimeoutMs, linkedCts.Token);

        // Fire hedges one at a time. Before each, wait the stagger delay AND
        // check whether a prior attempt has already succeeded. If so, stop.
        for (var i = 1; i < options.MaxAttempts; i++)
        {
            var delayMs = CalculateDelayMs(i, options);
            var won = await WaitForWinnerOrDelayAsync(attempts, i, delayMs, linkedCts.Token)
                .ConfigureAwait(false);

            if (won is not null)
            {
                // A prior attempt won during the delay. Do not fire this hedge.
                if (options.CancelOnSuccess)
                {
                    losersCts.Cancel();
                }

                EmitRaceEvents(policyName, options, attempts, startTimes, winnerIndex: won.Value.index, invoked: attemptInvoked);
                return won.Value.result;
            }

            // No winner yet. Fire the hedge.
            attempts[i] = StartAttempt(i, operation, startTimes, attemptInvoked, options.AttemptTimeoutMs, linkedCts.Token);
        }

        // All attempts are in flight. Wait for the first success.
        var (winnerIdx, winnerResult, failures) = await WaitForFirstSuccessOrAllFailAsync(attempts, linkedCts.Token)
            .ConfigureAwait(false);

        if (winnerIdx >= 0)
        {
            if (options.CancelOnSuccess)
            {
                losersCts.Cancel();
            }

            EmitRaceEvents(policyName, options, attempts, startTimes, winnerIdx, invoked: attemptInvoked);
            return winnerResult!;
        }

        // No winner: all attempts failed (or the caller cancelled).
        ct.ThrowIfCancellationRequested();

        throw BuildAggregateException(policyName, failures);
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
        => ExecuteAsync(policyName, operation, definition.Hedging, ct);

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private async Task<T> StartAttempt<T>(
        int attemptIndex,
        Func<CancellationToken, Task<T>> operation,
        DateTime[] startTimes,
        bool[] invoked,
        int attemptTimeoutMs,
        CancellationToken ct)
    {
        invoked[attemptIndex] = true;
        startTimes[attemptIndex] = DateTime.UtcNow;

        // Per-attempt timeout: wrap the operation in a linked CTS.
        if (attemptTimeoutMs > 0)
        {
            using var attemptCts = new CancellationTokenSource();
            using var attemptLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptCts.Token);
            attemptCts.CancelAfter(attemptTimeoutMs);
            return await operation(attemptLinked.Token).ConfigureAwait(false);
        }

        return await operation(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for either (a) any currently-invoked attempt to succeed, or
    /// (b) the stagger delay to elapse. Returns the winner on success, or null
    /// if the delay elapsed without a winner.
    /// </summary>
    private async Task<(int index, T result)?> WaitForWinnerOrDelayAsync<T>(
        Task<T>?[] attempts,
        int nextAttemptIndex,
        int delayMs,
        CancellationToken ct)
    {
        // Run the delay under its own CTS so we can release it early if an
        // attempt wins before the delay elapses.
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(TimeSpan.FromMilliseconds(delayMs), delayCts.Token);

        var pending = new List<(int index, Task<T> task)>();
        for (var i = 0; i < nextAttemptIndex; i++)
        {
            if (attempts[i] is not null)
            {
                pending.Add((i, attempts[i]!));
            }
        }

        while (pending.Count > 0)
        {
            // Cast each Task<T> to Task so Task.WhenAny accepts a homogeneous
            // array alongside the delay task.
            var allTasks = pending.Select(p => (Task)p.task).Append(delayTask).ToArray();
            var completed = await Task.WhenAny(allTasks).ConfigureAwait(false);

            if (ReferenceEquals(completed, delayTask))
            {
                // Delay elapsed without a winner.
                return null;
            }

            var completedEntry = pending.First(p => ReferenceEquals((Task)p.task, completed));

            if (completedEntry.task.IsCompletedSuccessfully)
            {
                // Cancel the delay task so it does not linger.
                delayCts.Cancel();
                return (completedEntry.index, completedEntry.task.Result);
            }

            // This attempt failed; remove and keep waiting.
            pending.RemoveAll(p => ReferenceEquals((Task)p.task, completed));
        }

        // No pending attempts and no winner yet; wait out the delay.
        await delayTask.ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Waits for the first attempt among the given array to succeed. If all
    /// fail (or are cancelled), returns the failure list with a -1 winner index.
    /// </summary>
    private async Task<(int winnerIdx, T? result, Exception?[] failures)> WaitForFirstSuccessOrAllFailAsync<T>(
        Task<T>?[] attempts,
        CancellationToken ct)
    {
        var failures = new Exception?[attempts.Length];

        while (true)
        {
            // First: check for an already-completed winner before waiting.
            // This prevents a deadlock when one attempt is already done and
            // another is pending forever (for example, waiting on a caller TCS).
            for (var i = 0; i < attempts.Length; i++)
            {
                if (attempts[i] is not null && attempts[i]!.IsCompletedSuccessfully)
                {
                    return (i, attempts[i]!.Result, failures);
                }
            }

            // Second: record failures and collect pending tasks.
            var pending = new List<(int index, Task<T> task)>();
            for (var i = 0; i < attempts.Length; i++)
            {
                if (attempts[i] is null)
                {
                    continue;
                }

                if (attempts[i]!.IsFaulted && failures[i] is null)
                {
                    failures[i] = attempts[i]!.Exception!.InnerException ?? attempts[i]!.Exception!;
                }
                else if (!attempts[i]!.IsCompleted)
                {
                    pending.Add((i, attempts[i]!));
                }
            }

            if (pending.Count == 0)
            {
                // No pending attempts and no winner: all failed (or cancelled).
                return (-1, default, failures);
            }

            var completed = await Task.WhenAny(pending.Select(p => p.task)).ConfigureAwait(false);
            var idx = pending.First(p => ReferenceEquals(p.task, completed)).index;

            if (completed.IsCompletedSuccessfully)
            {
                return (idx, completed.Result, failures);
            }

            failures[idx] = completed.Exception!.InnerException ?? completed.Exception!;
        }
    }

    /// <summary>
    /// Delay before firing attempt <paramref name="attemptIndex"/> (0-based).
    /// Attempt 0 = 0ms. Attempt 1 = DelayMs. Attempt 2 = DelayMs * 2 (if ExponentialBackoff) or DelayMs.
    /// </summary>
    public static int CalculateDelayMs(int attemptIndex, HedgingOptions options)
    {
        if (attemptIndex <= 0)
        {
            return 0;
        }

        if (!options.ExponentialBackoff)
        {
            return options.DelayMs;
        }

        var factor = 1L << (attemptIndex - 1);
        var raw = (long)options.DelayMs * factor;
        return raw > int.MaxValue ? int.MaxValue : (int)raw;
    }

    private void EmitRaceEvents<T>(
        string policyName,
        HedgingOptions options,
        Task<T>?[] attempts,
        DateTime[] startTimes,
        int winnerIndex,
        bool[] invoked)
    {
        if (!options.EmitAttemptEvents)
        {
            return;
        }

        var now = DateTime.UtcNow;

        for (var i = 0; i < attempts.Length; i++)
        {
            if (!invoked[i] || attempts[i] is null)
            {
                continue;
            }

            var durationMs = (now - startTimes[i]).TotalMilliseconds;

            if (i == winnerIndex)
            {
                _emitter.EmitHedgeWon(policyName, attemptNumber: i + 1, durationMs: durationMs);
                continue;
            }

            if (attempts[i]!.IsCompletedSuccessfully)
            {
                _emitter.EmitHedgeLost(policyName, attemptNumber: i + 1, durationMs: durationMs);
            }
            else if (attempts[i]!.IsCanceled)
            {
                _emitter.EmitHedgeCancelled(policyName, attemptNumber: i + 1);
            }
        }
    }

    /// <summary>
    /// Builds the exception thrown when all attempts fail. Uses the first
    /// attempt's exception as the outer exception, preserving its type for
    /// <c>catch</c> clauses. Other attempts' failures are attached to
    /// <see cref="Exception.Data"/>.
    /// </summary>
    private static Exception BuildAggregateException(string policyName, Exception?[] errors)
    {
        var firstIndex = -1;
        for (var i = 0; i < errors.Length; i++)
        {
            if (errors[i] is not null)
            {
                firstIndex = i;
                break;
            }
        }

        if (firstIndex < 0)
        {
            return new ResilienceException(
                message: $"All hedged attempts for policy '{policyName}' were cancelled.",
                policyName: policyName,
                category: ResilienceErrorCategory.Transient,
                attemptsMade: 0,
                totalDuration: TimeSpan.Zero);
        }

        var outer = errors[firstIndex]!;

        for (var i = 0; i < errors.Length; i++)
        {
            if (i == firstIndex || errors[i] is null)
            {
                continue;
            }

            outer.Data[$"hedge_attempt_{i + 1}"] = errors[i]!.Message;
            outer.Data[$"hedge_attempt_{i + 1}_exception"] = errors[i];
        }

        return outer;
    }
}
