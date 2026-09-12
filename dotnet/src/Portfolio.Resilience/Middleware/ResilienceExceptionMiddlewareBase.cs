// filepath: src/Portfolio.Resilience/Middleware/ResilienceExceptionMiddlewareBase.cs
// layer: Middleware | package: Portfolio.Resilience | since: v0.3.0
// purpose: ASP.NET Core middleware base that catches ResilienceException and lets services render their own error body.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (abstract base; services inherit and override RenderErrorAsync)
//   Depends on : HttpContext, RequestDelegate, ResilienceException, ILogger
//   Used by    : every ASP.NET Core service that wants standardized exception capture
//   See also   : docs/executor.md, SPEC.md §ResponseEnvelopeOwnership
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Middleware;

/// <summary>
/// Middleware that intercepts <see cref="ResilienceException"/> thrown from
/// downstream handlers. Services inherit this class and implement
/// <see cref="RenderErrorAsync"/> to render an error response in their own shape.
/// </summary>
/// <remarks>
/// <b>Why abstract, not concrete?</b> Response envelopes are service-specific.
/// A public landing-page API, an internal admin API, and a machine-to-machine
/// endpoint should each render errors differently. Sharing a single concrete
/// renderer would couple their contracts. Sharing only the <i>catch logic</i>
/// and the abstract render hook keeps them decoupled while still uniform.
/// <para>
/// Every service must satisfy the SPEC requirement: echo the correlation ID
/// in the response header <c>X-Correlation-Id</c>. This is done by the base
/// class automatically before <see cref="RenderErrorAsync"/> runs.
/// </para>
/// </remarks>
public abstract class ResilienceExceptionMiddlewareBase
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;

    protected ResilienceExceptionMiddlewareBase(RequestDelegate next, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invoked by the ASP.NET Core pipeline. Catches <see cref="ResilienceException"/>
    /// and delegates to <see cref="RenderErrorAsync"/>. Other exceptions propagate
    /// to the next handler in the pipeline.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (ResilienceException rex)
        {
            // Echo the correlation ID in the response header regardless of the
            // service's error body shape — this is a shared requirement.
            var correlationId = rex.CorrelationId ?? CorrelationContext.CurrentId;
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                context.Response.Headers["X-Correlation-Id"] = correlationId;
            }

            _logger.LogWarning(
                rex,
                "Resilience exception for policy '{PolicyName}', category {Category}, attempts {Attempts}",
                rex.PolicyName,
                rex.Category,
                rex.AttemptsMade);

            if (context.Response.HasStarted)
            {
                // Too late to render — the response is already on the wire.
                _logger.LogWarning(
                    "Response already started; cannot render error body for policy '{PolicyName}'",
                    rex.PolicyName);
                throw;
            }

            await RenderErrorAsync(context, rex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renders the error response for the given <see cref="ResilienceException"/>.
    /// Each service decides the status code, body shape, and content type.
    /// The <c>X-Correlation-Id</c> response header is set by the base class
    /// before this method is called.
    /// </summary>
    protected abstract Task RenderErrorAsync(HttpContext context, ResilienceException exception);
}
