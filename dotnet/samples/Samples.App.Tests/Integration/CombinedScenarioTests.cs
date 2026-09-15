// filepath: dotnet/samples/Samples.App.Tests/Integration/CombinedScenarioTests.cs
// layer: Integration | package: Samples.App.Tests | since: n/a
// purpose: Verifies CombinedScenario composes the payment-safe pipeline and charge succeeds on retry
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a
//   Depends on : Samples.App.Scenarios.CombinedScenario
//   Used by    : dotnet test
//   See also   : Scenarios/10_CombinedScenario.cs
// -----------------------------------------------------------------------------

using FluentAssertions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;
using Xunit;

namespace Samples.App.Tests.Integration;

/// <summary>Integration tests for <see cref="CombinedScenario"/>.</summary>
public sealed class CombinedScenarioTests
{
    /// <summary>The payment-safe pipeline composes six layers in the documented order.</summary>
    [Fact]
    public async Task RunAsync_CompletesAndReportsPaymentSafeComposition()
    {
        var sink = new CapturingSink();

        var message = await CombinedScenario.RunAsync(sink, CancellationToken.None);

        message.Should().Contain("Payment-safe pipeline composed 6 layers in order");
        message.Should().Contain("RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout");
        message.Should().Contain("Charge succeeded on attempt 2 of 3");
    }
}