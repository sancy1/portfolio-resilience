<!--
filepath: docs/circuit-breaker.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Explains the circuit breaker state machine, thresholds, and half-open probing.
-->

# Circuit Breaker

## What it is

A circuit breaker **stops calling a dependency** once it starts failing, and
periodically tests whether the dependency has recovered. When it has, normal calls
resume. When it has not, calls are rejected immediately without touching the
dependency.

The circuit has three states and a small, deterministic state machine:

    Closed  --(N consecutive failures)-->  Open
    Open    --(OpenDurationSeconds pass)--> HalfOpen
    HalfOpen --(probe succeeds)--> Closed
    HalfOpen --(probe fails)--> Open

Circuit breaker is the **middle** layer in the pipeline:

    Caller -> Retry -> Circuit -> Timeout -> Operation

Circuit sits **between** retry and timeout. Retry decides whether to attempt again;
the circuit decides whether the attempt is allowed at all.

## Why it exists

Imagine an external service is down. Without a circuit breaker:

- Every request waits for the full timeout (say 5000ms).
- Retry multiplies that by 3 attempts — 15+ seconds per request.
- Your thread pool fills up with requests waiting on a broken dependency.
- Your service appears down even though the problem is elsewhere.
- When the dependency comes back, a thundering herd of queued requests hits it
  simultaneously and knocks it over again.

The circuit breaker breaks this cycle:

- After 5 failures, the circuit opens. Subsequent calls are rejected in
  **microseconds** — no wait, no timeout, no thread consumption.
- After 30 seconds, a single probe request tests the dependency.
- If it succeeds, the circuit closes and normal traffic resumes at full capacity.
- If it fails, the circuit stays open for another 30 seconds.

**The whole point is to fail fast when failure is inevitable.**

## When you need it

**Yes, for every external dependency:**
- Another microservice (auth, notification, content)
- PostgreSQL (in the resilience wrapper sense, not the EF-level)
- Redis
- Third-party APIs (Stripe, OpenAI, etc.)

**No, for:**
- In-process operations with no external dependency (parsing, math, local logic)
- Operations that must always be attempted (health checks, admin diagnostics)

**The rule of thumb:** if the operation depends on a resource that can be
temporarily unavailable and whose unavailability degrades your service, protect it
with a circuit breaker.
## How it works

### The three states

**Closed** (normal operation)
- Every call passes through.
- Successes reset the consecutive-failure counter to 0.
- Failures increment the counter.
- At `FailureThreshold` consecutive failures, transition to `Open`.

**Open** (failing fast)
- Every call is rejected with `ResilienceException(CircuitOpen)`.
- The operation is **never invoked** — no wait, no timeout, no resource use.
- After `OpenDurationSeconds` since opening, transition to `HalfOpen` on the
  next call.

**HalfOpen** (probing)
- The **next single call** is allowed through as a probe.
- Only one probe is allowed in-flight at a time. Other concurrent calls are
  rejected with `CircuitOpen`.
- If the probe succeeds → reset counter, transition to `Closed`.
- If the probe fails → reset `OpenedAtUtc`, transition back to `Open`.

### State transition rules

| From | Condition | To | Side effect |
|------|-----------|----|----|
| `Closed` | `ConsecutiveFailures >= FailureThreshold` | `Open` | Record `OpenedAtUtc = now` |
| `Open` | `now - OpenedAtUtc >= OpenDurationSeconds` | `HalfOpen` | Mark probe slot available |
| `HalfOpen` | Probe succeeds | `Closed` | Reset `ConsecutiveFailures = 0` |
| `HalfOpen` | Probe fails | `Open` | Reset `OpenedAtUtc = now` |

### Why "consecutive" failures, not "total"

A service that succeeds 999 times and fails 5 times does not have a problem.
A service that fails 5 times **in a row** is down.

Consecutive counting means:
- Success resets the counter.
- A failure spike opens the circuit.
- A recovered dependency closes it on the first success (in HalfOpen).

### Which failures count

By default, only errors classified as `Transient` count toward the threshold.
Permanent errors (400 Bad Request, `ArgumentException`) do **not** count — a bug
in our code should not trip the circuit for a healthy dependency.

Control this with `CircuitOptions.OnlyCountTransient`:
- `true` (default): only `Transient` errors count.
- `false`: all errors count.

**A failed probe in HalfOpen always reopens the circuit**, regardless of
`OnlyCountTransient`. This is because the probe's whole purpose is to test the
dependency — if it fails, the dependency is still broken.

### Thread safety

`CircuitPolicyBuilder` is designed for concurrent use:
- Each policy has its own `Circuit` object.
- Each `Circuit` has its own lock.
- State transitions are atomic under the lock.
- The `_probeInFlight` flag ensures only one HalfOpen probe runs at a time,
  even if 100 threads hit the circuit simultaneously.

### State is per-process by default

Circuit state lives in `ConcurrentDictionary<string, Circuit>` inside the
`CircuitPolicyBuilder` singleton.

**Implication:** in a horizontally-scaled deployment with N instances, each
instance has its own circuit state. If a dependency fails, all N circuits will
open independently (roughly in parallel, but not instantly synchronized).

**This is usually fine.** Each instance is protecting its own resources. If you
need shared circuit state (e.g., to prevent stampedes on a shared dependency),
you would implement an `ICircuitBreakerMonitor` backed by Redis. The interface
exists for this reason.
## Configuration

Circuit tuning lives in `CircuitOptions`, per policy:

| Option | Default | Meaning |
|--------|---------|---------|
| `FailureThreshold` | `5` | Consecutive failures before opening |
| `OpenDurationSeconds` | `30` | How long to stay Open before probing |
| `SuccessThreshold` | `1` | Successful probes in HalfOpen required to close (currently always 1) |
| `OnlyCountTransient` | `true` | Only `Transient` errors count toward threshold |

**Set via `AddPolicy`:**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p =>
        {
            p.Circuit.FailureThreshold = 5;
            p.Circuit.OpenDurationSeconds = 30;
            p.Circuit.OnlyCountTransient = true;
        }));

**Set via `appsettings.json`:**

    {
      "Resilience": {
        "Policies": {
          "auth-service": {
            "Circuit": {
              "FailureThreshold": 5,
              "OpenDurationSeconds": 30,
              "OnlyCountTransient": true
            }
          }
        }
      }
    }

**Set via environment variable:**

    Resilience__Policies__auth-service__Circuit__FailureThreshold=5
    Resilience__Policies__auth-service__Circuit__OpenDurationSeconds=30

## Choosing FailureThreshold and OpenDurationSeconds

Too low → the circuit opens on a blip; normal traffic gets rejected.
Too high → the circuit stays closed through a real outage; load is wasted.

**Recommended by dependency:**

| Dependency | FailureThreshold | OpenDurationSeconds |
|------------|------------------|---------------------|
| Fast internal service (auth) | `5` | `15` |
| Slower internal service (content) | `10` | `30` |
| External API (rate-limited) | `10` | `60` |
| Database (shared, critical) | `20` | `30` |
| Redis | `10` | `15` |

**Starting guidance:** assume ~1% of normal traffic fails transiently. Choose a
threshold high enough that a 1% failure rate does not open the circuit. That
usually means threshold ≥ 5 for low-traffic dependencies, higher for high-traffic.

**OpenDurationSeconds guidance:** long enough that a transient outage (DNS blip,
brief network partition) heals before the probe, short enough that a real
recovery is detected quickly. 15–60 seconds is the usual range.

## Observing circuit state

The library exposes `ICircuitBreakerMonitor` for health endpoints:

    // Injected via DI
    var monitor = sp.GetRequiredService<ICircuitBreakerMonitor>();

    // Get one policy's state
    var snapshot = monitor.Get("auth-service");
    snapshot.State             // Closed | Open | HalfOpen
    snapshot.ConsecutiveFailures
    snapshot.OpenedAtUtc
    snapshot.NextProbeAtUtc

    // Get every policy's state
    var all = monitor.Snapshot();

This is what powers `/health/resilience` in consuming services. The endpoint
typically returns a JSON array of `{ policy, state, consecutiveFailures, ... }`.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Policies/CircuitPolicyBuilder.cs` | The circuit implementation |
| `src/Portfolio.Resilience/Configuration/CircuitOptions.cs` | Configuration model |
| `src/Portfolio.Resilience/Abstractions/ICircuitBreakerMonitor.cs` | Read-side interface + `CircuitState` + `CircuitSnapshot` |
| `src/Portfolio.Resilience/Implementation/CircuitBreakerMonitor.cs` | Aggregates multiple sources |
| `src/Portfolio.Resilience/Errors/ErrorClassifier.cs` | Determines what counts as a failure |

## Common mistakes

**Mistake 1 — choosing a threshold that opens on every blip.**
If your dependency fails 2% of the time normally, a `FailureThreshold = 3` opens
the circuit constantly. Measure normal failure rate first; set the threshold well
above the "consecutive bad luck" range.

**Mistake 2 — treating Open as "permanently broken".**
A circuit opens when the dependency is unhealthy. It closes automatically once the
dependency recovers (via the HalfOpen probe). No manual intervention is needed.

**Mistake 3 — trying to reset the circuit manually.**
There is no `Reset()` method. The state machine is time-driven. If you need to
force-close a circuit (e.g., after deploying a fix), restart the process — but
this is rare, and usually indicates the probe duration is too long.

**Mistake 4 — expecting shared circuit state across instances.**
Each process has its own circuit. In Kubernetes with 10 replicas, 10 circuits
open independently. If this is a problem, implement a Redis-backed monitor.
For most services, per-process circuits are correct.

**Mistake 5 — using OnlyCountTransient = false without reason.**
If our code has a bug that throws `ArgumentException` on every request, and
`OnlyCountTransient = false`, the circuit will open against a healthy dependency.
Leave the default.

**Mistake 6 — not exposing circuit state for observability.**
Without `/health/resilience`, you cannot tell from outside whether a circuit is
open. Include the endpoint in every service.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/CircuitPolicyBuilderTests.cs` (11 tests):

- Null/whitespace policy name rejected
- Success in Closed state → stays Closed, counter reset
- `FailureThreshold` consecutive transient failures → Open
- Circuit Open → operation never invoked, `ResilienceException(CircuitOpen)` thrown
- Permanent failures → do not count by default
- Permanent failures → count when `OnlyCountTransient = false`
- HalfOpen after `OpenDurationSeconds` → next call allowed as probe
- HalfOpen probe success → Closed
- HalfOpen probe failure → Open, `OpenedAtUtc` reset
- Different policies → independent circuits
- `Snapshot()` → returns every known circuit

## See also

- [error-classification.md](error-classification.md) — what counts as a failure
- [retry.md](retry.md) — how retry interacts with the circuit
- [timeout.md](timeout.md) — timeout runs inside the circuit gate
- [executor.md](executor.md) — where the circuit sits in the pipeline
- [../SPEC.md](../SPEC.md) §CircuitBreaker — the normative state machine
