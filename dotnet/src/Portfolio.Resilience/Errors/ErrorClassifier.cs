// filepath: src/Portfolio.Resilience/Errors/ErrorClassifier.cs
// layer: Errors | package: Portfolio.Resilience | since: v0.2.0
// purpose: Maps arbitrary exceptions to ResilienceErrorCategory using configurable rules.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (stateless service)
//   Depends on : ErrorClassificationOptions, ResilienceException
//   Used by    : RetryPolicyBuilder, CircuitPolicyBuilder, ResilienceExecutor (Stage F)
//   See also   : docs/error-classification.md, SPEC.md §ErrorCategories
// ─────────────────────────────────────────────────────────────────────────────

using System.Net;
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Errors;

/// <summary>
/// Classifies an exception into a <see cref="ResilienceErrorCategory"/> so that
/// retry and circuit-breaker policies can decide how to react.
/// </summary>
/// <remarks>
/// Classification priority (top to bottom):
/// <list type="number">
///   <item>Already-classified <see cref="ResilienceException"/> — passthrough.</item>
///   <item>Cancellation — <see cref="ResilienceErrorCategory.Permanent"/> or <see cref="ResilienceErrorCategory.Timeout"/>.</item>
///   <item>HTTP status code (via <c>HttpRequestException.StatusCode</c> or a <c>WebException</c>).</item>
///   <item>PostgreSQL SQLSTATE.</item>
///   <item>Exception type name against transient/permanent lists.</item>
///   <item>Fallback: <see cref="ResilienceErrorCategory.Permanent"/>.</item>
/// </list>
/// The default fallback is <c>Permanent</c> — the conservative choice. Retrying an
/// error that is actually permanent wastes resources and can compound load.
/// </remarks>
public sealed class ErrorClassifier
{
    private readonly ErrorClassificationOptions _options;

    public ErrorClassifier(ErrorClassificationOptions? options = null)
    {
        _options = options ?? new ErrorClassificationOptions();
    }

    /// <summary>Classifies <paramref name="exception"/> into an error category.</summary>
    public ResilienceErrorCategory Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // 1. Passthrough — an inner ResilienceException already carries a category.
        if (exception is ResilienceException rex)
        {
            return rex.Category;
        }

        // 2. Cancellation.
        if (exception is OperationCanceledException oce)
        {
            return ClassifyCancellation(oce);
        }

        // 3. HTTP status code.
        if (TryClassifyHttpStatus(exception, out var httpCategory))
        {
            return httpCategory;
        }

        // 4. PostgreSQL SQLSTATE.
        if (TryClassifySqlState(exception, out var sqlCategory))
        {
            return sqlCategory;
        }

        // 5. Exception type name.
        var typeName = exception.GetType().FullName;
        if (typeName is not null)
        {
            if (_options.PermanentExceptionTypeNames.Contains(typeName))
                return ResilienceErrorCategory.Permanent;

            if (_options.TransientExceptionTypeNames.Contains(typeName))
                return ResilienceErrorCategory.Transient;
        }

        // 6. Fallback.
        return ResilienceErrorCategory.Permanent;
    }
    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private ResilienceErrorCategory ClassifyCancellation(OperationCanceledException oce)
    {
        // If a caller's CancellationToken caused this, the operation was cancelled
        // intentionally. Retrying makes no sense — classify as Permanent.
        // A TaskCanceledException with no CancellationToken is usually an HTTP
        // timeout surfacing as cancellation — classify as Timeout.
        if (_options.TreatCancellationAsPermanent && oce.CancellationToken.IsCancellationRequested)
        {
            return ResilienceErrorCategory.Permanent;
        }

        return ResilienceErrorCategory.Timeout;
    }

    private bool TryClassifyHttpStatus(Exception exception, out ResilienceErrorCategory category)
    {
        category = ResilienceErrorCategory.Unknown;

        // .NET 5+ HttpRequestException carries StatusCode directly.
        if (exception is HttpRequestException httpEx && httpEx.StatusCode is HttpStatusCode status)
        {
            category = ClassifyHttpStatusCode((int)status);
            return true;
        }

        return false;
    }

    private ResilienceErrorCategory ClassifyHttpStatusCode(int code)
    {
        if (_options.TransientHttpStatusCodes.Contains(code))
            return ResilienceErrorCategory.Transient;

        if (_options.PermanentHttpStatusCodes.Contains(code))
            return ResilienceErrorCategory.Permanent;

        // 5xx not explicitly listed → still transient by class.
        if (code >= 500 && code <= 599)
            return ResilienceErrorCategory.Transient;

        // 4xx not explicitly listed → still permanent by class.
        if (code >= 400 && code <= 499)
            return ResilienceErrorCategory.Permanent;

        return ResilienceErrorCategory.Permanent;
    }

    private bool TryClassifySqlState(Exception exception, out ResilienceErrorCategory category)
    {
        category = ResilienceErrorCategory.Unknown;

        // NpgsqlException exposes a SqlState string. We match by reflection-free
        // property name so we do not force a compile-time dependency on Npgsql.
        var type = exception.GetType();
        var sqlStateProp = type.GetProperty("SqlState");

        if (sqlStateProp?.GetValue(exception) is string sqlState && !string.IsNullOrEmpty(sqlState))
        {
            category = _options.TransientSqlStates.Contains(sqlState)
                ? ResilienceErrorCategory.Transient
                : ResilienceErrorCategory.Permanent;
            return true;
        }

        return false;
    }
}
