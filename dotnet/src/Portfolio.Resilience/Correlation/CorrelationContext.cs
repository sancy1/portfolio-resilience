// filepath: src/Portfolio.Resilience/Correlation/CorrelationContext.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: Ambient async-safe storage for the correlation ID of the current request/flow.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Provides   : Ambient correlation ID via AsyncLocal<T>
//   Depends on : nothing (static primitive)
//   Used by    : AsyncLocalCorrelationAccessor, ResilienceExecutor (Stage F), HTTP middleware (Stage G)
//   See also   : docs/correlation.md, SPEC.md §Correlation
// ─────────────────────────────────────────────────────────────────────────────

namespace Portfolio.Resilience.Correlation;

/// <summary>
/// Holds the correlation ID for the current asynchronous flow.
/// <para>
/// Uses <see cref="AsyncLocal{T}"/> so the value flows correctly through
/// <c>await</c> boundaries without being shared across unrelated concurrent
/// operations. Reading or writing from different async branches after an
/// <c>await</c> that was started before another branch will see the value
/// that existed at the point of the branch — exactly the semantic we want
/// for request-scoped correlation.
/// </para>
/// <para>
/// This class is static and <b>not</b> intended to be injected.
/// Use <see cref="Abstractions.ICorrelationAccessor"/> in DI-based code —
/// the default implementation delegates to this class.
/// </para>
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>
    /// The correlation ID for the current async flow, or <c>null</c> if none has been set.
    /// </summary>
    public static string? CurrentId => Current.Value;

    /// <summary>
    /// Sets the correlation ID for the current async flow. Returns a disposable
    /// that restores the previous value on dispose — use in <c>using</c> blocks.
    /// </summary>
    /// <param name="correlationId">The ID to set. Must not be null or whitespace.</param>
    /// <returns>A disposable that restores the previous value.</returns>
    public static IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var previous = Current.Value;
        Current.Value = correlationId;

        return new PopScope(previous);
    }

    /// <summary>
    /// Generates a new correlation ID (GUID v4, lowercased hex without dashes)
    /// suitable for use in headers, logs, and traces.
    /// </summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    private sealed class PopScope : IDisposable
    {
        private readonly string? _previous;

        public PopScope(string? previous) => _previous = previous;

        public void Dispose() => Current.Value = _previous;
    }
}
