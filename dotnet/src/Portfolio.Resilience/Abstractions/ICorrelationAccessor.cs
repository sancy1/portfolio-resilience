// filepath: src/Portfolio.Resilience/Abstractions/ICorrelationAccessor.cs
namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// Provides the ambient correlation ID for the current async flow.
/// The default implementation uses AsyncLocal and echoes X-Correlation-Id.
/// </summary>
public interface ICorrelationAccessor
{
    string? Current { get; }
    IDisposable Push(string correlationId);
}
