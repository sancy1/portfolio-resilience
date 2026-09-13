// filepath: src/Portfolio.Resilience/Policies/RateLimiterPolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.6.0
// purpose: Enforces rate limits on outbound calls via four selectable strategies.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (concrete builder)
//   Depends on : RateLimiterOptions, RateLimitStrategy, ResilienceException, ResilienceEventEmitter
//   Used by    : CompositePolicyBuilder
//   See also   : docs/rate-limiter.md, SPEC.md section 12
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Implementation;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an async operation under a rate limiter. Four strategies are supported,
/// each interpreting <see cref="RateLimiterOptions.PermitLimit"/> and
/// <see cref="RateLimiterOptions.WindowSeconds"/> differently. See
/// <c>docs/rate-limiter.md</c> for the strategy-to-field matrix.
/// </summary>
/// <remarks>
/// Thread-safe. State is per-policy, held in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Rejections emit a <see cref="Events.ResilienceEventType.RateLimited"/> event through the
/// injected emitter and throw <see cref="ResilienceException"/> with the configured
/// <see cref="RateLimiterOptions.RejectionCategory"/>.
/// <para>
/// The <see cref="RateLimitStrategy.ConcurrencyLimit"/> strategy holds its semaphore
/// across the operation; all other strategies decide and release before the operation runs.
/// </para>
/// </remarks>
public sealed class RateLimiterPolicyBuilder
{
    /// <summary>
    /// Delay function. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Overridable for tests so they do not actually sleep.
    /// </summary>
    public delegate Task DelayStrategy(TimeSpan delay, CancellationToken ct);

    private readonly ResilienceEventEmitter _emitter;
    private readonly Func<DateTime> _clock;
    private readonly DelayStrategy _delay;

    // One LimiterState per policy name, created on first use.
    private readonly ConcurrentDictionary<string, LimiterState> _limiters
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a rate limiter builder.
    /// </summary>
    /// <param name="emitter">Optional event emitter. Defaults to a silent emitter.</param>
    /// <param name="clock">Optional clock. Defaults to <see cref="DateTime.UtcNow"/>.</param>
    /// <param name="delayStrategy">Optional delay function. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public RateLimiterPolicyBuilder(
        ResilienceEventEmitter? emitter = null,
        Func<DateTime>? clock = null,
        DelayStrategy? delayStrategy = null)
    {
        _emitter = emitter ?? new ResilienceEventEmitter();
        _clock = clock ?? (() => DateTime.UtcNow);
        _delay = delayStrategy ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>
    /// Executes <paramref name="operation"/> under the rate limiter for
    /// <paramref name="policyName"/>.
    /// </summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="policyName">The policy name whose limiter gates this call.</param>
    /// <param name="operation">The operation to execute if a permit is acquired.</param>
    /// <param name="options">Rate limiter tuning for this execution.</param>
    /// <param name="ct">Cancellation token propagated to the operation.</param>
    /// <returns>The operation's result on success.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="operation"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
    /// <exception cref="ResilienceException">With the configured <see cref="RateLimiterOptions.RejectionCategory"/> when the limiter rejects the call.</exception>
    public async Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        RateLimiterOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        // Disabled limiter: pass-through.
        if (!options.Enabled)
        {
            return await operation(ct).ConfigureAwait(false);
        }

        var state = _limiters.GetOrAdd(policyName, _ => new LimiterState());

        AcquireResult result = options.Strategy switch
        {
            RateLimitStrategy.TokenBucket => await AcquireTokenBucketAsync(state, options, ct).ConfigureAwait(false),
            RateLimitStrategy.SlidingWindow => await AcquireSlidingWindowAsync(state, options, ct).ConfigureAwait(false),
            RateLimitStrategy.FixedWindow => await AcquireFixedWindowAsync(state, options, ct).ConfigureAwait(false),
            RateLimitStrategy.ConcurrencyLimit => await AcquireConcurrencyLimitAsync(state, options, ct).ConfigureAwait(false),
            _ => AcquireResult.Reject(0, "rejected_immediately")
        };

        if (!result.Acquired)
        {
            var windowSeconds = options.Strategy == RateLimitStrategy.ConcurrencyLimit
                ? (int?)null
                : options.WindowSeconds;

            _emitter.EmitRateLimited(
                policyName: policyName,
                strategy: options.Strategy,
                permitLimit: options.PermitLimit,
                windowSeconds: windowSeconds,
                queueLimit: options.QueueLimit,
                queueDepth: result.QueueDepth);

            throw new ResilienceException(
                message: $"Rate limit exceeded for policy '{policyName}'.",
                policyName: policyName,
                category: options.RejectionCategory,
                attemptsMade: 0,
                totalDuration: TimeSpan.Zero,
                metadata: new Dictionary<string, object?>
                {
                    ["strategy"] = options.Strategy.ToString(),
                    ["permit_limit"] = options.PermitLimit,
                    ["queue_depth"] = result.QueueDepth,
                    ["reason"] = result.RejectionReason
                });
        }

        // For ConcurrencyLimit, the semaphore is held for the operation's duration.
        // For all other strategies, the permit was decided in the acquire step and
        // no state needs to be released after the operation.
        if (options.Strategy == RateLimitStrategy.ConcurrencyLimit && state.Semaphore is not null)
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

        return await operation(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------
    // Strategy helpers. Each returns AcquireResult.Accept() or .Reject(depth, reason).
    // All state mutations happen under the state object's lock.
    // ------------------------------------------------------------------------

    private async Task<AcquireResult> AcquireTokenBucketAsync(
        LimiterState state, RateLimiterOptions options, CancellationToken ct)
    {
        // Token bucket: PermitLimit = bucket capacity.
        // Refill rate = PermitLimit / WindowSeconds tokens per second.

        lock (state.Gate)
        {
            RefillTokens(state, options);

            if (state.Tokens >= 1.0)
            {
                state.Tokens -= 1.0;
                return AcquireResult.Accept();
            }
        }

        if (options.QueueLimit > 0)
        {
            var waitMs = TimeUntilNextTokenMs(state, options);
            if (waitMs <= options.QueueTimeoutMs)
            {
                await _delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);

                lock (state.Gate)
                {
                    RefillTokens(state, options);
                    if (state.Tokens >= 1.0)
                    {
                        state.Tokens -= 1.0;
                        return AcquireResult.Accept();
                    }
                }
                return AcquireResult.Reject(state.QueueDepth, "queue_timeout");
            }

            return AcquireResult.Reject(state.QueueDepth, "queue_timeout");
        }

        return AcquireResult.Reject(state.QueueDepth, "rejected_immediately");
    }

    private void RefillTokens(LimiterState state, RateLimiterOptions options)
    {
        var now = _clock();
        if (state.LastRefillUtc == default)
        {
            state.Tokens = options.PermitLimit;
            state.LastRefillUtc = now;
            return;
        }

        var elapsedSeconds = (now - state.LastRefillUtc).TotalSeconds;
        if (elapsedSeconds <= 0) return;

        var ratePerSecond = (double)options.PermitLimit / Math.Max(1, options.WindowSeconds);
        state.Tokens = Math.Min(options.PermitLimit, state.Tokens + (ratePerSecond * elapsedSeconds));
        state.LastRefillUtc = now;
    }

    private double TimeUntilNextTokenMs(LimiterState state, RateLimiterOptions options)
    {
        var ratePerSecond = (double)options.PermitLimit / Math.Max(1, options.WindowSeconds);
        if (ratePerSecond <= 0) return double.MaxValue;
        var secondsForOneToken = 1.0 / ratePerSecond;
        return secondsForOneToken * 1000.0;
    }

    private async Task<AcquireResult> AcquireSlidingWindowAsync(
        LimiterState state, RateLimiterOptions options, CancellationToken ct)
    {
        // Sliding window: PermitLimit calls permitted in any rolling WindowSeconds window.

        var now = _clock();
        var window = TimeSpan.FromSeconds(options.WindowSeconds);

        lock (state.Gate)
        {
            PruneWindow(state, now, window);

            if (state.WindowTimestamps.Count < options.PermitLimit)
            {
                state.WindowTimestamps.Enqueue(now);
                return AcquireResult.Accept();
            }
        }

        if (options.QueueLimit > 0)
        {
            DateTime oldest;
            lock (state.Gate)
            {
                if (state.WindowTimestamps.Count == 0)
                {
                    return AcquireResult.Reject(0, "queue_timeout");
                }
                oldest = state.WindowTimestamps.Peek();
            }

            var waitMs = (window - (now - oldest)).TotalMilliseconds;
            if (waitMs > 0 && waitMs <= options.QueueTimeoutMs)
            {
                await _delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);

                var after = _clock();
                lock (state.Gate)
                {
                    PruneWindow(state, after, window);
                    if (state.WindowTimestamps.Count < options.PermitLimit)
                    {
                        state.WindowTimestamps.Enqueue(after);
                        return AcquireResult.Accept();
                    }
                }
                return AcquireResult.Reject(state.WindowTimestamps.Count, "queue_timeout");
            }

            return AcquireResult.Reject(state.WindowTimestamps.Count, "queue_timeout");
        }

        return AcquireResult.Reject(state.WindowTimestamps.Count, "rejected_immediately");
    }

    private void PruneWindow(LimiterState state, DateTime now, TimeSpan window)
    {
        while (state.WindowTimestamps.Count > 0
            && now - state.WindowTimestamps.Peek() >= window)
        {
            state.WindowTimestamps.Dequeue();
        }
    }

    private async Task<AcquireResult> AcquireFixedWindowAsync(
        LimiterState state, RateLimiterOptions options, CancellationToken ct)
    {
        // Fixed window: PermitLimit calls permitted per WindowSeconds bucket.

        var now = _clock();
        var window = TimeSpan.FromSeconds(options.WindowSeconds);

        lock (state.Gate)
        {
            RolloverFixedWindow(state, now, window);

            if (state.FixedWindowCount < options.PermitLimit)
            {
                state.FixedWindowCount++;
                return AcquireResult.Accept();
            }
        }

        if (options.QueueLimit > 0)
        {
            DateTime windowStart;
            lock (state.Gate)
            {
                windowStart = state.WindowStartUtc;
            }

            var waitMs = (window - (now - windowStart)).TotalMilliseconds;
            if (waitMs > 0 && waitMs <= options.QueueTimeoutMs)
            {
                await _delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);

                var after = _clock();
                lock (state.Gate)
                {
                    RolloverFixedWindow(state, after, window);
                    if (state.FixedWindowCount < options.PermitLimit)
                    {
                        state.FixedWindowCount++;
                        return AcquireResult.Accept();
                    }
                }
                return AcquireResult.Reject(state.FixedWindowCount, "queue_timeout");
            }

            return AcquireResult.Reject(state.FixedWindowCount, "queue_timeout");
        }

        return AcquireResult.Reject(state.FixedWindowCount, "rejected_immediately");
    }

    private void RolloverFixedWindow(LimiterState state, DateTime now, TimeSpan window)
    {
        if (state.WindowStartUtc == default)
        {
            state.WindowStartUtc = now;
            return;
        }

        if (now - state.WindowStartUtc >= window)
        {
            var elapsed = now - state.WindowStartUtc;
            var windows = (int)(elapsed.Ticks / window.Ticks);
            state.WindowStartUtc = state.WindowStartUtc.AddTicks(windows * window.Ticks);
            state.FixedWindowCount = 0;
        }
    }

    private async Task<AcquireResult> AcquireConcurrencyLimitAsync(
        LimiterState state, RateLimiterOptions options, CancellationToken ct)
    {
        // Concurrency limit: PermitLimit simultaneous calls. No window.
        // The semaphore is NOT released here - ExecuteAsync releases it after
        // the operation completes.

        lock (state.Gate)
        {
            if (state.Semaphore is null)
            {
                state.Semaphore = new SemaphoreSlim(options.PermitLimit, options.PermitLimit);
            }
        }

        var semaphore = state.Semaphore!;

        // No queue: try to enter immediately without blocking.
        if (options.QueueLimit == 0)
        {
            if (semaphore.Wait(0))
            {
                return AcquireResult.Accept();
            }
            return AcquireResult.Reject(queueDepth: 0, reason: "rejected_immediately");
        }

        // Queue enabled: wait up to QueueTimeoutMs for a slot.
        var queueDepth = options.PermitLimit - semaphore.CurrentCount;
        if (queueDepth < 0) queueDepth = 0;

        var acquired = await semaphore
            .WaitAsync(TimeSpan.FromMilliseconds(options.QueueTimeoutMs), ct)
            .ConfigureAwait(false);

        if (acquired)
        {
            return AcquireResult.Accept();
        }

        return AcquireResult.Reject(queueDepth, "queue_timeout");
    }

    // ------------------------------------------------------------------------
    // Types
    // ------------------------------------------------------------------------

    /// <summary>
    /// The result of an attempt to acquire a rate-limit permit.
    /// </summary>
    /// <param name="Acquired">True if the call is permitted to proceed.</param>
    /// <param name="QueueDepth">The number of calls waiting at the moment of decision.</param>
    /// <param name="RejectionReason">
    /// One of "rejected_immediately", "queue_full", "queue_timeout", or null when acquired.
    /// </param>
    private readonly record struct AcquireResult(
        bool Acquired,
        int QueueDepth,
        string? RejectionReason)
    {
        /// <summary>Permit acquired; caller may proceed.</summary>
        public static AcquireResult Accept() => new(true, 0, null);

        /// <summary>Call rejected; caller must not proceed.</summary>
        public static AcquireResult Reject(int queueDepth, string reason) => new(false, queueDepth, reason);
    }

    /// <summary>
    /// Per-policy rate limiter state. All four strategies share this container;
    /// only the fields relevant to the configured strategy are used.
    /// One lock (<see cref="Gate"/>) protects every strategy's mutations.
    /// </summary>
    private sealed class LimiterState
    {
        public object Gate { get; } = new();

        // Token bucket
        public double Tokens { get; set; } = double.NaN;
        public DateTime LastRefillUtc { get; set; } = default;

        // Sliding + fixed window
        public Queue<DateTime> WindowTimestamps { get; } = new();
        public int FixedWindowCount { get; set; }
        public DateTime WindowStartUtc { get; set; } = default;

        // Concurrency limit - held across the operation by ExecuteAsync
        public SemaphoreSlim? Semaphore { get; set; }

        // Diagnostic only: number of calls that entered the queue and are waiting.
        public int QueueDepth { get; set; }
    }
}
