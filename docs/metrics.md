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
- `InFlight` — currently executing (currently always 0; wired in Stage F)

### The shipped implementation

**InMemoryMetricSink** — keeps the last N (default 1000) duration samples per policy in a
bounded queue. On `Snapshot()`, sorts a copy and computes percentiles via linear interpolation.
Thread-safe. Bounded memory.

**CompositeMetricSink** — fans out each sample to multiple sinks. If one throws, others
still receive the sample.

## Where metrics are produced

Today (Stage C): nothing produces metrics yet. The sink exists, it works, but nobody calls
`RecordCall`. This is deliberate — the tracker lands in Stage E, and the executor wires
it into the call path in Stage F.

From Stage F onward, every `IResilienceExecutor.ExecuteAsync(...)` call records a sample
automatically. You never call `RecordCall` yourself.

## Reading metrics: the /health/resilience endpoint

Stage H adds a health endpoint that reads from `ILatencyTracker` and `ICircuitBreakerMonitor`:

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

Metrics have no configuration on the sink itself beyond `windowSize`. Future stages will
add a sampling ratio (record 1 in N calls) for very-high-throughput services.

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
- [circuit-breaker.md](circuit-breaker.md) — how metrics inform circuit decisions (Stage E)
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
