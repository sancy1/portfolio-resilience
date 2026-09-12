// filepath: src/Portfolio.Resilience/Configuration/RetryOptions.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Retry tuning for a policy.
/// Backoff formula: delay(n) = min(BaseDelayMs * 2^(n-1), MaxDelayMs) + jitter.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>Number of retry attempts after the initial call. 0 disables retry.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Base delay in milliseconds.</summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>Maximum delay cap in milliseconds.</summary>
    public int MaxDelayMs { get; set; } = 30_000;

    /// <summary>Jitter ratio applied to the base delay (0.0 to 1.0).</summary>
    public double JitterRatio { get; set; } = 0.3;

    /// <summary>Also retry on the first attempt when the operation is classified permanent? Default: no.</summary>
    public bool RetryOnPermanent { get; set; } = false;
}
