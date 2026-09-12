// filepath: src/Portfolio.Resilience/Abstractions/ILogSink.cs
using Portfolio.Resilience.Events;

namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// Receives structured resilience events. Implementations may write to
/// console, file, OpenTelemetry, cloud logging, etc. This is the seam
/// that lets a service swap logging destinations without touching call sites.
/// </summary>
public interface ILogSink
{
    void Emit(ResilienceEvent evt);
}
