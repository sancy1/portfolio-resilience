// filepath: src/Portfolio.Resilience/HttpClient/HttpRequestSnapshot.cs
// layer: HttpClient | package: Portfolio.Resilience | since: v0.4.0
// purpose: Captures an HttpRequestMessage's immutable parts so retries can rebuild a fresh request.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (internal helper)
//   Depends on : HttpRequestMessage, HttpMethod, HttpContent
//   Used by    : ResilientHttpMessageHandler
//   See also   : docs/http-integration.md
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Http;

namespace Portfolio.Resilience.HttpClient;

/// <summary>
/// Captures the immutable parts of an <see cref="HttpRequestMessage"/> so that
/// multiple retry attempts can each build a fresh request with the same method,
/// headers, and body. Necessary because <see cref="HttpRequestMessage"/> is
/// single-use — its content stream is consumed on first send.
/// </summary>
internal sealed class HttpRequestSnapshot
{
    private readonly HttpMethod _method;
    private readonly List<KeyValuePair<string, IEnumerable<string>>> _headers;
    private readonly byte[]? _bodyBytes;
    private readonly string? _contentType;

    private HttpRequestSnapshot(
        HttpMethod method,
        List<KeyValuePair<string, IEnumerable<string>>> headers,
        byte[]? bodyBytes,
        string? contentType)
    {
        _method = method;
        _headers = headers;
        _bodyBytes = bodyBytes;
        _contentType = contentType;
    }

    /// <summary>Captures the request's method, headers, and body.</summary>
    public static async Task<HttpRequestSnapshot> CaptureAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var method = request.Method;
        var headers = request.Headers.ToList();
        byte[]? bodyBytes = null;
        string? contentType = null;

        if (request.Content is not null)
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            contentType = request.Content.Headers.ContentType?.ToString();

            // Capture custom content headers (Content-Type, Content-Encoding, etc.).
            foreach (var h in request.Content.Headers)
            {
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                headers.Add(new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value));
            }
        }

        return new HttpRequestSnapshot(method, headers, bodyBytes, contentType);
    }

    /// <summary>Builds a fresh request with the captured method, headers, and body.</summary>
    public HttpRequestMessage BuildRequest(Uri requestUri)
    {
        var request = new HttpRequestMessage(_method, requestUri);

        foreach (var header in _headers)
        {
            // Some headers must go through TryAddWithoutValidation because
            // they are technically content headers or need custom formatting.
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (_bodyBytes is not null)
        {
            request.Content = new ByteArrayContent(_bodyBytes);
            if (!string.IsNullOrEmpty(_contentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", _contentType);
            }
        }

        return request;
    }
}
