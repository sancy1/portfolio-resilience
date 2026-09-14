<!--
filepath: docs/metrics.md
package:  Portfolio.Resilience | since: v0.2.0
purpose:  Explains latency tracking, percentiles, error rates, and the /health/resilience endpoint.
-->

# Metrics

## What it is

Latency tracking for every resilience-wrapped call. Each call records its duration,
whether it succeeded, and how many attempts it took. The library aggregates those samples
into rolling statistics: p50, p95, p99, error rate, average, and in-flight count — per policy.

Where logging answers "what happened?", metrics answer "how is it going?".

## Why it exists

Three reasons.

**One: detect degradation before it becomes an incident.** If p95 for `auth-service` creeps
from 120ms to 800ms over an hour, you want to know before users complain. p95 is a leading
indicator; errors are a lagging one.

**Two: validate circuit decisions.** When a circuit opens, it should be because a policy
genuinely failed — high error rate, high latency. Metrics give you the evidence to tune
thresholds (5 failures? 10? 30 seconds? 60?).

**Three: expose health for orchestration.** Kubernetes, load balancers, and your own
`/health/resilience` endpoint read from these metrics to decide whether a pod is healthy
and whether to route traffic to it.

## When you need it

**Always.** Even in development, seeing that a retry added 5 seconds to a response is
useful. In production, metrics are your only continuous signal — logs are forensic, metrics
are operational.

If a service does not register any metric sink, the library falls back to a no-op sink.
Calls still work; you just do not see the numbers.

## How it works

One abstraction, one shipped implementation.

### The abstraction

Location: `src/Portfolio.Resilience/Abstractions/IMetricSink.cs`

    public interface IMetricSink
    {
        void RecordCall(string policyName, TimeSpan duration, bool success, int attempts);
    }

One method. Called once per executed operation. Implementations decide whether to store,
forward, or discard.

### The snapshot

Location: `src/Portfolio.Resilience/Abstractions/ILatencyTracker.cs`

Consumers read metrics through `LatencySnapshot`:

- `PolicyName` — which policy
- `TotalCalls` — total call count (not just windowed)
- `FailedCalls` — total failures
- `ErrorRate` — `FailedCalls / TotalCalls`, as a fraction
- `P50Ms`, `P95Ms`, `P99Ms` — percentiles over the rolling window
- `AvgMs` — mean over the rolling window
- `InFlight` — currently executing

### The shipped implementation

**InMemoryMetricSink** — keeps the last N (default 1000) duration samples per policy in a
bounded queue. On `Snapshot()`, sorts a copy and computes percentiles via linear interpolation.
Thread-safe. Bounded memory.

**CompositeMetricSink** — fans out each sample to multiple sinks. If one throws, others
still receive the sample.

## Where metrics are produced

Every `IResilienceExecutor.ExecuteAsync(...)` call records a sample automatically.
You never call `RecordCall` yourself. The executor wires the metric sink into the
call path once per operation, at the end, with the total duration (including retries)


## Reading metrics: the /health/resilience endpoint

A health endpoint reads from `ILatencyTracker` and `ICircuitBreakerMonitor` to expose the current state:

    GET /health/resilience

    {
      "policies": [
        {
          "name": "auth-service",
          "circuitState": "Closed",
          "p50Ms": 45,
          "p95Ms": 120,
          "p99Ms": 340,
          "errorRate": 0.02,
          "totalCalls": 1240,
          "inFlight": 3
        },
        {
          "name": "db.contact.write",
          "circuitState": "HalfOpen",
          "p50Ms": 12,
          "p95Ms": 60,
          "p99Ms": 180,
          "errorRate": 0.08,
          "totalCalls": 8901,
          "inFlight": 0
        }
      ]
    }

Operators read this at a glance. If `circuitState` is not `Closed`, something is wrong with
that dependency. If `p99Ms` is climbing, watch it.

## Choosing a window size

`InMemoryMetricSink` takes a `windowSize` constructor parameter (default 1000).

- **Smaller (100–500)** — more reactive to recent behavior. Good for detecting fast-moving
  incidents. Noisier when load is low.
- **Larger (1000–10000)** — smoother, more stable percentiles. Good for stable services.
  Slower to reflect a sudden degradation.

**Recommendation:** start with the default 1000. Tune once you have real traffic patterns.

## Configuration

Metrics have no configuration on the sink itself beyond `windowSize`. A sampling ratio
(record 1 in N calls) for very-high-throughput services is on the roadmap for a future release.

If you need a Prometheus or OpenTelemetry exporter, register an additional sink that
forwards to that system. The library stays out of the exporter business — that is what
the ecosystem is for.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Abstractions/IMetricSink.cs` | Sink contract |
| `src/Portfolio.Resilience/Abstractions/ILatencyTracker.cs` | Read-side interface + snapshot type |
| `src/Portfolio.Resilience/Sinks/InMemoryMetricSink.cs` | Rolling window + percentiles |
| `src/Portfolio.Resilience/Sinks/CompositeMetricSink.cs` | Fan-out |

## Percentile method

The library uses **linear interpolation** on the sorted window. For a window of N samples:

- `rank = p * (N - 1)`
- `lower = floor(rank)`, `upper = ceil(rank)`
- `value = sorted[lower] * (1 - weight) + sorted[upper] * weight`
  where `weight = rank - lower`

This is the "R-7" percentile method, standard in most statistical software.

For `[10, 20, 30]`:

- p50 = 20 (exact middle)
- p95 = 20 * 0.05 + 30 * 0.95 = 29.5
- p99 = 20 * 0.01 + 30 * 0.99 = 29.9

If you need a different method (nearest-rank, or a histogram-based estimate), implement a
custom `IMetricSink`. The abstraction is the seam.

## Common mistakes

**Mistake 1 — recording inside a hot loop.**
Metrics are cheap but not free. If you call a policy 10,000 times per second, the sort
inside `Snapshot()` becomes measurable. Snapshot is O(N log N) and called rarely; `RecordCall`
is O(1). Fine for typical loads. If you hit this ceiling, add a sampled sink.

**Mistake 2 — treating p99 as an average.**
p99 = 340ms does **not** mean calls take about 340ms. It means 99% of calls are under 340ms,
and 1% are above. Use p50 as "typical" and p99 as "worst common case".

**Mistake 3 — resetting metrics on deploy.**
The rolling window is in-memory. A restart clears it. If you need persistence across
deploys, forward to Prometheus or another external system.

**Mistake 4 — using error rate without volume.**
0.5 error rate on 4 calls is noise. 0.05 error rate on 10,000 calls is a real problem.
Always read `errorRate` alongside `totalCalls`.

**Mistake 5 — exposing raw metric values to unauthenticated callers.**
`/health/resilience` reveals internal service names, latencies, and failure rates. In
production, protect it. Use the same gateway secret or admin authentication you use elsewhere.

## Testing

`InMemoryMetricSink` is deterministic and fast to test:

    var sink = new InMemoryMetricSink(windowSize: 100);
    sink.RecordCall("test-policy", TimeSpan.FromMilliseconds(10), success: true, attempts: 1);
    sink.RecordCall("test-policy", TimeSpan.FromMilliseconds(20), success: true, attempts: 1);
    sink.RecordCall("test-policy", TimeSpan.FromMilliseconds(30), success: false, attempts: 3);

    var snap = sink.Get("test-policy");

    Assert.Equal(3, snap!.TotalCalls);
    Assert.Equal(1, snap.FailedCalls);
    Assert.Equal(1.0 / 3.0, snap.ErrorRate, precision: 4);
    Assert.Equal(20, snap.P50Ms, precision: 2);

No external services, no timing dependencies. Just numbers in, numbers out.

## See also

- [logging.md](logging.md) — the sibling concern to metrics
- [correlation.md](correlation.md) — how correlation ties samples to requests
- [circuit-breaker.md](circuit-breaker.md) — how metrics inform circuit decisions
- [../SPEC.md](../SPEC.md) §Metrics — the normative metric name contract
- [../README.md](../README.md) — package install and quick-start

---

## Test coverage

Verified by `tests/Portfolio.Resilience.Tests/InMemoryMetricSinkTests.cs` (19 tests)
and `tests/Portfolio.Resilience.Tests/CompositeMetricSinkTests.cs` (7 tests):

- Percentile math: p50 with odd and even counts, p95, p99, linear interpolation
- Error rate computation, including zero-call safety
- Rolling window bounds: cumulative counts never reset, percentiles reflect window only
- Multi-policy isolation and case-insensitive policy name handling
- Thread safety: 1,000 parallel `RecordCall` invocations produce exact count
- Composite fan-out forwards every field verbatim (policy, duration, success, attempts)
- Composite exception isolation: one broken sink cannot stop the others
## How to use it — a worked walkthrough

This section walks through wiring metrics into a real service, reading them
in a health endpoint, and interpreting the numbers in a monitoring tool.

### Step 1 — Confirm the default sink is registered

`AddPortfolioResilience` registers an `InMemoryMetricSink` automatically.
There is nothing to configure for the common case:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts = 3;
            p.Timeout.TimeoutMs = 5000;
        }));

After registration, the container can resolve `ILatencyTracker`:

    var tracker = provider.GetRequiredService<ILatencyTracker>();
    var snap = tracker.Get("auth-service");
    // snap == null until at least one call has been recorded

**You do not need to call `RecordCall` yourself.** The executor records a
sample once per `ExecuteAsync` call, with the total duration (including
retries) and the final outcome.

### Step 2 — Expose the metrics through a health endpoint

The library ships `ILatencyTracker` and `ICircuitBreakerMonitor` specifically
so you can write a small health endpoint. Example controller:

    [ApiController]
    [Route("health/resilience")]
    public sealed class ResilienceHealthController : ControllerBase
    {
        private readonly ILatencyTracker _tracker;
        private readonly ICircuitBreakerMonitor _circuits;

        public ResilienceHealthController(
            ILatencyTracker tracker,
            ICircuitBreakerMonitor circuits)
        {
            _tracker = tracker;
            _circuits = circuits;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var circuits = _circuits.Snapshot().ToDictionary(c => c.PolicyName);
            var policies = _tracker.Snapshot().Select(l => new
            {
                name = l.PolicyName,
                circuitState = circuits.TryGetValue(l.PolicyName, out var c)
                    ? c.State.ToString()
                    : "Unknown",
                p50Ms = l.P50Ms,
                p95Ms = l.P95Ms,
                p99Ms = l.P99Ms,
                errorRate = l.ErrorRate,
                totalCalls = l.TotalCalls,
                inFlight = l.InFlight
            });

            return Ok(new { policies });
        }
    }

**Protect this endpoint in production.** It reveals internal policy names,
latencies, and failure rates. Use the same authentication as your other
admin endpoints.

### Step 3 — Read the numbers with the right questions

A single snapshot is not useful. The useful questions are comparative:

| Question | Where to look |
|----------|--------------|
| Is the typical call fast? | `p50Ms` should be stable and low for the dependency's class (e.g. 5–50ms for in-cluster, 50–200ms for external) |
| Is the tail acceptable? | `p99Ms` should be within the dependency's SLA. If p99 is >10× p50, you have a long-tail problem |
| Are failures rare? | `errorRate` should be under the budget for the dependency (e.g. <1% for a healthy internal service) |
| Is the dependency under stress? | Watch `inFlight` — a rising count means calls are piling up |
| Is the circuit healthy? | `circuitState` should be `Closed`. `Open` means the dependency is down; `HalfOpen` means it is probing recovery |

### Step 4 — React to a bad signal

Three concrete scenarios and the right response:

**Scenario A — p99 climbs but p50 is stable.**

Something rare is slow. Possibly a cache miss path, a lock contention issue,
or a specific request shape. Pull the slow traces by filtering logs for
`duration_ms > 500`. Fix the slow path — do not raise the timeout.

**Scenario B — errorRate climbs slowly.**

A dependency is degrading. Check the `error_category` distribution in your
log sink. If `Transient` dominates, the dependency is having intermittent
trouble. If `Timeout` dominates, the ceiling is firing — either the
dependency is slower than its SLA, or the ceiling is too low.

**Scenario C — inFlight never returns to zero.**

Something is hanging. Check whether a call is ignoring its cancellation token.
A well-behaved operation honors `ct.ThrowIfCancellationRequested()` at await
points; one that ignores it holds a thread indefinitely.

### Step 5 — Forward metrics to a real system

The in-memory sink is fine for a health endpoint. For long-term dashboards
and alerting, forward samples to Prometheus, OpenTelemetry, Datadog, or
similar. Two options:

**Option A — install the OpenTelemetry package.** `Portfolio.Resilience.OpenTelemetry`
ships an `OpenTelemetryMetricSink` that exports every sample as native OTel
histograms and counters. See [opentelemetry.md](opentelemetry.md).

**Option B — write your own sink.** Implement `IMetricSink` and register it
via `AddMetricSink(...)`. The composite sink fans out to every registered
sink, so the in-memory sink keeps powering the health endpoint while your
custom sink forwards to the external system:

    public sealed class PrometheusMetricSink : IMetricSink
    {
        private static readonly Histogram<double> Duration = Metrics
            .CreateHistogram<double>("resilience_call_duration_ms", "ms");

        public void RecordCall(string policyName, TimeSpan duration, bool success, int attempts)
        {
            Duration.Record(
                duration.TotalMilliseconds,
                new KeyValuePair<string, object?>("policy", policyName),
                new KeyValuePair<string, object?>("success", success));
        }
    }

    // Registration
    builder.Services.AddPortfolioResilience(r => r
        .AddMetricSink(new PrometheusMetricSink())
        .AddPolicy("auth-service", p => { /* ... */ }));

**The composite sink isolates exceptions.** If your custom sink throws, the
in-memory sink still receives the sample, and the pipeline does not fail.

### Step 6 — Choose a window size

`InMemoryMetricSink` keeps the last 1000 samples per policy by default. Two
things to know:

- **Smaller windows** (100–500) are more reactive but noisier at low volume.
- **Larger windows** (1000–10000) are smoother but slower to reflect a sudden
  change.

**Start with the default.** For a service calling a dependency fewer than
10 times per second, 1000 samples cover 1.5+ minutes of history — enough to
see trends. For higher volumes, the window represents a shorter window of
wall-clock time, so consider increasing it.

To change the size, register a custom sink with the desired window:

    builder.Services.AddSingleton<IMetricSink>(
        new InMemoryMetricSink(windowSize: 5000));

**Note:** this replaces the automatic in-memory sink. If you want both the
default and a larger window, register the larger one as an *additional* sink
via `AddMetricSink(...)` — the composite keeps both.

### A note on what NOT to do

**Do not read `errorRate` in isolation.** `errorRate = 0.5` on four calls is
noise. `errorRate = 0.05` on 100,000 calls is a real problem. Always read the
rate **alongside `totalCalls`**.

**Do not reset metrics on deploy.** The rolling window is in-memory. A restart
clears it. If you need history across deploys, forward to Prometheus or OTel
using the pattern in Step 5.

**Do not expose the endpoint unauthenticated.** Internal policy names,
latency distributions, and failure rates are operational intelligence. In a
production environment, protect them.
