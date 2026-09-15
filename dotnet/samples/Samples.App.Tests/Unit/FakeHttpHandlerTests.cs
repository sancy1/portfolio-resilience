// filepath: dotnet/samples/Samples.App.Tests/Unit/FakeHttpHandlerTests.cs
// layer: Unit | package: Samples.App.Tests | since: n/a
// purpose: Verifies FakeHttpHandler returns scripted responses and records requests
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Infrastructure.FakeHttpHandler
//   Used by    : dotnet test
//   See also   : Infrastructure/FakeHttpHandler.cs
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Samples.App.Infrastructure;
using Xunit;

namespace Samples.App.Tests.Unit;

/// <summary>Unit tests for <see cref="FakeHttpHandler"/>.</summary>
public sealed class FakeHttpHandlerTests
{
    /// <summary>Constructor with empty script throws.</summary>
    [Fact]
    public void Constructor_EmptyScript_Throws()
    {
        Action act = () => _ = new FakeHttpHandler();
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>A single scripted response is returned for the first request.</summary>
    [Fact]
    public async Task SendAsync_SingleScript_ReturnsScriptedResponse()
    {
        using var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var resp = await client.GetAsync("https://example.test/x");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestCount.Should().Be(1);
    }

    /// <summary>Multiple scripted responses are returned in order.</summary>
    [Fact]
    public async Task SendAsync_MultipleScript_ReturnsInOrder()
    {
        using var handler = new FakeHttpHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var r1 = await client.GetAsync("https://example.test/a");
        var r2 = await client.GetAsync("https://example.test/b");

        r1.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestCount.Should().Be(2);
    }

    /// <summary>Last script is reused if more requests arrive than scripted.</summary>
    [Fact]
    public async Task SendAsync_MoreRequestsThanScript_ReusesLast()
    {
        using var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        for (int i = 0; i < 5; i++)
        {
            await client.GetAsync("https://example.test/x");
        }

        handler.RequestCount.Should().Be(5);
    }

    /// <summary>WithStatus helper returns the given status on every request.</summary>
    [Fact]
    public async Task WithStatus_ReturnsStatusOnEveryRequest()
    {
        using var handler = FakeHttpHandler.WithStatus(HttpStatusCode.TooManyRequests);
        using var client = new HttpClient(handler);

        var r1 = await client.GetAsync("https://example.test/a");
        var r2 = await client.GetAsync("https://example.test/b");

        r1.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        r2.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}