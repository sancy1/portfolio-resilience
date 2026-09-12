<!--
filepath: README.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Main project README — install, quick-start, feature overview, and links.
-->

# Portfolio.Resilience

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-237%20passing-brightgreen.svg)](#)
[![NuGet](https://img.shields.io/badge/nuget-Portfolio.Resilience-blue.svg)](https://www.nuget.org/packages/Portfolio.Resilience)

> **Resilience primitives for .NET.** Retry, circuit breaker, timeout, fallback,
> correlation, structured logging, and latency metrics — in one small library
> with a cross-language spec.

Built for microservice architectures where every HTTP call, database query, and
cache lookup can fail transiently. Instead of hand-rolling retry logic in each
service (differently, each time), call one method:

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
- The circuit between "the network was busy" and "the user saw an error" is lost.

With `Portfolio.Resilience`, every outbound call goes through a battle-tested
pipeline:

    Caller -> Retry -> Circuit Breaker -> Timeout -> Operation

Each layer does one thing:

| Layer | Responsibility | Docs |
|-------|---------------|------|
| **Retry** | Absorbs transient failures with exponential backoff + jitter | [retry.md](docs/retry.md) |
| **Circuit Breaker** | Fails fast when a dependency is genuinely broken | [circuit-breaker.md](docs/circuit-breaker.md) |
| **Timeout** | Bounds every attempt | [timeout.md](docs/timeout.md) |
| **Executor** | Coordinates the pipeline + fallback + observability | [executor.md](docs/executor.md) |

And two cross-cutting concerns:

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
        .AddPolicy("notification-service", p =>
        {
            p.Retry.MaxAttempts  = 5;
            p.Timeout.TimeoutMs  = 30000;
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

That is the **entire integration** for a call site. Retry, circuit breaker,
timeout, logging, metrics, and correlation all happen inside `ExecuteAsync`.

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
            +-- RetryPolicyBuilder         (outermost)
                  |
                  +-- CircuitPolicyBuilder (middle)
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

| Feature | Portfolio.Resilience | Polly | Microsoft.Extensions.Http.Resilience |
|---------|---------------------|-------|-------------------------------------|
| Retry + backoff + jitter | ✅ | ✅ | ✅ |
| Circuit breaker | ✅ | ✅ | ✅ |
| Timeout per attempt | ✅ | ✅ | ✅ |
| Fallback | ✅ | ✅ | ✅ |
| Correlation ID (ambient) | ✅ | manual | partial |
| Structured event schema | ✅ (cross-language) | manual | no |
| Latency percentiles built-in | ✅ | no | external (OTel) |
| Cross-language spec | ✅ | no | no |
| Zero external dependencies | ✅ | ✅ | no |
| .NET 10 target | ✅ | ✅ | ✅ |

**When to choose this library:** you want a single, opinionated, well-tested
pipeline with built-in observability and a cross-language spec. Especially if you
also have Python or Go services that need the same behavior.

**When not to choose this library:** you need maximum flexibility with
compositional policies, or you're already committed to OpenTelemetry for
observability and don't want a second metric system.
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
| [docs/executor.md](docs/executor.md) | The pipeline entry point |
| [docs/http-integration.md](docs/http-integration.md) | DelegatingHandler setup |
| [docs/api-stability.md](docs/api-stability.md) | Frozen public API |
| [SPEC.md](SPEC.md) | Cross-language specification |

## Repository structure

    resilience/
    ├── README.md                     ← you are here
    ├── SPEC.md                       ← the cross-language contract
    ├── CHANGELOG.md                  ← version history
    ├── VERSION                       ← current version
    ├── LICENSE                       ← MIT
    ├── docs/                         ← per-concern documentation
    │   ├── README.md
    │   ├── correlation.md
    │   ├── logging.md
    │   ├── metrics.md
    │   ├── error-classification.md
    │   ├── retry.md
    │   ├── timeout.md
    │   ├── circuit-breaker.md
    │   ├── executor.md
    │   ├── http-integration.md
    │   └── api-stability.md
    └── dotnet/
        ├── src/Portfolio.Resilience/     ← the library source
        ├── tests/Portfolio.Resilience.Tests/  ← 237 tests
        └── Portfolio.Resilience.slnx

## Test suite

237 tests, 0 failures, 0 warnings. Run them with:

    cd dotnet
    dotnet test Portfolio.Resilience.slnx

Coverage spans every sink, the correlation primitive, the error classifier,
each policy builder, the executor, and the HTTP handler.

## Design principles

1. **One library, one pipeline, one shape.** No per-service retry
   implementations. No divergent failure semantics.

2. **Share the engine, own the interface.** The library shares mechanics
   (retry, circuit, timeout, correlation, event schema). Each service owns its
   HTTP response envelope. See [SPEC.md §11](SPEC.md).

3. **Ambient where it helps, injected where it matters.** Correlation IDs are
   ambient (`AsyncLocal`). The DI adapter exists for testability.

4. **Never crash the caller.** A broken log sink, a metric recorder that
   throws, a classifier that fails — none of these should take down the
   pipeline.

5. **Contracts before implementations.** The cross-language SPEC was written
   before the .NET implementation. A future Python implementation follows the
   same spec.

6. **Spec-first. Doc-first. Test-first.** No feature ships without all three.

## Roadmap

| Version | Status | What it adds |
|---------|--------|--------------|
| `v0.5.0` | Current | First public release. All core features. |
| `v0.6.0` | Planned | Bulkhead isolation, adaptive retry |
| `v1.0.0` | Planned | API freeze, spec 1.0 |
| Python impl | Planned | `alexander_resilience` for FastAPI services |
| Go impl | Planned | When notification-service adopts it |

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
