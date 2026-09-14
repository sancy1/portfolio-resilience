// filepath: src/Portfolio.Resilience/HttpClient/ResilientHttpMessageHandler.cs
// layer: HttpClient | package: Portfolio.Resilience | since: v0.8.0
// purpose: DelegatingHandler that routes every HttpClient request through the pipeline and emits the idempotency key on every attempt.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : DelegatingHandler
//   Depends on : IResilienceExecutor, IdempotencyContext, HttpClientOptions, HttpRequestMessage
//   Used by    : HttpClientBuilderExtensions.AddResilientHandler, all consumer services
//   See also   : docs/http-integration.md, docs/idempotency.md, SPEC.md section 18
// -----------------------------------------------------------------------------

using System.Net.Http;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;

namespace Portfolio.Resilience.HttpClient;

/// <summary>
/// Sends HTTP requests through the resilience pipeline and adds the ambient
/// idempotency key as a header on every attempt (primary and each retry).
/// Non-success HTTP status codes (5xx, 408, 425, 429) are treated as failures
/// and participate in retry and circuit accounting.
/// </summary>
/// <remarks>
/// <b>Request cloning:</b> <see cref="HttpRequestMessage"/> is single-use - after
/// the first send, its content stream is consumed. Since retries need a fresh
/// request each attempt, we snapshot the original and clone it per attempt.
/// <para>
/// <b>Idempotency header:</b> When <see cref="IdempotencyContext.CurrentKey"/>
/// is non-null (the executor always sets it), the handler adds the configured
/// header to every attempt. Downstream services that dedupe on this header see
/// one logical write even when the pipeline retries.
/// </para>
/// </remarks>
public sealed class ResilientHttpMessageHandler : DelegatingHandler
{
    private readonly IResilienceExecutor _executor;
    private readonly string _policyName;
    private readonly HttpClientOptions _options;

    /// <summary>
    /// Creates a handler with default options (<c>Idempotency-Key</c> header).
    /// </summary>
    /// <param name="executor">The executor used to run each request.</param>
    /// <param name="policyName">The policy name for every request sent through this handler.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="executor"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
    public ResilientHttpMessageHandler(
        IResilienceExecutor executor,
        string policyName)
        : this(executor, policyName, options: null)
    {
    }

    /// <summary>
    /// Creates a handler with explicit options.
    /// </summary>
    /// <param name="executor">The executor used to run each request.</param>
    /// <param name="policyName">The policy name for every request sent through this handler.</param>
    /// <param name="options">Optional handler options. Defaults to a new <see cref="HttpClientOptions"/>.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="executor"/> is null.</exception>
    /// <exception cref="ArgumentException">If <paramref name="policyName"/> is null or whitespace.</exception>
    public ResilientHttpMessageHandler(
        IResilienceExecutor executor,
        string policyName,
        HttpClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        _executor = executor;
        _policyName = policyName;
        _options = options ?? new HttpClientOptions();
    }

    /// <summary>The policy name used for every request sent through this handler.</summary>
    public string PolicyName => _policyName;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await HttpRequestSnapshot.CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return await _executor.ExecuteAsync(
            _policyName,
            async attemptCt =>
            {
                var attemptRequest = snapshot.BuildRequest(request.RequestUri!);

                // Emit the ambient idempotency key on every attempt. The executor
                // guarantees a non-null key when we are inside ExecuteAsync.
                var key = IdempotencyContext.CurrentKey;
                if (!string.IsNullOrEmpty(key))
                {
                    attemptRequest.Headers.TryAddWithoutValidation(
                        _options.IdempotencyHeaderName,
                        key);
                }

                var response = await base.SendAsync(attemptRequest, attemptCt)
                    .ConfigureAwait(false);

                if (IsTransientHttpFailure(response.StatusCode))
                {
                    response.EnsureSuccessStatusCode();
                }

                return response;
            },
            fallback: null,
            ct: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientHttpFailure(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 425 || code == 429 || (code >= 500 && code <= 599);
    }
}
