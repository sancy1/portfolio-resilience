// filepath: src/Portfolio.Resilience/Configuration/ErrorClassificationOptions.cs
// layer: Configuration | package: Portfolio.Resilience | since: v0.2.0
// purpose: Configurable rules for classifying exceptions into ResilienceErrorCategory.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (POCO)
//   Depends on : n/a
//   Used by    : ErrorClassifier, PolicyDefinition
//   See also   : docs/error-classification.md, SPEC.md §ErrorCategories
// ─────────────────────────────────────────────────────────────────────────────

namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Rules that determine how an arbitrary exception maps to a
/// <see cref="Errors.ResilienceErrorCategory"/>. All lists are overridable via
/// configuration (e.g. bound from environment variables) so services can tune
/// classification without code changes.
/// </summary>
public sealed class ErrorClassificationOptions
{
    /// <summary>HTTP status codes that indicate a retryable (transient) failure.</summary>
    public HashSet<int> TransientHttpStatusCodes { get; set; } = new()
    {
        408, // Request Timeout
        425, // Too Early
        429, // Too Many Requests
        500, // Internal Server Error
        502, // Bad Gateway
        503, // Service Unavailable
        504, // Gateway Timeout
        507, // Insufficient Storage
        509  // Bandwidth Limit Exceeded
    };

    /// <summary>HTTP status codes that indicate a permanent failure. Not retryable.</summary>
    public HashSet<int> PermanentHttpStatusCodes { get; set; } = new()
    {
        400, 401, 403, 404, 405, 406, 407, 409, 410,
        411, 412, 413, 414, 415, 416, 417, 418, 421, 422,
        423, 424, 426, 428, 431, 451
    };

    /// <summary>PostgreSQL SQLSTATE codes classified as transient (retryable).</summary>
    public HashSet<string> TransientSqlStates { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "08000", // Connection exception
        "08003", // Connection does not exist
        "08006", // Connection failure
        "08001", // Client unable to establish connection
        "08004", // Server rejected connection
        "08007", // Transaction resolution unknown
        "40001", // Serialization failure
        "40P01", // Deadlock detected
        "57P01", // Admin shutdown
        "57P02", // Crash shutdown
        "57P03", // Cannot connect now
        "53300", // Too many connections
        "55P03"  // Lock not available
    };

    /// <summary>Fully-qualified exception type names classified as transient.</summary>
    public HashSet<string> TransientExceptionTypeNames { get; set; } = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpRequestException",
        "System.Net.Sockets.SocketException",
        "System.IO.IOException",
        "System.TimeoutException",
        "Npgsql.NpgsqlException",
        "StackExchange.Redis.RedisConnectionException",
        "StackExchange.Redis.RedisTimeoutException"
    };

    /// <summary>Fully-qualified exception type names that are always permanent.</summary>
    public HashSet<string> PermanentExceptionTypeNames { get; set; } = new(StringComparer.Ordinal)
    {
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException",
        "System.InvalidOperationException",
        "System.NotSupportedException",
        "System.FormatException",
        "System.ValidationException"
    };

    /// <summary>
    /// When true, an <see cref="OperationCanceledException"/> caused by a caller
    /// cancellation token is classified as Permanent rather than Timeout.
    /// Default: true — a user-initiated cancel is not a failure to retry.
    /// </summary>
    public bool TreatCancellationAsPermanent { get; set; } = true;
}
