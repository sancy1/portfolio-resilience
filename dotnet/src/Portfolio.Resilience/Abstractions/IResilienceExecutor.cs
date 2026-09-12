// filepath: src/Portfolio.Resilience/Abstractions/IResilienceExecutor.cs
namespace Portfolio.Resilience.Abstractions;

/// <summary>
/// The main entry point. Every external call in every service goes through one of these methods.
/// Retries, circuit breaking, timeout, fallback, structured logging, and latency tracking
/// all happen inside.
/// </summary>
public interface IResilienceExecutor
{
    /// <summary>Execute an async operation that returns a value.</summary>
    Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task<T>>? fallback = null,
        CancellationToken ct = default);

    /// <summary>Execute an async operation that returns no value.</summary>
    Task ExecuteAsync(
        string policyName,
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task>? fallback = null,
        CancellationToken ct = default);
}
