// filepath: src/Portfolio.Resilience/Configuration/HttpClientOptions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.8.0
// purpose: Options for the resilient HTTP handler - header name for the ambient idempotency key.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : n/a
//   Used by    : ResilientHttpMessageHandler, HttpClientBuilderExtensions
//   See also   : docs/http-integration.md, docs/idempotency.md, SPEC.md section 18
// -----------------------------------------------------------------------------

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Configuration for <c>ResilientHttpMessageHandler</c>. Controls which
/// HTTP header carries the ambient idempotency key on every outbound attempt.
/// </summary>
/// <remarks>
/// <para>
/// The default header name is <c>Idempotency-Key</c>, which Stripe, Adyen,
/// Square, and most modern payment providers accept natively. Override it for
/// a provider that uses a different convention.
/// </para>
/// <para>
/// The header is emitted on <b>every</b> attempt - primary and every retry -
/// with the same value, so a downstream deduplicator sees a single logical
/// write rather than multiple.
/// </para>
/// </remarks>
public sealed class HttpClientOptions
{
    /// <summary>
    /// The default HTTP header name used to carry the idempotency key.
    /// Matches the convention used by Stripe, Adyen, and Square.
    /// </summary>
    public const string DefaultHeaderName = "Idempotency-Key";

    /// <summary>
    /// The HTTP header name used to carry the idempotency key. Defaults to
    /// <see cref="DefaultHeaderName"/>. Must not be null or whitespace.
    /// </summary>
    public string IdempotencyHeaderName { get; set; } = DefaultHeaderName;

    /// <summary>
    /// Returns human-readable warnings for suspicious configuration.
    /// Never throws. Called once per handler construction.
    /// </summary>
    /// <returns>A list of warning strings; empty when configuration is valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(IdempotencyHeaderName))
        {
            warnings.Add(
                $"HttpClientOptions.IdempotencyHeaderName must not be null or whitespace; " +
                $"defaulting to '{DefaultHeaderName}' at runtime.");
        }

        return warnings;
    }
}
