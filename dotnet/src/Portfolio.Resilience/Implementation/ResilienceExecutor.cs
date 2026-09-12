// filepath: src/Portfolio.Resilience/Implementation/ResilienceExecutor.cs
// layer: Implementation | package: Portfolio.Resilience | since: v0.3.0
// purpose: The main entry point. Runs operations through retry -> circuit -> timeout with full observability.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : IResilienceExecutor
//   Depends on : IResiliencePolicyRegistry, CompositePolicyBuilder, ResilienceEventEmitter,
//                InMemoryMetricSink, ErrorClassifier, ResilienceException, CorrelationContext
//   Used by    : every service that needs resilient calls (via DI extension in Stage F file 2)
//   See also   : docs/executor.md, SPEC.md §Executor
// ─────────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;
using Portfolio.Resilience.Policies;
using Portfolio.Resilience.Sinks;

namespace Portfolio.Resilience.Implementation;

/// <summary>
/// Runs operations through the full resilience pipeline:
/// retry -> circuit -> timeout, with structured logging and latency metrics.
/// </summary>
public sealed class ResilienceExecutor : IResilienceExecutor
{
    private readonly IResiliencePolicyRegistry _registry;
    private readonly CompositePolicyBuilder _pipeline;
    private readonly ResilienceEventEmitter _emitter;
    private readonly InMemoryMetricSink _metricSink;
    private readonly ErrorClassifier _classifier;

    public ResilienceExecutor(
        IResiliencePolicyRegistry? registry = null,
        CompositePolicyBuilder? pipeline = null,
        ResilienceEventEmitter? emitter = null,
        InMemoryMetricSink? metricSink = null,
        ErrorClassifier? classifier = null)
    {
        _registry   = registry   ?? new ResiliencePolicyRegistry();
        _pipeline   = pipeline   ?? new CompositePolicyBuilder();
        _emitter    = emitter    ?? new ResilienceEventEmitter();
        _metricSink = metricSink ?? new InMemoryMetricSink();
        _classifier = classifier ?? new ErrorClassifier();
    }
    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task<T>>? fallback = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);

        var definition = _registry.Resolve(policyName);
        var startedAt = Stopwatch.GetTimestamp();
        var attempts = 0;

        _emitter.EmitCallStarted(policyName);
        _metricSink.BeginInFlight(policyName);

        try
        {
            var result = await _pipeline.ExecuteAsync(
                operation: async attemptCt =>
                {
                    attempts++;
                    return await operation(attemptCt).ConfigureAwait(false);
                },
                definition: definition,
                ct: ct).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            _emitter.EmitCallSucceeded(policyName, elapsed.TotalMilliseconds, attempts);
            _metricSink.RecordCall(policyName, elapsed, success: true, attempts);

            return result;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var classified = ClassifyAsResilienceException(policyName, ex, attempts, elapsed);

            // Fallback path
            if (fallback is not null)
            {
                _emitter.EmitFallbackUsed(policyName, reason: classified.Category.ToString());
                try
                {
                    var fallbackResult = await fallback(ct).ConfigureAwait(false);
                    _metricSink.RecordCall(policyName, elapsed, success: false, attempts);
                    return fallbackResult;
                }
                catch (Exception fallbackEx)
                {
                    _emitter.EmitCallFailed(policyName, classified, attempts, elapsed);
                    _metricSink.RecordCall(policyName, elapsed, success: false, attempts);
                    throw new ResilienceException(
                        message: $"Both operation and fallback failed for policy '{policyName}'.",
                        policyName: policyName,
                        category: classified.Category,
                        attemptsMade: attempts,
                        totalDuration: elapsed,
                        correlationId: CorrelationContext.CurrentId,
                        metadata: new Dictionary<string, object?>
                        {
                            ["fallback_error"] = fallbackEx.Message
                        },
                        inner: fallbackEx);
                }
            }

            _emitter.EmitCallFailed(policyName, classified, attempts, elapsed);
            _metricSink.RecordCall(policyName, elapsed, success: false, attempts);

            throw classified;
        }
        finally
        {
            _metricSink.EndInFlight(policyName);
        }
    }
    /// <inheritdoc />
    public Task ExecuteAsync(
        string policyName,
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task>? fallback = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);

        // Adapt to the generic path — wrap void op so it returns a sentinel value.
        return ExecuteAsync<object?>(
            policyName: policyName,
            operation: async attemptCt =>
            {
                await operation(attemptCt).ConfigureAwait(false);
                return null;
            },
            fallback: fallback is null
                ? null
                : async fallbackCt =>
                {
                    await fallback(fallbackCt).ConfigureAwait(false);
                    return null;
                },
            ct: ct);
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    /// <summary>
    /// Ensures the exception surfaced to callers is always a typed
    /// <see cref="ResilienceException"/> carrying full context.
    /// </summary>
    private ResilienceException ClassifyAsResilienceException(
        string policyName,
        Exception ex,
        int attempts,
        TimeSpan elapsed)
    {
        if (ex is ResilienceException existing)
        {
            return existing;
        }

        return new ResilienceException(
            message: $"Operation failed for policy '{policyName}': {ex.Message}",
            policyName: policyName,
            category: _classifier.Classify(ex),
            attemptsMade: attempts,
            totalDuration: elapsed,
            correlationId: CorrelationContext.CurrentId,
            inner: ex);
    }
}
