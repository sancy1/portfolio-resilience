// filepath: src/Portfolio.Resilience/Correlation/AsyncLocalCorrelationAccessor.cs
// layer: Infrastructure | package: Portfolio.Resilience | since: v0.2.0
// purpose: ICorrelationAccessor implementation delegating to CorrelationContext. Enables DI-based access.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ICorrelationAccessor
//   Depends on : CorrelationContext (Correlation/CorrelationContext.cs)
//   Used by    : ServiceCollectionExtensions (registered as singleton)
//   See also   : docs/correlation.md, SPEC.md §Correlation
// ─────────────────────────────────────────────────────────────────────────────

using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.Correlation;

/// <summary>
/// Default <see cref="ICorrelationAccessor"/> implementation.
/// Delegates to the ambient <see cref="CorrelationContext"/> so injected
/// consumers and static callers observe the same value.
/// <para>
/// This is a thin adapter — it holds no state of its own. All correlation
/// state lives in <see cref="CorrelationContext"/>'s <c>AsyncLocal</c>.
/// </para>
/// </summary>
public sealed class AsyncLocalCorrelationAccessor : ICorrelationAccessor
{
    /// <inheritdoc />
    public string? Current => CorrelationContext.CurrentId;

    /// <inheritdoc />
    public IDisposable Push(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return CorrelationContext.Push(correlationId);
    }
}
