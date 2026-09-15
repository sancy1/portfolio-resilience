// filepath: dotnet/samples/Samples.App/Scenarios/02_CircuitScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates the circuit breaker - opens after N failures, rejects, recovers via HalfOpen probe
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : IResilienceExecutor, CapturingSink, ICircuitBreakerMonitor (optional), CircuitState
//   Used by    : Program.cs, Integration tests
//   See also   : docs/circuit-breaker.md, README.md - Findings
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Abstractions;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 02 - Circuit breaker. Demonstrates the full state machine:
/// Closed -> Open after two consecutive transient failures; a third call is
/// rejected without invoking the operation; after the open duration elapses,
/// a HalfOpen probe succeeds and the circuit closes.
/// </summary>
/// <remarks>
/// As of Portfolio.Resilience 0.8.0, circuit events (CircuitOpened,
/// CircuitHalfOpened, CircuitClosed) are not delivered to registered
/// <c>ILogSink</c> instances, so the scenario proves the state machine by
/// behavior instead: the operation is not invoked while the circuit is Open,
/// and it is invoked again after the open duration. When an
/// <see cref="ICircuitBreakerMonitor"/> is available, it is additionally used
/// to read the circuit state directly. See README.md - Findings.
/// </remarks>
public static class CircuitScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    /// <param name="executor">The resilience executor resolved from the DI container.</param>
    /// <param name="sink">The capturing sink. Captured but not used for circuit assertions.</param>
    /// <param name="monitor">Optional circuit breaker monitor. Null if not registered.</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <returns>A one-line message describing what was verified.</returns>
    public static async Task<string> RunAsync(
        IResilienceExecutor executor,
        CapturingSink sink,
        ICircuitBreakerMonitor? monitor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(sink);

        sink.Clear();

        var invocationCount = 0;
        Func<CancellationToken, Task<int>> failing = _ =>
        {
            invocationCount++;
            throw new TimeoutException("simulated transient dependency failure");
        };

        // --- Phase 1: two transient failures open the circuit. ---
        for (var i = 0; i < 2; i++)
        {
            try
            {
                await executor.ExecuteAsync<int>(
                    policyName: "circuit-policy",
                    operation: failing,
                    fallback: null!,
                    idempotencyKey: null,
                    timeBudgetMs: null,
                    ct: ct);
                throw new InvalidOperationException($"expected call {i + 1} to fail, but it succeeded");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("expected call"))
            {
                throw;
            }
            catch (Exception)
            {
                // expected
            }
        }

        if (invocationCount != 2)
        {
            throw new InvalidOperationException(
                $"expected 2 invocations before the circuit opens, got {invocationCount}");
        }

        // Nullable-safe: two ?. so the .State access is also guarded.
        CircuitState? stateAfterFailures = monitor?.Get("circuit-policy")?.State;

        // --- Phase 2: circuit should be Open. Next call must be rejected without invoking. ---
        var invocationsBeforeRejection = invocationCount;
        try
        {
            await executor.ExecuteAsync<int>(
                policyName: "circuit-policy",
                operation: failing,
                fallback: null!,
                idempotencyKey: null,
                timeBudgetMs: null,
                ct: ct);
            throw new InvalidOperationException("expected the circuit to reject the call, but it succeeded");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("expected the circuit"))
        {
            throw;
        }
        catch (Exception)
        {
            // expected
        }

        if (invocationCount != invocationsBeforeRejection)
        {
            throw new InvalidOperationException(
                $"the operation was invoked while the circuit was Open " +
                $"(before={invocationsBeforeRejection}, after={invocationCount})");
        }

        // --- Phase 3: wait past OpenDurationSeconds, then probe succeeds. ---
        await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);

        var recovered = await executor.ExecuteAsync<int>(
            policyName: "circuit-policy",
            operation: _ => Task.FromResult(99),
            fallback: null!,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: ct);

        if (recovered != 99)
        {
            throw new InvalidOperationException($"expected 99 after recovery, got {recovered}");
        }

        CircuitState? stateAfterRecovery = monitor?.Get("circuit-policy")?.State;

        // --- Assert on monitor state if available. ---
        if (stateAfterFailures is not null && stateAfterFailures != CircuitState.Open)
        {
            throw new InvalidOperationException(
                $"expected circuit state Open after failures, got {stateAfterFailures}");
        }
        if (stateAfterRecovery is not null && stateAfterRecovery != CircuitState.Closed)
        {
            throw new InvalidOperationException(
                $"expected circuit state Closed after recovery, got {stateAfterRecovery}");
        }

        var monitorNote = monitor is null
            ? " (monitor not registered; state verified by behavior only)"
            : $" (monitor confirmed Open -> Closed)";

        return $"Circuit opened after 2 transient failures, rejected the next call, " +
               $"and recovered via HalfOpen probe{monitorNote}.";
    }
}