// filepath: tests/Portfolio.Resilience.Tests/ErrorClassifierTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.2.0
// purpose: Verifies ErrorClassifier maps HTTP codes, SQLSTATEs, and exception types to categories.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : ErrorClassifier (Errors/ErrorClassifier.cs)
//   Depends on : ErrorClassificationOptions, xUnit, FluentAssertions
//   See also   : docs/error-classification.md
// ─────────────────────────────────────────────────────────────────────────────

using System.Net;
using FluentAssertions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class ErrorClassifierTests
{
    private readonly ErrorClassifier _classifier = new();

    // ------------------------------------------------------------------------
    // Constructor + validation
    // ------------------------------------------------------------------------

    [Fact]
    public void Classify_ThrowsOnNullException()
    {
        Action act = () => _classifier.Classify(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_UsesDefaultsWhenOptionsNull()
    {
        var classifier = new ErrorClassifier(null);
        classifier.Classify(new InvalidOperationException("x"))
            .Should().Be(ResilienceErrorCategory.Permanent);
    }

    // ------------------------------------------------------------------------
    // Passthrough
    // ------------------------------------------------------------------------

    [Fact]
    public void Classify_ResilienceException_PassesThroughCategory()
    {
        var rex = new ResilienceException(
            message: "wrapped",
            policyName: "p",
            category: ResilienceErrorCategory.CircuitOpen,
            attemptsMade: 0,
            totalDuration: TimeSpan.Zero);

        _classifier.Classify(rex).Should().Be(ResilienceErrorCategory.CircuitOpen);
    }

    // ------------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------------

    [Fact]
    public void Classify_OperationCanceled_WithRequestedToken_IsPermanent()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = new OperationCanceledException(cts.Token);
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Permanent);
    }

    [Fact]
    public void Classify_OperationCanceled_WithoutRequestedToken_IsTimeout()
    {
        var ex = new OperationCanceledException();
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Timeout);
    }
    // ------------------------------------------------------------------------
    // HTTP status codes
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Classify_TransientHttpCodes_AreTransient(int statusCode)
    {
        var ex = new HttpRequestException("boom", null, (HttpStatusCode)statusCode);
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Transient);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public void Classify_PermanentHttpCodes_ArePermanent(int statusCode)
    {
        var ex = new HttpRequestException("nope", null, (HttpStatusCode)statusCode);
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Permanent);
    }

    [Fact]
    public void Classify_Unlisted5xx_IsTransient()
    {
        var ex = new HttpRequestException("mystery 5xx", null, (HttpStatusCode)599);
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public void Classify_Unlisted4xx_IsPermanent()
    {
        var ex = new HttpRequestException("mystery 4xx", null, (HttpStatusCode)499);
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Permanent);
    }

    [Fact]
    public void Classify_HttpRequestExceptionWithoutStatusCode_IsTransient()
    {
        // HttpRequestException with no StatusCode comes from our type-name list.
        var ex = new HttpRequestException("network blip");
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Transient);
    }
    // ------------------------------------------------------------------------
    // Type names
    // ------------------------------------------------------------------------

    [Fact]
    public void Classify_TimeoutException_IsTransient()
    {
        // NOTE: System.TimeoutException is a BCL exception for network/I/O
        // timeouts. It is classified as Transient because a retry may succeed.
        // ResilienceErrorCategory.Timeout is reserved for when the library's own
        // timeout policy fires — that happens in TimeoutPolicyBuilder, not here.
        _classifier.Classify(new TimeoutException())
            .Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public void Classify_SocketException_IsTransient()
    {
        var ex = new System.Net.Sockets.SocketException(
            (int)System.Net.Sockets.SocketError.TimedOut);
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public void Classify_IOException_IsTransient()
    {
        _classifier.Classify(new IOException("disk?")).Should().Be(ResilienceErrorCategory.Transient);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(FormatException))]
    public void Classify_PermanentExceptionTypes_ArePermanent(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "test")!;
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Permanent);
    }

    [Fact]
    public void Classify_UnknownExceptionType_IsPermanentByDefault()
    {
        var ex = new CustomUnknownException("who knows");
        _classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Permanent);
    }

    // ------------------------------------------------------------------------
    // Configurability
    // ------------------------------------------------------------------------

    [Fact]
    public void Classify_RespectsCustomHttpCodeList()
    {
        var options = new ErrorClassificationOptions();
        options.TransientHttpStatusCodes.Add(418); // normally permanent

        var classifier = new ErrorClassifier(options);
        var ex = new HttpRequestException("teapot", null, (HttpStatusCode)418);

        classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Transient);
    }

    [Fact]
    public void Classify_RespectsCancellationAsPermanentToggle()
    {
        var options = new ErrorClassificationOptions
        {
            TreatCancellationAsPermanent = false
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var classifier = new ErrorClassifier(options);
        var ex = new OperationCanceledException(cts.Token);

        classifier.Classify(ex).Should().Be(ResilienceErrorCategory.Timeout);
    }

    private sealed class CustomUnknownException : Exception
    {
        public CustomUnknownException(string message) : base(message) { }
    }
}
