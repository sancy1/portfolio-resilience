// filepath: src/Portfolio.Resilience/Correlation/IdempotencyContext.cs
// layer: Correlation | package: Portfolio.Resilience | since: v0.8.0
// purpose: Ambient async-safe storage for the idempotency key of the current flow.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Provides   : Ambient idempotency key via AsyncLocal<T>
//   Depends on : CorrelationContext (fallback key generation)
//   Used by    : ResilienceExecutor, ResilientHttpMessageHandler
//   See also   : docs/idempotency.md, SPEC.md section 18
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Correlation;

/// <summary>
/// Holds the idempotency key for the current asynchronous flow.
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the value flows correctly through
/// <c>await</c> boundaries without being shared across unrelated concurrent
/// operations. Retries, hedged attempts, and any operation invoked inside
/// the pipeline see the same key.
/// </para>
/// <para>
/// This class is static and <b>not</b> intended to be injected. The executor
/// sets it; the HTTP handler and any user code read it.
/// </para>
/// </summary>
public static class IdempotencyContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>
    /// The idempotency key for the current async flow, or <c>null</c> if none has been set.
    /// </summary>
    public static string? CurrentKey => Current.Value;

    /// <summary>
    /// Sets the idempotency key for the current async flow. Returns a disposable
    /// that restores the previous value on dispose - use in <c>using</c> blocks.
    /// </summary>
    /// <param name="key">The key to set. Must not be null or whitespace.</param>
    /// <returns>A disposable that restores the previous value.</returns>
    /// <exception cref="ArgumentException">If <paramref name="key"/> is null or whitespace.</exception>
    public static IDisposable Push(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var previous = Current.Value;
        Current.Value = key;

        return new PopScope(previous);
    }

    /// <summary>
    /// Generates a new idempotency key derived from the ambient correlation ID
    /// when one is present, or a fresh GUID otherwise. Suitable for call sites
    /// that do not supply an explicit key.
    /// </summary>
    /// <returns>A non-empty key.</returns>
    public static string GenerateFromCorrelation()
    {
        var correlationId = CorrelationContext.CurrentId;
        return correlationId is not null
            ? $"idem-{correlationId}"
            : $"idem-{Guid.NewGuid():N}";
    }

    private sealed class PopScope : IDisposable
    {
        private readonly string? _previous;

        public PopScope(string? previous) => _previous = previous;

        public void Dispose() => Current.Value = _previous;
    }
}
