<!--
filepath: README.md
package:  Portfolio.Resilience | since: v0.8.0
purpose:  Main project README - install, quick-start, feature overview, and links.
-->

# Portfolio.Resilience

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-523%20passing-brightgreen.svg)](#)
[![NuGet](https://img.shields.io/badge/nuget-Portfolio.Resilience-blue.svg)](https://www.nuget.org/packages/Portfolio.Resilience)

> **Resilience primitives for .NET.** Rate limiter, bulkhead, hedging, retry,
> circuit breaker, timeout, fallback, policy composition, correlation,
> structured logging, and latency metrics - in one small library with a
> cross-language spec.

Built for microservice architectures where every HTTP call, database query, and
cache lookup can fail transiently - or overwhelm a dependency if it does not.
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
transiently - a DNS blip, a TCP reset, a briefly overloaded dependency, a slow
database. Without protection:

- Every transient failure becomes a user-visible error.
- A single slow dependency exhausts your thread pool.
- A burst of traffic slams a rate-limited dependency.
- A p99 latency spike wrecks an otherwise fast operation.
- The circuit between "the network was busy" and "the user saw an error" is lost.

With `Portfolio.Resilience`, every outbound call goes through a battle-tested
pipeline:

    Caller -> RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit Breaker -> Timeout -> Operation

Each layer does one thing:

| Layer | Responsibility | Docs |
|-------|---------------|------|
| **Rate Limiter** | Caps how many calls may proceed per time period | [rate-limiter.md](docs/rate-limiter.md) |
| **Bulkhead** | Caps how many calls may run concurrently | [bulkhead.md](docs/bulkhead.md) |
| **Hedging** | Fires parallel attempts; the first success wins | [hedging.md](docs/hedging.md) |
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

### What ships in the box

Every concern below is **built in, tested, and documented**. Optional add-ons
are marked explicitly.

| Concern | What it does | Lives in | Docs |
|---------|-------------|----------|------|
| **Rate Limiter** | Caps how many calls may proceed per time period. Four strategies: `TokenBucket`, `SlidingWindow`, `FixedWindow`, `ConcurrencyLimit`. | `RateLimiterPolicyBuilder`, `RateLimiterOptions` | [rate-limiter.md](docs/rate-limiter.md) |
| **Bulkhead** | Caps how many calls may run concurrently. Queues excess up to `QueueLimit`; times out after `QueueTimeoutMs`. | `BulkheadPolicyBuilder`, `BulkheadOptions` | [bulkhead.md](docs/bulkhead.md) |
| **Hedging** | Fires parallel attempts with a stagger delay; the first success wins. **Not safe for non-idempotent operations** - see docs. | `HedgingPolicyBuilder`, `HedgingOptions` | [hedging.md](docs/hedging.md) |
| **Retry** | Absorbs transient failures with exponential backoff + jitter. Classifier-gated: permanent errors fail immediately. | `RetryPolicyBuilder`, `RetryOptions` | [retry.md](docs/retry.md) |
| **Circuit Breaker** | Fails fast when a dependency is genuinely broken. Three-state machine (`Closed` / `Open` / `HalfOpen`) with automatic recovery. | `CircuitPolicyBuilder`, `CircuitOptions` | [circuit-breaker.md](docs/circuit-breaker.md) |
| **Timeout** | Bounds every attempt. Distinguishes caller cancellation from our own ceiling. | `TimeoutPolicyBuilder`, `TimeoutOptions` | [timeout.md](docs/timeout.md) |
| **Fallback** | Returns a degraded response when the pipeline fails. Per-call, not per-policy. | Per-call argument to `IResilienceExecutor.ExecuteAsync` | [executor.md](docs/executor.md) |
| **Policy Composition** | Build a custom pipeline order from any subset of layers. Both direct (`ResiliencePipeline.Wrap`) and fluent (`ResiliencePipelineBuilder`) styles. | `IResiliencePolicy`, `ResiliencePipeline`, `ResiliencePipelineBuilder` | [composition.md](docs/composition.md) |
| **Executor** | Coordinates the whole pipeline plus events, metrics, correlation. | `ResilienceExecutor`, `IResilienceExecutor` | [executor.md](docs/executor.md) |
| **Structured Logging** | Emits one JSON event at every pipeline decision. Sink-swappable: `ConsoleLogSink`, `FileLogSink`, `NullLogSink`, `CompositeLogSink`. | `ILogSink`, `ResilienceEventEmitter`, `ResilienceEvent` | [logging.md](docs/logging.md) |
| **Metrics** | p50/p95/p99 latency, error rate, average, in-flight counts per policy. No OpenTelemetry required. | `IMetricSink`, `InMemoryMetricSink`, `ILatencyTracker` | [metrics.md](docs/metrics.md) |
| **Correlation IDs** | Ambient correlation ID attached to every event, exception, and metric. Propagates via `X-Correlation-Id` across services. | `CorrelationContext` (`AsyncLocal`), `AsyncLocalCorrelationAccessor` | [correlation.md](docs/correlation.md) |
| **Error Classification** | Maps any exception to one of six categories: `Transient`, `Permanent`, `CircuitOpen`, `Timeout`, `FallbackUsed`, `Unknown`. Configurable HTTP codes, SQLSTATEs, exception types. | `ErrorClassifier`, `ErrorClassificationOptions` | [error-classification.md](docs/error-classification.md) |
| **HTTP Integration** | Every `HttpClient` request runs through the pipeline. One-line fluent API. | `ResilientHttpMessageHandler`, `AddResilientHandler()` | [http-integration.md](docs/http-integration.md) |
| **Standard Handler** (opt-in) | One-liner for a safe default policy: `AddStandardResilienceHandler()`. Retry + circuit + timeout; rate limiter, bulkhead, and hedging are off by default. | `HttpClientBuilderExtensions`, `StandardPolicy` | [http-integration.md](docs/http-integration.md) |
| **OpenTelemetry Export** (opt-in, separate package) | Every event becomes an OTel log record; every call becomes an OTel histogram and counter. | `OpenTelemetryLogSink`, `OpenTelemetryMetricSink` | [opentelemetry.md](docs/opentelemetry.md) |
| **Roslyn Analyzers** (opt-in, separate package) | Compile-time warnings for common resilience mistakes: `PR0001` (HttpClient bypass) and `PR0002` (policy misconfiguration). | `HttpClientBypassAnalyzer`, `MisconfigurationAnalyzer` | [analyzers.md](docs/analyzers.md) |
| **Middleware Base** | Catch `ResilienceException` at the service boundary. Render errors in your own service's response shape - the library shares the mechanism, you own the envelope. | `ResilienceExceptionMiddlewareBase` | [executor.md](docs/executor.md) |
| **DI + Config** | One-line registration. Bind policies from `appsettings.json` or environment variables. | `AddPortfolioResilience()`, `LoadFromConfiguration()` | [executor.md](docs/executor.md) |
| **Startup Validation** | Misconfigured policies log a clear warning at first resolve. The library never crashes the caller. | `ResiliencePolicyRegistry` + `Validate()` methods | [rate-limiter.md](docs/rate-limiter.md), [bulkhead.md](docs/bulkhead.md), [hedging.md](docs/hedging.md) |

**Observability is not bolted on.** Every layer in the pipeline above emits
structured events via `ILogSink` and records latency via `IMetricSink`. Every
event carries a `correlation_id`. The two side channels run parallel to the
execution pipeline - no configuration required to get events or metrics; they
work out of the box with sensible defaults.

---

## How we compare with Polly

Polly is the .NET standard for resilience - mature, widely adopted, and the
base for Microsoft's own `Microsoft.Extensions.Resilience`. Portfolio.Resilience
is not a Polly replacement; it has a different design center. This table
summarizes where the two libraries overlap and where each has an edge.

| Feature | Portfolio.Resilience | Polly | MS.Extensions.Http.Resilience |
|---------|---------------------|-------|-------------------------------|
| Retry + backoff + jitter | ✅ | ✅ | ✅ |
| Circuit breaker | ✅ | ✅ | ✅ |
| Timeout (per-attempt) | ✅ | ✅ | ✅ |
| Fallback | ✅ | ✅ | ✅ |
| Rate limiter (4 strategies) | ✅ | ✅ | ✅ |
| Bulkhead | ✅ | ✅ | ✅ |
| Hedging | ✅ | ✅ | ✅ |
| Idempotency key propagation | ✅ | ❌ | ❌ |
| PCI-safe event scrubbing | ✅ | ❌ | ❌ |
| Timeout budget propagation | ✅ | ❌ | ❌ |
| Payment-safe pipeline preset | ✅ | ❌ | ❌ |
| Policy composition (Wrap) | ✅ | ✅ | ✅ |
| OpenTelemetry integration | ✅ | ✅ | ✅ |
| Compile-time analyzers | ✅ | ❌ | ✅ |
| `AddStandardResilienceHandler()` | ✅ | ❌ | ✅ |
| Chaos engineering | 🔜 v1.x | ✅ | ❌ |
| Cloud sinks | ecosystem | ecosystem | ecosystem |
| Ambient correlation IDs | ✅ built-in | manual | partial |
| Cross-language SPEC | ✅ | ❌ | ❌ |
| Latency percentiles without OTel | ✅ | OTel only | OTel only |
| Zero core dependencies | ✅ | ✅ | ❌ (Polly) |

### What we bring that Polly does not

Four v0.8.0 features address the specific needs of any service handling
non-idempotent writes, sensitive data, or strict SLAs:

- **Idempotency key propagation** - every retry attempt carries the same
  key, so a hedged or retried write reaches the downstream provider as one
  logical operation. Polly leaves this to the caller.
- **PCI-safe event scrubbing** - one flag turns on PAN/CVV/SSN masking
  before any log sink sees an event. Polly has no equivalent.
- **Timeout budget propagation** - a caller expresses "this whole call must
  fit in 3 seconds" and every layer caps itself to that budget. Polly's
  per-attempt timeouts compose without a total ceiling.
- **Payment-safe pipeline preset** - a named factory returns a fixed safe
  layer order so a critical write never races. Polly requires careful manual
  composition.

Three structural properties also remain differentiators:

1. **Correlation IDs are built in.** Every event, exception, and metric
   carries a `correlation_id` automatically. One HTTP header
   (`X-Correlation-Id`) propagates across services. With Polly you wire this
   yourself.

2. **Structured events have a cross-language contract.** `SPEC.md` defines
   every event name (`retry_attempted`, `circuit_opened`, `rate_limited`,
   `hedge_won`, etc.), every field name (`policy_name`, `attempt`,
   `duration_ms`), every metric name (`p50_ms`, `error_rate`), and every
   error category. A future Python or Go implementation produces identical
   output, so dashboards work across languages.

3. **Observability is included, not deferred.** p50/p95/p99 latency, error
   rates, and in-flight counts are computed by the library. No OpenTelemetry
   setup required. If you already use OTel, our optional package feeds it.

### Where Polly still leads

- **Chaos engineering.** Polly's Simmy injects faults for resilience testing.
  This library has explicitly deferred that to v1.x. If you need to inject
  chaos today, Polly is the only option among the three.
- **Ecosystem maturity.** Polly has 200M+ downloads, .NET Foundation
  membership, and a broad plugin ecosystem. Portfolio.Resilience is new and
  has no plugin marketplace. It has a smaller surface, a single maintainer,
  and a simpler dependency story.

### Cloud sinks

Every backend - Datadog, Application Insights, Sentry, Honeycomb, Grafana,
New Relic - is reachable today via `ILogSink` plus the optional
`Portfolio.Resilience.OpenTelemetry` package. The library does not ship
per-vendor sinks; the OTel package is the universal adapter, and a custom
sink is ~30 lines. This keeps the core zero-dependency. See
[docs/logging.md](docs/logging.md) for the pattern.

### When to choose Polly / MS.Extensions.Resilience

- You want the industry standard.
- You need chaos engineering **today**.
- You're already invested in the Polly / OpenTelemetry ecosystem.
- You want the widest ecosystem of plugins and community support.

### When to choose Portfolio.Resilience

- You want **correlation IDs and structured observability built in** rather
  than assembled from separate pieces.
- You have **multiple services in different languages** and want a
  consistent event schema and error taxonomy.
- You want a **small, dependency-free core library** with no transitive
  package chain.
- You want compile-time analyzers that catch resilience mistakes before
  they ship.
- You need **idempotency, PCI scrubbing, or total time budgets** - the four
  v0.8.0 features Polly does not offer.
- You want to learn from or extend a well-documented, spec-driven codebase.

### Can I use both?

Yes. They operate at different layers. Some teams use Polly for the
fine-grained HTTP client pipeline and Portfolio.Resilience for the
higher-level operation pipeline (database calls, Redis, cross-service
business operations). Neither library knows or cares about the other.

---

## Install

Three packages, install only what you need:

    # Core library - required
    dotnet add package Portfolio.Resilience

    # Optional: OpenTelemetry export
    dotnet add package Portfolio.Resilience.OpenTelemetry

    # Optional: compile-time analyzers
    dotnet add package Portfolio.Resilience.Analyzers

Requires **.NET 10** or later. The core package has **no third-party
dependencies** (only the ASP.NET Core framework reference).

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
isolation, hedging (if enabled), retry, circuit breaking, timeout, logging,
metrics, and correlation all happen inside `ExecuteAsync`.

### Optional: the one-liner for HTTP clients

If you do not want to define a policy by hand and just want sensible defaults
on an `HttpClient`, use the standard handler:

    builder.Services
        .AddHttpClient("auth-service", c =>
            c.BaseAddress = new Uri(config["AUTH_SERVICE_URL"]!))
        .AddStandardResilienceHandler();

This registers a policy named `"standard"` with retry + circuit + timeout, and
routes every request through it. Override individual settings inline:

    .AddStandardResilienceHandler(p =>
    {
        p.Retry.MaxAttempts = 5;
        p.Timeout.TimeoutMs = 10_000;
    });

See [http-integration.md](docs/http-integration.md) for details.

### Optional: custom pipeline composition

For a custom layer order, use `ResiliencePipeline`:

    var pipeline = ResiliencePipeline.Wrap(
        rateLimiter, bulkhead, retry, circuit, timeout);

    await pipeline.ExecuteAsync("external-api", operation, definition);

Or the fluent builder for conditional layers:

    var pipeline = new ResiliencePipelineBuilder()
        .WithName("external-api-pipeline")
        .Add(rateLimiter)
        .AddIf(env.IsProduction(), retry)
        .Add(circuit)
        .Add(timeout)
        .Build();

See [composition.md](docs/composition.md) for details.

### Logging scenarios

Logging is **optional and composable**. Every pipeline decision emits a
structured JSON event. Whether those events go to a file, the console, a cloud
provider, or nowhere at all is your choice at registration time.

**Scenario 1 - Local only (typical for development or containers with stdout collection):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())    // stdout - captured by Docker/K8s
        .AddPolicy("auth-service", p => { /* ... */ }));

**Scenario 2 - Local file (for hosts without stdout collection, or a local audit trail):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new FileLogSink("/var/log/resilience"))   // daily rotating JSON Lines
        .AddPolicy("auth-service", p => { /* ... */ }));

**Scenario 3 - Cloud only via OpenTelemetry (fully managed observability):**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p => { /* ... */ }));

    builder.Services.AddPortfolioResilienceOpenTelemetry();

**Scenario 4 - Hybrid (local + cloud, belt-and-suspenders):**

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new FileLogSink("/var/log/resilience"))    // local backup
        .AddPolicy("auth-service", p => { /* ... */ }));

    builder.Services.AddPortfolioResilienceOpenTelemetry();     // cloud via OTel

**Scenario 5 - Silent (nothing logged):**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p => { /* ... */ }));
    // No AddLogSink call - NullLogSink (silent default)

**How the choice is made:**

| Scenario | What you call | Where events land |
|----------|---------------|-------------------|
| Local only | `AddLogSink(ConsoleLogSink)` or `AddLogSink(FileLogSink)` | Local |
| Cloud only | `AddPortfolioResilienceOpenTelemetry()` | Cloud |
| **Hybrid** | **Both `AddLogSink(...)` and `AddPortfolioResilienceOpenTelemetry()`** | **Both** |
| Silent | *(no sink registered)* | Nowhere |

**`AddLogSink` composes.** Every call adds another destination. Multiple calls
= multiple sinks, automatically wired through `CompositeLogSink`. A broken cloud
sink never blocks the local one - a per-sink exception policy isolates them.

See [docs/logging.md](docs/logging.md) for the full event schema, sample cloud
sink implementations, and migration patterns. See
[docs/opentelemetry.md](docs/opentelemetry.md) for the OTel package.

## HttpClient integration

If you prefer the handler pattern - every `HttpClient` request through a policy:

    builder.Services
        .AddHttpClient("auth-service", c =>
        {
            c.BaseAddress = new Uri(config["AUTH_SERVICE_URL"]!);
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddResilientHandler("auth-service");

Then use `HttpClient` normally. Every request goes through the pipeline for the
`auth-service` policy.

For a one-liner with safe defaults instead of a named policy:

    builder.Services
        .AddHttpClient("auth-service")
        .AddStandardResilienceHandler();

See [http-integration.md](docs/http-integration.md) for the full story.

### Roslyn analyzers (optional)

Install `Portfolio.Resilience.Analyzers` to get compile-time warnings when
code bypasses the pipeline, or when a policy is misconfigured:

    dotnet add package Portfolio.Resilience.Analyzers

Two rules ship:

- **PR0001** - an `HttpClient` obtained from `IHttpClientFactory` is called
  directly, without going through the resilience pipeline.
- **PR0002** - a policy enables a feature (rate limiter, bulkhead, hedging)
  but sets a companion value to `0` or negative.

The analyzers run in your IDE and in `dotnet build`. They never affect runtime
behavior. See [analyzers.md](docs/analyzers.md) for configuration and
suppression options.

---

## Features

### Rate limiter

Caps how many calls may proceed in a given time period. Four strategies:

    TokenBucket    - smooth refill, allows bursts up to capacity
    SlidingWindow  - precise; no boundary effects (default)
    FixedWindow    - cheapest; allows 2x burst at boundaries
    ConcurrencyLimit - caps simultaneous calls instead of rate

Rejections are fast and local - the dependency never sees the request. Each
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

### Hedging

Fires parallel attempts of the same operation with a stagger delay between
them, and returns the first success. A latency optimization, not a reliability
one - use retry for transient failures.

    Caller -> Primary starts immediately
                |
                +-- if still running after DelayMs -> fire hedge 1
                      |
                      +-- if still running after DelayMs -> fire hedge 2
                            |
                            +-- first success wins; losers cancelled

**Not safe for non-idempotent operations.** A hedged `POST /charge` can create
two charges if the server does not deduplicate. Use hedging only on idempotent
reads, or on writes with an idempotency key. The library logs a warning at
startup when a policy enables hedging.

Three event types are emitted per race: `hedge_won`, `hedge_lost`, and
`hedge_cancelled`.

### Retry

Exponential backoff with jitter. Every attempt is bounded, and every delay is
formula-driven:

    delay(n) = min(base_delay * 2^(n-1), max_delay) + jitter(0, ratio * base_delay)

Only `Transient` errors are retried. Permanent errors (400 Bad Request,
`ArgumentException`) fail immediately - no wasted retries.

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
retry sequence. Distinguishes **user cancellation** from **our timeout** - a
user closing a browser tab does not trigger retries or count against the circuit.

### Fallback

Optional per-call fallback. When the pipeline fails, the fallback runs outside
the pipeline and produces a degraded response. Falls back to a clear
`ResilienceException` if no fallback is provided.

### Policy composition

Build a pipeline in any order, from any subset of the available layers. Two
entry points, both equally valid:

    // Direct - one line for a static shape
    var pipeline = ResiliencePipeline.Wrap(rateLimiter, retry, circuit, timeout);

    // Fluent - for configuration-driven shapes
    var pipeline = new ResiliencePipelineBuilder()
        .WithName("external-api")
        .Add(rateLimiter)
        .AddIf(isProduction, retry)
        .Add(circuit)
        .Add(timeout)
        .Build();

Every builder implements `IResiliencePolicy`, so custom layers work too. The
library's default pipeline (`CompositePolicyBuilder`) uses composition
internally; user-defined pipelines use the same primitives.

### Standard handler

One-line `HttpClient` resilience with a safe default policy:

    builder.Services
        .AddHttpClient("auth-service")
        .AddStandardResilienceHandler();

The built-in `"standard"` policy enables retry, circuit, and timeout. Rate
limiter, bulkhead, and hedging are off by default - they multiply load or
duplicate requests and must be opted into explicitly. If you register your own
policy named `"standard"`, yours wins.

### OpenTelemetry export (optional package)

Install `Portfolio.Resilience.OpenTelemetry` to export every event as an OTel
log record and every call as an OTel histogram and counter:

    dotnet add package Portfolio.Resilience.OpenTelemetry

One-line registration:

    builder.Services.AddPortfolioResilienceOpenTelemetry();

Events become OTel logs with `resilience.*` structured attributes. Metrics
become:

- `resilience.call.duration_ms` - histogram tagged with `policy_name`, `success`
- `resilience.call.succeeded_total` - counter tagged with `policy_name`, `attempts`
- `resilience.call.failed_total` - counter tagged with `policy_name`, `attempts`

The core library remains zero-dependency. See
[opentelemetry.md](docs/opentelemetry.md).

### Structured logging

Every pipeline decision emits a `ResilienceEvent`. The default sink is silent;
opt in to `ConsoleLogSink`, `FileLogSink`, or a custom `ILogSink`:

    {"event_type":"retry_attempted","policy_name":"auth-service",
     "correlation_id":"abc-123","attempt":2,"metadata":{"delay_ms":120}}

Cross-language JSON schema is defined in [SPEC.md section 7](SPEC.md).

### Metrics

p50, p95, p99, error rate, in-flight counts - per policy. Exposed via
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
One user request -> one trace ID -> every log line and metric is linked.

### Error classification

Every failure is classified into one of six categories:
`Transient`, `Permanent`, `CircuitOpen`, `Timeout`, `FallbackUsed`, `Unknown`.
The category drives retry and circuit decisions - and gives you a
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
                        +-- HedgingPolicyBuilder
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

The pipeline shape is **configurable per policy**. A policy that does not
enable hedging skips that layer. A policy that disables retry runs the
remaining layers once.

Under the hood, the pipeline is composed via `IResiliencePolicy`. Users who
want a custom order build one with `ResiliencePipeline.Wrap(...)` or
`ResiliencePipelineBuilder` - see [composition.md](docs/composition.md).

Two side channels:

- **ILogSink** - receives structured events at each decision point
- **IMetricSink** - receives latency/outcome samples; feeds ILatencyTracker

Everything is swappable. `AddPortfolioResilience` wires sensible defaults; each
piece can be replaced.

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
| [docs/composition.md](docs/composition.md) | Custom pipeline order via Wrap or Builder |
| [docs/hedging.md](docs/hedging.md) | Parallel attempts, safety limits |
| [docs/opentelemetry.md](docs/opentelemetry.md) | Optional OTel log + metric export |
| [docs/analyzers.md](docs/analyzers.md) | Optional Roslyn analyzers |
| [docs/executor.md](docs/executor.md) | The pipeline entry point |
| [docs/http-integration.md](docs/http-integration.md) | DelegatingHandler setup |
| [docs/api-stability.md](docs/api-stability.md) | Frozen public API |
| [SPEC.md](SPEC.md) | Cross-language specification |

Every doc includes a **"How to use it" walkthrough** with step-by-step code
examples - not just API reference.

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
        +-- composition.md
        +-- hedging.md
        +-- opentelemetry.md
        +-- analyzers.md
        +-- executor.md
        +-- http-integration.md
        +-- api-stability.md
    +-- dotnet/
        +-- src/
        |   +-- Portfolio.Resilience/              - the core library
        |   +-- Portfolio.Resilience.OpenTelemetry/ - OTel export (optional)
        |   +-- Portfolio.Resilience.Analyzers/    - Roslyn analyzers (optional)
        +-- tests/
        |   +-- Portfolio.Resilience.Tests/               - 468 core tests
        |   +-- Portfolio.Resilience.OpenTelemetry.Tests/ - 37 OTel tests
        |   +-- Portfolio.Resilience.Analyzers.Tests/     - 18 analyzer tests
        +-- Portfolio.Resilience.slnx

## Test suite

**523 tests, 0 failures, 0 warnings.** Run them with:

    cd dotnet
    dotnet test Portfolio.Resilience.slnx

Coverage spans every sink, the correlation primitive, the error classifier,
each policy builder (retry, timeout, circuit, rate limiter, bulkhead, hedging),
the composite pipeline, the composition API, the registry, the executor, the
HTTP handler, the standard handler, the OpenTelemetry sinks, the Roslyn
analyzers, and the four v0.8.0 features (idempotency key propagation,
PCI-safe event scrubbing, timeout budget propagation, and the payment-safe
pipeline preset).

## Design principles

1. **One library, one pipeline, one shape.** No per-service retry
   implementations. No divergent failure semantics.

2. **Share the engine, own the interface.** The library shares mechanics
   (rate limiting, bulkhead, hedging, retry, circuit, timeout, correlation,
   event schema). Each service owns its HTTP response envelope. See
   [SPEC.md section 11](SPEC.md).

3. **Ambient where it helps, injected where it matters.** Correlation IDs are
   ambient (`AsyncLocal`). The DI adapter exists for testability.

4. **Never crash the caller.** A broken log sink, a metric recorder that
   throws, a classifier that fails - none of these should take down the
   pipeline. Configuration errors, however, **do** fail loud: enabling a
   feature without wiring its builder throws `InvalidOperationException` on
   first call, not silent non-enforcement.

5. **Contracts before implementations.** The cross-language SPEC was written
   before the .NET implementation. A future Python implementation follows the
   same spec.

6. **Spec-first. Doc-first. Test-first.** No feature ships without all three.

7. **Docs ship with code.** Every feature has a doc. Every doc has a
   walkthrough. No stale references, no duplicated sections, no "coming soon"
   markers left behind.

## Roadmap

Priority is driven by (1) what users need most and (2) closing the feature gap
with Polly. Everything below is tracked in the repo's issues and planned in
this order.

### v0.8.0 - Current line [released 2026-09-14]

| Feature | Status |
|---------|--------|
| **Idempotency key propagation** | Released - every retry and hedged attempt carries the same key; `Idempotency-Key` header emitted by the HTTP handler |
| **PCI-safe event scrubbing** | Released - `IEventScrubber` + `DefaultPciScrubber`; opt-in via `LoggingOptions.ScrubSensitiveData` |
| **Timeout budget propagation** | Released - `timeBudgetMs` on `ExecuteAsync`; retry, timeout, and hedging layers cap themselves to the remaining budget |
| **Payment-safe pipeline preset** | Released - `ResiliencePipeline.WithPaymentSafeDefaults()` enforces the safe layer order for critical writes |
### v0.7.0 - Previous line [released 2026-09-14]

| Feature | Status |
|---------|--------|
| **Policy composition (Wrap)** | [ok] Released - `ResiliencePipeline`, `ResiliencePipelineBuilder`, `IResiliencePolicy` |
| **Hedging** | [ok] Released - parallel attempts with stagger, per-attempt timeout, safety warning |
| **OpenTelemetry export** | [ok] Released - optional package `Portfolio.Resilience.OpenTelemetry` |
| **Roslyn analyzers** | [ok] Released - optional package `Portfolio.Resilience.Analyzers` (PR0001, PR0002) |
| **`AddStandardResilienceHandler()`** | [ok] Released - one-liner for safe HTTP defaults |
| **Rate limiter** | [ok] Since v0.6.0 - TokenBucket, SlidingWindow, FixedWindow, ConcurrencyLimit |
| **Bulkhead isolation** | [ok] Since v0.6.0 - concurrency cap with bounded waiter queue |
| **Retry + circuit + timeout + fallback** | [ok] Since v0.5.0 |
| **Correlation + logging + metrics** | [ok] Since v0.2.0-v0.5.0 |

### v1.0.0 - API freeze

| Feature | Why it matters |
|---------|---------------|
| **API freeze** | Public API is locked. Semver guarantees apply. |
| **SPEC 1.0** | Cross-language contract finalized. |
| **Documentation website** | Full site at `sancy1.github.io/portfolio-resilience`. |
| **Performance benchmarks** | Throughput and latency under load, published in the README. |

### v1.x and beyond - Polyglot

| Feature | Why it matters |
|---------|---------------|
| **Python implementation** | `portfolio_resilience` on PyPI for FastAPI services. Same SPEC. |
| **Go implementation** | For the notification-service and future Go services. |
| **Chaos engineering hooks** | Inject faults for resilience testing (like Polly's Simmy). |
| **Distributed circuit state** | Redis-backed `ICircuitBreakerMonitor` for cross-replica coordination. |

### What we intentionally exclude

- **A Dashboard UI** - the health endpoint is enough. UI is a separate concern.
- **A `RateLimit` middleware replacement** - ASP.NET Core has rate limiting built in; use it at the edge, use us for outbound calls.
- **Retries for non-idempotent operations without an idempotency key** - enforced by documentation and startup warnings, not by runtime blocking.
- **First-party cloud sinks** - the OTel package is the universal adapter; a custom sink is ~30 lines. See the comparison table above.

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

MIT - see [LICENSE](LICENSE).

## Author

**Alexander Sanchez Cyril**

Built as part of the [alexander-portfolio-v2](https://github.com/sancy1/alexander-portfolio-v2)
microservice platform. The library was extracted when the second service
needed the same resilience primitives as the first.