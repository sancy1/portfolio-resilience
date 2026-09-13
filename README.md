<!--
filepath: README.md
package:  Portfolio.Resilience | since: v0.6.0
purpose:  Main project README — install, quick-start, feature overview, and links.
-->

# Portfolio.Resilience

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-273%20passing-brightgreen.svg)](#)
[![NuGet](https://img.shields.io/badge/nuget-Portfolio.Resilience-blue.svg)](https://www.nuget.org/packages/Portfolio.Resilience)

> **Resilience primitives for .NET.** Rate limiter, bulkhead, retry, circuit
> breaker, timeout, fallback, correlation, structured logging, and latency
> metrics — in one small library with a cross-language spec.

Built for microservice architectures where every HTTP call, database query, and
cache lookup can fail transiently — or overwhelm a dependency if it does not.
Instead of hand-rolling retry logic in each service (differently, each time),
call one method:

    var user = await _resilience.ExecuteAsync(
        "auth-service",
        ct => _authClient.GetUserAsync(id, ct),
        fallback: ct => Task.FromResult(UserDto.Anonymous),
        ct: ct);

The library owns the pipeline. Your service owns the operation.

---

## Why this library exists

Modern services make hundreds of outbound calls per request. Each one can fail
transiently — a DNS blip, a TCP reset, a briefly overloaded dependency, a slow
database. Without protection:

- Every transient failure becomes a user-visible error.
- A single slow dependency exhausts your thread pool.
- A burst of traffic slams a rate-limited dependency.
- The circuit between "the network was busy" and "the user saw an error" is lost.

With `Portfolio.Resilience`, every outbound call goes through a battle-tested
pipeline:

    Caller -> RateLimiter -> Bulkhead -> Retry -> Circuit Breaker -> Timeout -> Operation

Each layer does one thing:

| Layer | Responsibility | Docs |
|-------|---------------|------|
| **Rate Limiter** | Caps how many calls may proceed per time period | [rate-limiter.md](docs/rate-limiter.md) |
| **Bulkhead** | Caps how many calls may run concurrently | [bulkhead.md](docs/bulkhead.md) |
| **Retry** | Absorbs transient failures with exponential backoff + jitter | [retry.md](docs/retry.md) |
| **Circuit Breaker** | Fails fast when a dependency is genuinely broken | [circuit-breaker.md](docs/circuit-breaker.md) |
| **Timeout** | Bounds every attempt | [timeout.md](docs/timeout.md) |
| **Executor** | Coordinates the pipeline + fallback + observability | [executor.md](docs/executor.md) |

And three cross-cutting concerns:

| Concern | What it gives you | Docs |
|---------|------------------|------|
| **Structured Logging** | One JSON event per pipeline decision, sink-swappable | [logging.md](docs/logging.md) |
| **Metrics** | p50/p95/p99, error rate, in-flight counts, per policy | [metrics.md](docs/metrics.md) |
| **Correlation** | One ID across every service in a request trace | [correlation.md](docs/correlation.md) |

---

## Install

    dotnet add package Portfolio.Resilience

Requires **.NET 10** or later. No other dependencies.

## Quick start

### 1. Register in `Program.cs`

    using Portfolio.Resilience.Extensions;
    using Portfolio.Resilience.Sinks;

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts            = 3;
            p.Retry.BaseDelayMs            = 100;
            p.Circuit.FailureThreshold     = 5;
            p.Circuit.OpenDurationSeconds  = 30;
            p.Timeout.TimeoutMs            = 5000;
        })
        .AddPolicy("external-api", p =>
        {
            // Rate limit: at most 100 calls per minute
            p.RateLimiter.Enabled            = true;
            p.RateLimiter.Strategy           = RateLimitStrategy.SlidingWindow;
            p.RateLimiter.PermitLimit        = 100;
            p.RateLimiter.WindowSeconds      = 60;

            // Bulkhead: at most 20 in flight, queue up to 10 more
            p.Bulkhead.Enabled               = true;
            p.Bulkhead.MaxConcurrency        = 20;
            p.Bulkhead.MaxQueue              = 10;
            p.Bulkhead.QueueTimeoutMs        = 2000;

            p.Timeout.TimeoutMs              = 10000;
        }));

### 2. Inject the executor

    using Portfolio.Resilience.Abstractions;

    public sealed class UserService
    {
        private readonly IResilienceExecutor _resilience;
        private readonly System.Net.Http.HttpClient _http;

        public UserService(
            IResilienceExecutor resilience,
            IHttpClientFactory httpFactory)
        {
            _resilience = resilience;
            _http       = httpFactory.CreateClient("auth-service");
        }

### 3. Wrap your calls

    public Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct)
    {
        return _resilience.ExecuteAsync(
            "auth-service",
            async token => await _http.GetFromJsonAsync<UserDto>(
                $"/api/v1/users/{id}", token),
            fallback: token => Task.FromResult<UserDto?>(UserDto.Anonymous),
            ct: ct);
    }

That is the **entire integration** for a call site. Rate limiting, bulkhead
isolation, retry, circuit breaking, timeout, logging, metrics, and correlation
all happen inside `ExecuteAsync`.

### Logging scenarios

Logging is **optional and composable**. Every pipeline decision emits a
structured JSON event. Whether those events go to a file, the console, a cloud
provider, or nowhere at all is your choice at registration time.

**Scenario 1 — Local only (typical for development or containers with stdout collection):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())    // stdout — captured by Docker/K8s
        .AddPolicy("auth-service", p => { /* ... */ }));

**Scenario 2 — Local file (for hosts without stdout collection, or a local audit trail):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new FileLogSink("/var/log/resilience"))   // daily rotating JSON Lines
        .AddPolicy("auth-service", p => { /* ... */ }));

**Scenario 3 — Cloud only (fully managed observability):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new OpenTelemetrySink(otelEndpoint))       // user-written, ~20 lines
        .AddPolicy("auth-service", p => { /* ... */ }));

**Scenario 4 — Hybrid (local + cloud, belt-and-suspenders):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new FileLogSink("/var/log/resilience"))    // local backup
        .AddLogSink(new DatadogSink(apiKey: config["DD_API_KEY"]))   // cloud dashboards
        .AddPolicy("auth-service", p => { /* ... */ }));

**Scenario 5 — Silent (nothing logged):**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p => { /* ... */ }));
    // No AddLogSink call — NullLogSink (silent default)

**How the choice is made:**

| Scenario | What you call | Where events land |
|----------|---------------|-------------------|
| Local only | `AddLogSink(ConsoleLogSink)` or `AddLogSink(FileLogSink)` | Local |
| Cloud only | `AddLogSink(CustomCloudSink)` | Cloud |
| **Hybrid** | **Multiple `AddLogSink(...)` calls** | **Both** |
| Silent | *(no `AddLogSink` call)* | Nowhere |

**`AddLogSink` composes.** Every call adds another destination. Multiple calls
= multiple sinks, automatically wired through `CompositeLogSink`. A broken cloud
sink never blocks the local one — a per-sink exception policy isolates them.

See [docs/logging.md](docs/logging.md) for the full event schema, sample cloud
sink implementations, and migration patterns.

## HttpClient integration

If you prefer the handler pattern — every `HttpClient` request through a policy:

    builder.Services
        .AddHttpClient("auth-service", c =>
        {
            c.BaseAddress = new Uri(config["AUTH_SERVICE_URL"]!);
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddResilientHandler("auth-service");

Then use `HttpClient` normally. Every request goes through the pipeline for the
`auth-service` policy.

See [http-integration.md](docs/http-integration.md) for the full story.

---

## Features

### Rate limiter

Caps how many calls may proceed in a given time period. Four strategies:

    TokenBucket    - smooth refill, allows bursts up to capacity
    SlidingWindow  - precise; no boundary effects (default)
    FixedWindow    - cheapest; allows 2x burst at boundaries
    ConcurrencyLimit - caps simultaneous calls instead of rate

Rejections are fast and local — the dependency never sees the request. Each
rejection emits a `rate_limited` event with strategy, permit limit, queue depth,
and reason.

### Bulkhead

Caps how many calls may run concurrently against a resource. Prevents thread
pool exhaustion when a dependency slows down.

    Caller -> Bulkhead (MaxConcurrency = 20) -> operation
                    |
                    +-- additional callers wait up to QueueTimeoutMs (bounded by MaxQueue)
                    +-- beyond that: rejected with bulkhead_rejected

The semaphore is held across the operation and released in a `finally` block,
so slots return on success, failure, or cancellation.

### Retry

Exponential backoff with jitter. Every attempt is bounded, and every delay is
formula-driven:

    delay(n) = min(base_delay * 2^(n-1), max_delay) + jitter(0, ratio * base_delay)

Only `Transient` errors are retried. Permanent errors (400 Bad Request,
`ArgumentException`) fail immediately — no wasted retries.

### Circuit breaker

Three states, deterministic transitions, thread-safe:

    Closed  --(N consecutive failures)-->  Open
    Open    --(open_duration elapsed)-->   HalfOpen
    HalfOpen --(probe succeeds)-->          Closed
    HalfOpen --(probe fails)-->             Open

While `Open`, the operation is never invoked. Rejections cost microseconds.
Recovery is automatic via the half-open probe.

### Timeout

Per-attempt ceiling. `TimeoutMs = 5000` bounds each attempt, not the whole
retry sequence. Distinguishes **user cancellation** from **our timeout** — a
user closing a browser tab does not trigger retries or count against the circuit.

### Fallback

Optional per-call fallback. When the pipeline fails, the fallback runs outside
the pipeline and produces a degraded response. Falls back to a clear
`ResilienceException` if no fallback is provided.

### Structured logging

Every pipeline decision emits a `ResilienceEvent`. The default sink is silent;
opt in to `ConsoleLogSink`, `FileLogSink`, or a custom `ILogSink`:

    {"event_type":"retry_attempted","policy_name":"auth-service",
     "correlation_id":"abc-123","attempt":2,"metadata":{"delay_ms":120}}

Cross-language JSON schema is defined in [SPEC.md §7](SPEC.md).

### Metrics

p50, p95, p99, error rate, in-flight counts — per policy. Exposed via
`ILatencyTracker` and typically surfaced through a `/health/resilience`
endpoint:

    GET /api/v1/health/resilience

    {
      "policies": [
        { "name": "auth-service", "circuitState": "Closed",
          "p50Ms": 45, "p95Ms": 120, "p99Ms": 340,
          "errorRate": 0.02, "totalCalls": 1240, "inFlight": 3 }
      ]
    }

### Correlation

Every event, exception, and metric sample carries a `correlation_id`. The
`X-Correlation-Id` HTTP header propagates automatically across services.
One user request → one trace ID → every log line and metric is linked.

### Error classification

Every failure is classified into one of six categories:
`Transient`, `Permanent`, `CircuitOpen`, `Timeout`, `FallbackUsed`, `Unknown`.
The category drives retry and circuit decisions — and gives you a
language-neutral way to write alerting rules.

See [error-classification.md](docs/error-classification.md).

---

## Architecture

    LandingPageService.API           (your service)
      |
      +-- IResilienceExecutor         <-- the entry point
            |
            +-- RateLimiterPolicyBuilder    (outermost)
                  |
                  +-- BulkheadPolicyBuilder
                        |
                        +-- RetryPolicyBuilder
                              |
                              +-- CircuitPolicyBuilder
                                    |
                                    +-- TimeoutPolicyBuilder (innermost)
                                          |
                                          +-- your operation
                                                |
                                                +-- HttpClient / EF Core / Redis / ...

Two side channels:

- **ILogSink** — receives structured events at each decision point
- **IMetricSink** — receives latency/outcome samples; feeds ILatencyTracker

Everything is swappable. `AddPortfolioResilience` wires sensible defaults; each
piece can be replaced.

## Comparison with other libraries

**Polly** is the .NET standard for resilience — 200M+ downloads, .NET Foundation
member, and the base for Microsoft's own `Microsoft.Extensions.Resilience`. This
library is **not a Polly replacement.** It has a different design center.

### Feature comparison

| Feature | Portfolio.Resilience | Polly | MS.Extensions.Http.Resilience |
|---------|---------------------|-------|-------------------------------|
| Retry + exponential backoff + jitter | ✅ | ✅ | ✅ |
| Circuit breaker | ✅ | ✅ | ✅ |
| Timeout per attempt | ✅ | ✅ | ✅ |
| Fallback | ✅ | ✅ | ✅ |
| **Rate limiter** | ✅ **v0.6.0** | ✅ | ✅ |
| **Bulkhead isolation** | ✅ **v0.6.0** | ✅ | ✅ |
| **Hedging** | 🔜 v0.7.0 | ✅ | ✅ |
| **Policy composition (Wrap)** | 🔜 v0.7.0 | ✅ | ✅ |
| **Chaos engineering (Simmy)** | ❌ | ✅ | ❌ |
| **OpenTelemetry integration** | 🔜 v0.7.0 | ✅ | ✅ |
| **Built-in cloud sinks** | 🔜 v0.8.0 | ecosystem | ecosystem |
| **Ambient correlation IDs** | ✅ **built-in** | manual | partial |
| **Structured event schema (cross-language)** | ✅ **SPEC.md** | manual | no |
| **Latency percentiles built-in** | ✅ **no OTel required** | OTel only | OTel only |
| **Zero external dependencies** | ✅ | ✅ | ✅ (Polly) |
| .NET 10 target | ✅ | ✅ | ✅ |
| Ecosystem maturity | new | 200M+ downloads | Microsoft-backed |

### What this library does differently

Three design decisions that Polly deliberately leaves to the consumer:

1. **Correlation IDs are built in.** Every event, exception, and metric carries
   a `correlation_id` automatically. One HTTP header (`X-Correlation-Id`)
   propagates across services. With Polly you wire this yourself.

2. **Structured events have a cross-language contract.** `SPEC.md` defines every
   event name (`retry_attempted`, `circuit_opened`, `rate_limited`, etc.), every
   field name (`policy_name`, `attempt`, `duration_ms`), every metric name (`p50_ms`,
   `error_rate`), and every error category. A future Python or Go implementation
   produces **identical output**, so dashboards work across languages.

3. **Observability is included, not deferred.** p50/p95/p99 latency, error rates,
   and in-flight counts are computed by the library. No OpenTelemetry setup
   required. If you already use OTel, our events feed it via a sink — but OTel
   is not mandatory.

### When to choose Polly / MS.Extensions.Resilience

- You want the industry standard.
- You need hedging, chaos engineering, or policy composition **today**.
- You're already invested in the Polly / OpenTelemetry ecosystem.
- You want the widest ecosystem of plugins and community support.

### When to choose Portfolio.Resilience

- You want **correlation IDs and structured observability built in** rather than
  assembled from separate pieces.
- You have **multiple services in different languages** and want a consistent
  event schema and error taxonomy.
- You want a **small, dependency-free library** with no transitive package chain.
- You want to learn from or extend a well-documented, spec-driven codebase.

### Can I use both?

Yes — they operate at different layers. Some teams use Polly for the fine-grained
HTTP client pipeline and Portfolio.Resilience for the higher-level operation
pipeline (database calls, Redis, cross-service business operations). Neither
library knows or cares about the other.

---

## Documentation

Full documentation lives in [`docs/`](docs/):

| Doc | What it covers |
|-----|---------------|
| [docs/README.md](docs/README.md) | Index and reading order |
| [docs/correlation.md](docs/correlation.md) | Ambient correlation IDs |
| [docs/logging.md](docs/logging.md) | Event schema + sinks |
| [docs/metrics.md](docs/metrics.md) | Latency, percentiles, error rates |
| [docs/error-classification.md](docs/error-classification.md) | Categories and rules |
| [docs/retry.md](docs/retry.md) | Backoff formula and tuning |
| [docs/timeout.md](docs/timeout.md) | Ceiling semantics |
| [docs/circuit-breaker.md](docs/circuit-breaker.md) | State machine |
| [docs/rate-limiter.md](docs/rate-limiter.md) | Four strategies, queue behavior |
| [docs/bulkhead.md](docs/bulkhead.md) | Concurrency cap, waiter queue |
| [docs/executor.md](docs/executor.md) | The pipeline entry point |
| [docs/http-integration.md](docs/http-integration.md) | DelegatingHandler setup |
| [docs/api-stability.md](docs/api-stability.md) | Frozen public API |
| [SPEC.md](SPEC.md) | Cross-language specification |

## Repository structure

    resilience/
    +-- README.md                     - you are here
    +-- SPEC.md                       - the cross-language contract
    +-- CHANGELOG.md                  - version history
    +-- VERSION                       - current version
    +-- LICENSE                       - MIT
    +-- docs/                         - per-concern documentation
        +-- README.md
        +-- correlation.md
        +-- logging.md
        +-- metrics.md
        +-- error-classification.md
        +-- retry.md
        +-- timeout.md
        +-- circuit-breaker.md
        +-- rate-limiter.md
        +-- bulkhead.md
        +-- executor.md
        +-- http-integration.md
        +-- api-stability.md
    +-- dotnet/
        +-- src/Portfolio.Resilience/         - the library source
        +-- tests/Portfolio.Resilience.Tests/ - 273 tests
        +-- Portfolio.Resilience.slnx

## Test suite

273 tests, 0 failures, 0 warnings. Run them with:

    cd dotnet
    dotnet test Portfolio.Resilience.slnx

Coverage spans every sink, the correlation primitive, the error classifier,
each policy builder (retry, timeout, circuit, rate limiter, bulkhead), the
composite pipeline, the registry, the executor, and the HTTP handler.

## Design principles

1. **One library, one pipeline, one shape.** No per-service retry
   implementations. No divergent failure semantics.

2. **Share the engine, own the interface.** The library shares mechanics
   (rate limiting, bulkhead, retry, circuit, timeout, correlation, event schema).
   Each service owns its HTTP response envelope. See [SPEC.md §11](SPEC.md).

3. **Ambient where it helps, injected where it matters.** Correlation IDs are
   ambient (`AsyncLocal`). The DI adapter exists for testability.

4. **Never crash the caller.** A broken log sink, a metric recorder that
   throws, a classifier that fails — none of these should take down the
   pipeline. Configuration errors, however, **do** fail loud: enabling a
   feature without wiring its builder throws `InvalidOperationException` on
   first call, not silent non-enforcement.

5. **Contracts before implementations.** The cross-language SPEC was written
   before the .NET implementation. A future Python implementation follows the
   same spec.

6. **Spec-first. Doc-first. Test-first.** No feature ships without all three.

## Roadmap

Priority is driven by (1) what users need most and (2) closing the feature gap
with Polly. Everything below is tracked in the repo's issues and planned in
this order.

### v0.6.0 — Current line ✅ Released

| Feature | Status |
|---------|--------|
| **Rate limiter** | ✅ Released — TokenBucket, SlidingWindow, FixedWindow, ConcurrencyLimit |
| **Bulkhead isolation** | ✅ Released — concurrency cap with bounded waiter queue |
| **Ambient correlation IDs** | ✅ Since v0.2.0 |
| **Structured logging** | ✅ Since v0.2.0 |
| **Latency metrics** | ✅ Since v0.2.0 |

### v0.7.0 — Composition and Observability

| Feature | Why it matters |
|---------|---------------|
| **Policy composition (Wrap)** | Build custom pipeline orders instead of the fixed chain. |
| **Hedging** | Parallel requests for latency-sensitive reads. |
| **OpenTelemetry integration** | Native OTel exporter for events and metrics. |
| **Roslyn analyzer** | Warn when `HttpClient.SendAsync` bypasses the wrapper. |

### v0.8.0 — Cloud sinks and dashboard

| Feature | Why it matters |
|---------|---------------|
| **Built-in `DatadogSink`** | Ship a first-party cloud sink. |
| **Built-in `OpenTelemetrySink`** | Ready-made OTel exporter. |
| **Built-in `ApplicationInsightsSink`** | Azure-native option. |
| **`AddStandardResilienceHandler()`** | One-liner for `HttpClientFactory` — matches Microsoft's helper. |

### v1.0.0 — API freeze

| Feature | Why it matters |
|---------|---------------|
| **API freeze** | Public API is locked. Semver guarantees apply. |
| **SPEC 1.0** | Cross-language contract finalized. |
| **Documentation website** | Full site at `sancy1.github.io/portfolio-resilience`. |
| **Performance benchmarks** | Throughput and latency under load, published in the README. |

### v1.x and beyond — Polyglot

| Feature | Why it matters |
|---------|---------------|
| **Python implementation** | `portfolio_resilience` on PyPI for FastAPI services. Same SPEC. |
| **Go implementation** | For the notification-service and future Go services. |
| **Chaos engineering hooks** | Inject faults for resilience testing (like Polly's Simmy). |
| **Additional languages** | Whatever the portfolio grows into. |

### What we intentionally exclude

- **A `Dashboard` UI** — the health endpoint is enough. UI is a separate concern.
- **A `RateLimit` middleware replacement** — ASP.NET Core has rate limiting built in; use it at the edge, use us for outbound calls.
- **Retries for non-idempotent operations** — enforced at the policy level, not automated.

See [CHANGELOG.md](CHANGELOG.md) for historical changes.

## Contributing

This is a personal project, but the library is open source. If you find a bug
or want a feature:

1. Open an issue at [github.com/sancy1/portfolio-resilience/issues](https://github.com/sancy1/portfolio-resilience/issues)
2. Or open a PR following the conventions:
   - One feature per PR
   - Tests for every change
   - Docs updated in the same PR
   - No `dotnet build` warnings

Every PR must pass `dotnet test` with 0 failures.

## License

MIT — see [LICENSE](LICENSE).

## Author

**Alexander Sanchez Cyril**

Built as part of the [alexander-portfolio-v2](https://github.com/sancy1/alexander-portfolio-v2)
microservice platform. The library was extracted when the second service
needed the same resilience primitives as the first.
