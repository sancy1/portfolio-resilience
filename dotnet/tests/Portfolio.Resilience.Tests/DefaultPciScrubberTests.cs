// filepath: tests/Portfolio.Resilience.Tests/DefaultPciScrubberTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.8.0
// purpose: Verifies DefaultPciScrubber masks PAN, CVV, and SSN patterns in messages and metadata.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : DefaultPciScrubber (Sinks/DefaultPciScrubber.cs)
//   Depends on : ResilienceEvent, ResilienceEventType, xUnit, FluentAssertions
//   See also   : docs/pci-scrubbing.md, SPEC.md section 19
// -----------------------------------------------------------------------------

using FluentAssertions;
using Portfolio.Resilience.Events;
using Portfolio.Resilience.Sinks;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class DefaultPciScrubberTests
{
    private readonly DefaultPciScrubber _scrubber = new();

    private static ResilienceEvent Event(
        string? message = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
        => new()
        {
            EventType = ResilienceEventType.CallFailed,
            PolicyName = "p",
            ErrorMessage = message,
            Metadata = metadata ?? new Dictionary<string, object?>()
        };

    // ------------------------------------------------------------------------
    // Guards
    // ------------------------------------------------------------------------

    [Fact]
    public void Scrub_ThrowsOnNullEvent()
    {
        Action act = () => _scrubber.Scrub(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ------------------------------------------------------------------------
    // PAN masking
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("card 4111111111111111 declined")]
    [InlineData("card 4111-1111-1111-1111 declined")]
    [InlineData("card 4111 1111 1111 1111 declined")]
    [InlineData("pan=5500005555555559")]
    public void Scrub_PanPattern_IsMasked(string input)
    {
        var scrubbed = _scrubber.Scrub(Event(message: input));

        scrubbed.ErrorMessage.Should().NotBeNull();
        scrubbed.ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
        scrubbed.ErrorMessage.Should().NotContain("4111");
        scrubbed.ErrorMessage.Should().NotContain("5500");
    }

    [Fact]
    public void Scrub_ShortDigitRun_IsNotMasked()
    {
        var scrubbed = _scrubber.Scrub(Event(message: "trace 12345678 done"));

        scrubbed.ErrorMessage.Should().Be("trace 12345678 done");
    }

    [Fact]
    public void Scrub_LongDigitRun_IsMaskedWholesale()
    {
        // 19 digits matches PAN pattern.
        var scrubbed = _scrubber.Scrub(Event(message: "id 1234567890123456789 end"));

        scrubbed.ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
        scrubbed.ErrorMessage.Should().NotContain("1234567890123456789");
    }

    // ------------------------------------------------------------------------
    // CVV masking
    // ------------------------------------------------------------------------

    [Theory]
    [InlineData("cvv 123")]
    [InlineData("CVV: 1234")]
    [InlineData("cvc=999")]
    [InlineData("Cvc = 4567")]
    public void Scrub_CvvPattern_IsMasked(string input)
    {
        var scrubbed = _scrubber.Scrub(Event(message: input));

        scrubbed.ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
    }

    // ------------------------------------------------------------------------
    // SSN masking
    // ------------------------------------------------------------------------

    [Fact]
    public void Scrub_SsnPattern_IsMasked()
    {
        var scrubbed = _scrubber.Scrub(Event(message: "ssn 123-45-6789 invalid"));

        scrubbed.ErrorMessage.Should().Contain(DefaultPciScrubber.Mask);
        scrubbed.ErrorMessage.Should().NotContain("123-45-6789");
    }

    [Fact]
    public void Scrub_NineDigitsWithoutDashes_IsNotMaskedAsSsn()
    {
        var scrubbed = _scrubber.Scrub(Event(message: "code 123456789 end"));

        scrubbed.ErrorMessage.Should().Be("code 123456789 end");
    }

    // ------------------------------------------------------------------------
    // Metadata
    // ------------------------------------------------------------------------

    [Fact]
    public void Scrub_StringValuesInMetadata_AreMasked()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["decline_reason"] = "card 4111111111111111 declined",
            ["cvv_echo"] = "cvv 123",
            ["trace"] = "abc-123"
        };

        var scrubbed = _scrubber.Scrub(Event(metadata: metadata));

        ((string)scrubbed.Metadata["decline_reason"]!).Should().Contain(DefaultPciScrubber.Mask);
        ((string)scrubbed.Metadata["cvv_echo"]!).Should().Contain(DefaultPciScrubber.Mask);
        scrubbed.Metadata["trace"].Should().Be("abc-123");
    }

    [Fact]
    public void Scrub_NonStringMetadataValues_ArePreserved()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["attempt"] = 3,
            ["duration_ms"] = 123.45,
            ["success"] = false
        };

        var scrubbed = _scrubber.Scrub(Event(metadata: metadata));

        scrubbed.Metadata["attempt"].Should().Be(3);
        scrubbed.Metadata["duration_ms"].Should().Be(123.45);
        scrubbed.Metadata["success"].Should().Be(false);
    }

    // ------------------------------------------------------------------------
    // Non-mutation contract
    // ------------------------------------------------------------------------

    [Fact]
    public void Scrub_DoesNotMutateInputEvent()
    {
        var original = Event(message: "card 4111111111111111 declined");

        _ = _scrubber.Scrub(original);

        original.ErrorMessage.Should().Be("card 4111111111111111 declined");
    }

    [Fact]
    public void Scrub_ReturnsNewInstance_EvenWhenNothingMatched()
    {
        var original = Event(message: "no sensitive data here");

        var scrubbed = _scrubber.Scrub(original);

        scrubbed.Should().NotBeSameAs(original);
        scrubbed.ErrorMessage.Should().Be(original.ErrorMessage);
    }

    // ------------------------------------------------------------------------
    // Combined patterns
    // ------------------------------------------------------------------------

    [Fact]
    public void Scrub_MessageWithMultiplePatterns_MasksAll()
    {
        var input = "card 4111111111111111 ssn 123-45-6789 cvv 123";

        var scrubbed = _scrubber.Scrub(Event(message: input));

        scrubbed.ErrorMessage.Should().NotContain("4111111111111111");
        scrubbed.ErrorMessage.Should().NotContain("123-45-6789");
        scrubbed.ErrorMessage.Should().NotContain("123");
    }

    // ------------------------------------------------------------------------
    // Null and empty handling
    // ------------------------------------------------------------------------

    [Fact]
    public void Scrub_NullErrorMessage_IsPreserved()
    {
        var scrubbed = _scrubber.Scrub(Event(message: null));

        scrubbed.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Scrub_EmptyMetadata_IsPreserved()
    {
        var scrubbed = _scrubber.Scrub(Event(metadata: new Dictionary<string, object?>()));

        scrubbed.Metadata.Should().BeEmpty();
    }
}
