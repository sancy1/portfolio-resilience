// filepath: src/Portfolio.Resilience/Errors/ResilienceException.cs
namespace Portfolio.Resilience.Errors;

/// <summary>
/// Thrown when a resilience-wrapped operation fails permanently
/// (all retries exhausted, or circuit open, and no fallback provided).
/// </summary>
public sealed class ResilienceException : Exception
{
    public string PolicyName { get; }
    public ResilienceErrorCategory Category { get; }
    public int AttemptsMade { get; }
    public TimeSpan TotalDuration { get; }
    public string? CorrelationId { get; }
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    public ResilienceException(
        string message,
        string policyName,
        ResilienceErrorCategory category,
        int attemptsMade,
        TimeSpan totalDuration,
        string? correlationId = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        Exception? inner = null)
        : base(message, inner)
    {
        PolicyName     = policyName;
        Category       = category;
        AttemptsMade   = attemptsMade;
        TotalDuration  = totalDuration;
        CorrelationId  = correlationId;
        Metadata       = metadata ?? new Dictionary<string, object?>();
    }
}
