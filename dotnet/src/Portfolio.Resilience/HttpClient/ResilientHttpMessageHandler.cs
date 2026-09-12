// filepath: src/Portfolio.Resilience/HttpClient/ResilientHttpMessageHandler.cs
// layer: HttpClient | package: Portfolio.Resilience | since: v0.4.0
// purpose: DelegatingHandler that routes every HttpClient request through the resilience executor.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : DelegatingHandler
//   Depends on : IResilienceExecutor, HttpRequestMessage, HttpResponseMessage
//   Used by    : HttpClientBuilderExtensions.AddResilientHttpClient, all consumer services
//   See also   : docs/http-integration.md, SPEC.md §HttpIntegration
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Http;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.HttpClient;

/// <summary>
/// Sends HTTP requests through the resilience pipeline (retry -> circuit -> timeout).
/// Non-success HTTP status codes (5xx, 408, 429) are treated as failures and
/// participate in retry and circuit accounting.
/// </summary>
/// <remarks>
/// <b>Request cloning:</b> <see cref="HttpRequestMessage"/> is single-use — after
/// the first send, its content stream is consumed. Since retries need a fresh
/// request each attempt, we snapshot the original and clone it per attempt.
/// <para>
/// <b>Non-success as failure:</b> The handler calls <c>EnsureSuccessStatusCode()</c>
/// inside the operation so 5xx/408/429 responses throw, get classified as
/// transient, and trigger retry. Without this, a 503 would be returned to the
/// caller on the first attempt with no retry.
/// </para>
/// </remarks>
public sealed class ResilientHttpMessageHandler : DelegatingHandler
{
    private readonly IResilienceExecutor _executor;
    private readonly string _policyName;

    public ResilientHttpMessageHandler(
        IResilienceExecutor executor,
        string policyName)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        _executor = executor;
        _policyName = policyName;
    }

    /// <summary>The policy name used for every request sent through this handler.</summary>
    public string PolicyName => _policyName;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Snapshot the request so we can rebuild it per attempt.
        var snapshot = await HttpRequestSnapshot.CaptureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return await _executor.ExecuteAsync(
            _policyName,
            async attemptCt =>
            {
                var attemptRequest = snapshot.BuildRequest(request.RequestUri!);
                var response = await base.SendAsync(attemptRequest, attemptCt)
                    .ConfigureAwait(false);

                // Force retry on transient HTTP failures (5xx, 408, 429).
                if (IsTransientHttpFailure(response.StatusCode))
                {
                    response.EnsureSuccessStatusCode();  // throws HttpRequestException with StatusCode
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
