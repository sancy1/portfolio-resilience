// filepath: src/Portfolio.Resilience/Policies/RetryPolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.2.0
// purpose: Executes an operation with exponential-backoff retry, using the error classifier to gate retries.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (concrete builder)
//   Depends on : RetryOptions, ErrorClassifier, ResilienceErrorCategory
//   Used by    : CompositePolicyBuilder, ResilienceExecutor (Stage F)
//   See also   : docs/retry.md, SPEC.md §Retry
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// Runs an async operation with retry and exponential backoff.
/// <para>
/// Backoff formula: <c>delay(n) = min(BaseDelayMs * 2^(n-1), MaxDelayMs) + jitter</c>
/// where <c>n</c> is the retry attempt number (1-based) and
/// <c>jitter = rand(0, JitterRatio * BaseDelayMs)</c>.
/// </para>
/// <para>
/// Only exceptions classified as <see cref="ResilienceErrorCategory.Transient"/>
/// are retried. Permanent errors rethrow immediately. This prevents retrying
/// validation failures and other terminal conditions.
/// </para>
/// </summary>
public sealed class RetryPolicyBuilder
{
    /// <summary>
    /// Delay function. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
    /// Overridable for tests so they do not actually sleep.
    /// </summary>
    public delegate Task DelayStrategy(TimeSpan delay, CancellationToken ct);

    private readonly ErrorClassifier _classifier;
    private readonly DelayStrategy _delay;
    private readonly Random _random;

    public RetryPolicyBuilder(
        ErrorClassifier? classifier = null,
        DelayStrategy? delayStrategy = null)
    {
        _classifier = classifier ?? new ErrorClassifier();
        _delay = delayStrategy ?? ((d, ct) => Task.Delay(d, ct));
        _random = new Random();
    }
    /// <summary>Executes <paramref name="operation"/> with retry per <paramref name="options"/>.</summary>
    /// <typeparam name="T">The operation's return type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="options">Retry tuning for this execution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation's result on first success.</returns>
    /// <exception cref="ArgumentNullException">If operation is null.</exception>
    /// <exception cref="OperationCanceledException">If the caller's token fires.</exception>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        var attempt = 0;
        var maxAttempts = 1 + Math.Max(0, options.MaxAttempts); // first try + retries

        while (true)
        {
            attempt++;
            ct.ThrowIfCancellationRequested();

            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && ShouldRetry(ex, options))
            {
                var delay = CalculateDelay(attempt, options);
                await _delay(delay, ct).ConfigureAwait(false);
            }
            // Any other exception (or last attempt) propagates out.
        }
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private bool ShouldRetry(Exception ex, RetryOptions options)
    {
        var category = _classifier.Classify(ex);

        return category switch
        {
            ResilienceErrorCategory.Transient => true,
            ResilienceErrorCategory.Timeout => true,
            ResilienceErrorCategory.Permanent => options.RetryOnPermanent,
            ResilienceErrorCategory.CircuitOpen => false,
            ResilienceErrorCategory.FallbackUsed => false,
            _ => false
        };
    }

    /// <summary>
    /// Computes the delay before retry <paramref name="attempt"/> (1-based).
    /// Formula: <c>min(base * 2^(attempt-1), max) + rand(0, jitter_ratio * base)</c>.
    /// </summary>
    public TimeSpan CalculateDelay(int attempt, RetryOptions options)
    {
        var baseMs = (double)options.BaseDelayMs;
        var maxMs = (double)options.MaxDelayMs;
        var jitterRatio = Math.Max(0.0, Math.Min(1.0, options.JitterRatio));

        var exponential = baseMs * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, maxMs);
        var jitter = _random.NextDouble() * jitterRatio * baseMs;

        return TimeSpan.FromMilliseconds(capped + jitter);
    }
}
