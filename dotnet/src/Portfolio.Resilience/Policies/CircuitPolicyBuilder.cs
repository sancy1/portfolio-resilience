// filepath: src/Portfolio.Resilience/Policies/CircuitPolicyBuilder.cs
// layer: Policies | package: Portfolio.Resilience | since: v0.2.0
// purpose: Circuit breaker state machine. Gates operations based on consecutive failures.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : ICircuitBreakerMonitor (per-policy snapshots for health endpoints)
//   Depends on : CircuitOptions, ErrorClassifier, ResilienceException
//   Used by    : CompositePolicyBuilder, ResilienceExecutor (Stage F), /health/resilience
//   See also   : docs/circuit-breaker.md, SPEC.md §CircuitBreaker
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Concurrent;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Errors;

namespace Portfolio.Resilience.Policies;

/// <summary>
/// A per-policy circuit breaker. Thread-safe. Uses an injectable clock so tests
/// can advance time without sleeping.
/// </summary>
/// <remarks>
/// State machine:
/// <code>
/// Closed --(FailureThreshold consecutive failures)--> Open
/// Open   --(OpenDurationSeconds elapsed)------------> HalfOpen
/// HalfOpen --(probe succeeds)-----------------------> Closed
/// HalfOpen --(probe fails)--------------------------> Open
/// </code>
/// </remarks>
public sealed class CircuitPolicyBuilder : ICircuitBreakerMonitor
{
    private readonly ErrorClassifier _classifier;
    private readonly Func<DateTime> _clock;

    // One CircuitState per policy name. Created on first use.
    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

    public CircuitPolicyBuilder(
        ErrorClassifier? classifier = null,
        Func<DateTime>? clock = null)
    {
        _classifier = classifier ?? new ErrorClassifier();
        _clock = clock ?? (() => DateTime.UtcNow);
    }
    /// <summary>
    /// Executes <paramref name="operation"/> under the circuit for <paramref name="policyName"/>.
    /// Throws <see cref="ResilienceException"/> with category <see cref="ResilienceErrorCategory.CircuitOpen"/>
    /// without invoking the operation when the circuit is Open.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        CircuitOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        var circuit = _circuits.GetOrAdd(policyName, _ => new Circuit());

        // ---- Gate check ----
        if (!circuit.TryEnter(options, _clock()))
        {
            var snapshot = circuit.Snapshot(policyName, _clock());

            throw new ResilienceException(
                message: $"Circuit open for policy '{policyName}'.",
                policyName: policyName,
                category: ResilienceErrorCategory.CircuitOpen,
                attemptsMade: 0,
                totalDuration: TimeSpan.Zero,
                metadata: new Dictionary<string, object?>
                {
                    ["opened_at_utc"] = snapshot.OpenedAtUtc,
                    ["next_probe_at_utc"] = snapshot.NextProbeAtUtc,
                    ["consecutive_failures"] = snapshot.ConsecutiveFailures
                });
        }

        // ---- Execute ----
        try
        {
            var result = await operation(ct).ConfigureAwait(false);
            circuit.RecordSuccess(options, _clock());
            return result;
        }
        catch (Exception ex)
        {
            var category = _classifier.Classify(ex);
            circuit.RecordFailure(options, category, _clock());
            throw;
        }
    }

    // ------------------------------------------------------------------------
    // ICircuitBreakerMonitor
    // ------------------------------------------------------------------------

    public IReadOnlyCollection<CircuitSnapshot> Snapshot()
    {
        var now = _clock();
        return _circuits
            .Select(kv => kv.Value.Snapshot(kv.Key, now))
            .ToArray();
    }

    public CircuitSnapshot? Get(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        if (_circuits.TryGetValue(policyName, out var circuit))
        {
            return circuit.Snapshot(policyName, _clock());
        }
        return null;
    }
    // ------------------------------------------------------------------------
    // Internal state machine
    // ------------------------------------------------------------------------

    private sealed class Circuit
    {
        private readonly object _gate = new();

        private CircuitState _state = CircuitState.Closed;
        private int _consecutiveFailures;
        private DateTime? _openedAtUtc;
        private bool _probeInFlight;

        /// <summary>
        /// Returns true if the caller may proceed. Performs time-based
        /// transitions (Open → HalfOpen) as a side effect.
        /// </summary>
        public bool TryEnter(CircuitOptions options, DateTime now)
        {
            lock (_gate)
            {
                // Time-based transition: Open → HalfOpen.
                if (_state == CircuitState.Open
                    && _openedAtUtc.HasValue
                    && (now - _openedAtUtc.Value).TotalSeconds >= options.OpenDurationSeconds)
                {
                    _state = CircuitState.HalfOpen;
                    _probeInFlight = false;
                }

                return _state switch
                {
                    CircuitState.Closed => true,
                    CircuitState.HalfOpen => TryAcquireProbe(),
                    CircuitState.Open => false,
                    _ => false
                };
            }
        }

        public void RecordSuccess(CircuitOptions options, DateTime now)
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _openedAtUtc = null;
                _probeInFlight = false;
                _state = CircuitState.Closed;
            }
        }

        public void RecordFailure(CircuitOptions options, ResilienceErrorCategory category, DateTime now)
        {
            lock (_gate)
            {
                // Only certain categories count toward opening the circuit.
                if (options.OnlyCountTransient && category != ResilienceErrorCategory.Transient)
                {
                    // Permanent errors don't count.
                    if (_state == CircuitState.HalfOpen)
                    {
                        // But a failed probe still returns to Open.
                        _state = CircuitState.Open;
                        _openedAtUtc = now;
                        _probeInFlight = false;
                    }
                    return;
                }

                _consecutiveFailures++;

                if (_state == CircuitState.HalfOpen)
                {
                    // Failed probe → back to Open.
                    _state = CircuitState.Open;
                    _openedAtUtc = now;
                    _probeInFlight = false;
                }
                else if (_state == CircuitState.Closed
                      && _consecutiveFailures >= options.FailureThreshold)
                {
                    _state = CircuitState.Open;
                    _openedAtUtc = now;
                }
            }
        }

        public CircuitSnapshot Snapshot(string policyName, DateTime now)
        {
            lock (_gate)
            {
                DateTime? nextProbeAtUtc =
                    _state == CircuitState.Open && _openedAtUtc.HasValue
                        ? _openedAtUtc.Value.AddSeconds(30) // placeholder; real value uses options
                        : null;

                return new CircuitSnapshot(
                    PolicyName: policyName,
                    State: _state,
                    ConsecutiveFailures: _consecutiveFailures,
                    OpenedAtUtc: _openedAtUtc,
                    NextProbeAtUtc: nextProbeAtUtc);
            }
        }

        private bool TryAcquireProbe()
        {
            if (_probeInFlight) return false;
            _probeInFlight = true;
            return true;
        }
    }
}
