// filepath: dotnet/samples/Samples.App/Infrastructure/FakeHttpHandler.cs
// layer: Infrastructure | package: Samples.App | since: n/a
// purpose: In-process HttpMessageHandler that returns scripted responses and records every request
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : System.Net.Http.HttpMessageHandler
//   Depends on : System.Net.Http primitives
//   Used by    : Scenarios 07 and 10 (HTTP-header assertions), Unit tests
//   See also   : docs/http-integration.md
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Http;

namespace Samples.App.Infrastructure;

/// <summary>
/// A <see cref="HttpMessageHandler"/> that returns scripted responses in order and
/// records every <see cref="HttpRequestMessage"/> it receives. Used by the sample to
/// exercise HTTP-header behavior (Idempotency-Key, X-Correlation-Id) without a network.
/// </summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<Func<HttpRequestMessage, HttpResponseMessage>> _script;
    private readonly List<HttpRequestMessage> _requests = new();
    private int _index;

    /// <summary>Creates a handler with the given script.</summary>
    /// <param name="script">Ordered response factories. Must not be null or empty.</param>
    public FakeHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Length == 0)
        {
            throw new ArgumentException("At least one scripted response is required.", nameof(script));
        }
        _script = new List<Func<HttpRequestMessage, HttpResponseMessage>>(script);
    }

    /// <summary>Creates a handler that returns the same status code for every request.</summary>
    /// <param name="statusCode">The status code to return on every response.</param>
    /// <returns>A new handler.</returns>
    public static FakeHttpHandler WithStatus(HttpStatusCode statusCode)
    {
        return new FakeHttpHandler(_ => new HttpResponseMessage(statusCode));
    }

    /// <summary>A snapshot of every request seen so far, in arrival order.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get { lock (_requests) { return _requests.ToArray(); } }
    }

    /// <summary>The number of requests received so far.</summary>
    public int RequestCount
    {
        get { lock (_requests) { return _requests.Count; } }
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_requests) { _requests.Add(request); }

        var factory = _script[Math.Min(_index, _script.Count - 1)];
        _index++;

        var response = factory(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}