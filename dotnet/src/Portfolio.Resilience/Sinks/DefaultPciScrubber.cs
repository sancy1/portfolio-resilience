// filepath: src/Portfolio.Resilience/Sinks/DefaultPciScrubber.cs
// layer: Sinks | package: Portfolio.Resilience | since: v0.8.0
// purpose: Regex-based scrubber that masks PAN, CVV, and SSN patterns before events reach a sink.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : IEventScrubber
//   Depends on : ResilienceEvent, System.Text.RegularExpressions
//   Used by    : CompositeLogSink (when configured), ServiceCollectionExtensions
//   See also   : docs/pci-scrubbing.md, SPEC.md section 19
// -----------------------------------------------------------------------------

using System.Text.RegularExpressions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Sinks;

/// <summary>
/// Default <see cref="IEventScrubber"/>. Masks three pattern families that
/// commonly appear in decline messages, validation errors, and provider echoes:
/// <list type="bullet">
///   <item>PAN - primary account number: 13 to 19 consecutive digits, optionally separated by spaces or dashes.</item>
///   <item>CVV/CVC - 3 or 4 digits adjacent to the tokens "cvv" or "cvc".</item>
///   <item>SSN - US Social Security Number in the canonical ddd-dd-dddd format.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// This scrubber is a <b>first line of defense, not a compliance guarantee</b>.
/// It catches common patterns but cannot catch every provider-specific format,
/// every locale's identity number format, or every way a caller might embed
/// sensitive data in metadata. Use it as one layer of a broader PCI strategy:
/// do not put sensitive data in events in the first place, and audit what
/// your services log.
/// </para>
/// <para>
/// Thread-safe. All regexes are compiled and immutable.
/// </para>
/// </remarks>
public sealed class DefaultPciScrubber : IEventScrubber
{
    /// <summary>Replacement token used to redact matched patterns.</summary>
    public const string Mask = "[REDACTED]";

    // PAN: 13-19 digits, optionally with single spaces or dashes between groups.
    // The negative lookbehind and lookahead prevent matching a longer digit run
    // (e.g. a 24-digit trace ID would otherwise lose its middle 19 digits).
    private static readonly Regex PanPattern = new(
        pattern: @"(?<!\d)(?:\d[\- ]?){12,18}\d(?!\d)",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // CVV: "cvv" or "cvc" followed by a colon/equals/space and 3-4 digits.
    private static readonly Regex CvvPattern = new(
        pattern: @"\b(?:cvv|cvc)\b\s*[:=]?\s*\d{3,4}\b",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // SSN: canonical US format ddd-dd-dddd.
    private static readonly Regex SsnPattern = new(
        pattern: @"\b\d{3}-\d{2}-\d{4}\b",
        options: RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public ResilienceEvent Scrub(ResilienceEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var scrubbedMessage = evt.ErrorMessage is null ? null : ScrubText(evt.ErrorMessage);
        var scrubbedType = evt.ErrorType is null ? null : ScrubText(evt.ErrorType);
        var scrubbedMetadata = ScrubMetadata(evt.Metadata);

        // Records are immutable; use `with` to return a scrubbed copy and leave
        // the caller's instance untouched.
        return evt with
        {
            ErrorMessage = scrubbedMessage,
            ErrorType = scrubbedType,
            Metadata = scrubbedMetadata
        };
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private static string ScrubText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Order matters: SSN before PAN, because an SSN matches neither PAN
        // (too few digits) nor CVV patterns, but running it last is also safe.
        // Running it first is slightly cheaper (fewer characters to scan for
        // the broader patterns below).
        var result = SsnPattern.Replace(value, Mask);
        result = CvvPattern.Replace(result, Mask);
        result = PanPattern.Replace(result, Mask);
        return result;
    }

    private static IReadOnlyDictionary<string, object?> ScrubMetadata(
        IReadOnlyDictionary<string, object?> metadata)
    {
        if (metadata.Count == 0)
        {
            return metadata;
        }

        var scrubbed = new Dictionary<string, object?>(metadata.Count);

        foreach (var kvp in metadata)
        {
            scrubbed[kvp.Key] = kvp.Value switch
            {
                string s => ScrubText(s),
                _ => kvp.Value
            };
        }

        return scrubbed;
    }
}
