// filepath: src/Portfolio.Resilience/Configuration/LoggingOptions.cs
namespace Portfolio.Resilience.Configuration;

/// <summary>
/// Controls which event types are emitted. Default: all except verbose ones.
/// </summary>
public sealed class LoggingOptions
{
    public bool EmitCallStarted    { get; set; } = false;
    public bool EmitRetryAttempted { get; set; } = true;
    public bool EmitCallSucceeded  { get; set; } = true;
    public bool EmitCallFailed     { get; set; } = true;
    public bool EmitCircuitEvents  { get; set; } = true;
    public bool EmitFallbackUsed   { get; set; } = true;
    public bool EmitTimeoutBreached{ get; set; } = true;
}
