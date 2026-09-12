<!--
filepath: CHANGELOG.md
package:  Portfolio.Resilience | since: v0.2.0
purpose:  Version history for the resilience library.
-->

# Changelog

All notable changes to `Portfolio.Resilience`. Format follows [Keep a Changelog](https://keepachangelog.com/),
versioning follows [Semantic Versioning](https://semver.org/).

## [0.5.1] - 2026-09-12

### Documentation

- **README.md: new "Logging scenarios" section** — five configurations for
  choosing where resilience events go:
  - Local-only (ConsoleLogSink or FileLogSink)
  - Cloud-only (custom ILogSink implementation)
  - Hybrid (local + cloud via CompositeLogSink)
  - Silent (NullLogSink — no configuration)
  - Includes a decision table mapping each scenario to the API call.
- **README.md: expanded "Comparison with other libraries"** — added side-by-side
  feature table with Polly and Microsoft.Extensions.Http.Resilience.
- **README.md: enriched "Roadmap" section** — versioned plan through v1.x.

### Internal

- **PLANNING.md** — new private planning document (not committed; see `.gitignore`).
- **.gitignore** — added private planning files.

### Compatibility

- **No code changes.** Binary-compatible with 0.5.0. All 237 tests pass.
## [0.5.0] - Stages F and G complete

### Added (Stage F - Executor & DI)

- `Implementation/ResilienceExecutor.cs` - the IResilienceExecutor entry point
  - Full pipeline: retry -> circuit -> timeout
  - Emits structured events (started, succeeded, failed, fallback used)
  - Records latency and in-flight counts
  - Correlation ID propagation from ambient context
  - Fallback support (per-call, not per-policy)
- `Configuration/ResilienceBuilder.cs` - fluent configuration API
- `Extensions/ServiceCollectionExtensions.cs` - `AddPortfolioResilience()` one-line registration
- `Extensions/ConfigurationExtensions.cs` - bind ResilienceOptions from IConfiguration
- `Middleware/ResilienceExceptionMiddlewareBase.cs` - abstract middleware for services to
  render their own error responses while sharing catch + correlation header logic
- 48 tests

### Added (Stage G - HTTP integration)

- `HttpClient/ResilientHttpMessageHandler.cs` - DelegatingHandler that routes every
  HttpClient request through the resilience pipeline
- `HttpClient/HttpRequestSnapshot.cs` - captures request for rebuilding across retries
  (HttpRequestMessage is single-use)
- `HttpClient/HttpClientBuilderExtensions.cs` - `AddResilientHandler("policy-name")`
  fluent API that attaches to any IHttpClientBuilder
- 14 tests

### Design decisions

**Response envelope ownership** - the middleware base is abstract. Each service
implements `RenderErrorAsync` to render errors in its own shape. Only the catch logic,
correlation header, and structured logging are shared. See SPEC.md §ResponseEnvelopeOwnership.

**Fallback is per-call, not per-policy** - a retry policy is reusable; a fallback value
is site-specific. The caller decides per call site whether a fallback makes sense.

**HTTP request cloning** - HttpRequestMessage is single-use. `HttpRequestSnapshot`
captures method, headers, URI, and body bytes so each retry attempt can build a fresh
request. Matches Microsoft.Extensions.Http.Resilience's approach.

**Extend, don't wrap** - `AddResilientHandler` extends `IHttpClientBuilder` rather than
providing a new `AddHttpClient` wrapper. Fits the existing .NET pattern.

### Fixed

- Removed unnecessary `Microsoft.Extensions.*` PackageReferences now provided by
  `FrameworkReference Microsoft.AspNetCore.App`. Build now has zero warnings.
## [0.3.0] - Stage D complete

### Added

- `Configuration/ErrorClassificationOptions.cs` - configurable classification rules
- `Errors/ErrorClassifier.cs` - maps exceptions to `ResilienceErrorCategory`
- `Policies/RetryPolicyBuilder.cs` - exponential backoff with jitter, classifier-gated
- `Policies/TimeoutPolicyBuilder.cs` - async timeout with typed `ResilienceException`
- `Policies/CircuitPolicyBuilder.cs` - circuit state machine, implements `ICircuitBreakerMonitor`
- `Policies/CompositePolicyBuilder.cs` - chains retry -> circuit -> timeout
- 62 new tests across 5 test files

### Pipeline order

    Caller -> Retry -> Circuit -> Timeout -> Operation

Retry outermost (each retry gets a fresh circuit check and timeout budget),
circuit middle (rejects early when Open), timeout innermost (bounds a single attempt).

### Fixed

- `InMemoryMetricSink` now has real `BeginInFlight` / `EndInFlight` methods
  (previously the field was never assigned).


## [0.2.0] — Stage C complete

### Added

- `Correlation/CorrelationContext.cs` — ambient `AsyncLocal` correlation primitive
- `Correlation/AsyncLocalCorrelationAccessor.cs` — DI adapter implementing `ICorrelationAccessor`
- `Sinks/ConsoleLogSink.cs` — compact JSON Lines to stdout
- `Sinks/FileLogSink.cs` — daily-rotating JSON Lines to file (`resilience-YYYY-MM-DD.jsonl`)
- `Sinks/NullLogSink.cs` — silent no-op sink
- `Sinks/CompositeLogSink.cs` — fan-out log sink with per-child exception isolation
- `Sinks/InMemoryMetricSink.cs` — bounded rolling window with p50/p95/p99 and error rate
- `Sinks/CompositeMetricSink.cs` — fan-out metric sink with per-child exception isolation
- 67 passing tests covering every sink, the correlation primitive, and the DI adapter

### Contract (Stage B, unchanged)

- `Abstractions/` — interfaces: `ILogSink`, `IMetricSink`, `ICorrelationAccessor`,
  `ICircuitBreakerMonitor`, `ILatencyTracker`, `IResilienceExecutor`, `IResiliencePolicyRegistry`
- `Configuration/` — options classes for retry, circuit, timeout, fallback, logging
- `Errors/` — `ResilienceException`, `ResilienceErrorCategory`
- `Events/` — `ResilienceEvent`, `ResilienceEventType`

### Documentation

- `docs/README.md` — index
- `docs/correlation.md` — correlation concern
- `docs/logging.md` — logging concern
- `docs/metrics.md` — metrics concern

### Not yet implemented (future stages)

- Stage D — error classification + policy builders
- Stage E — circuit monitor, latency tracker, event emitter, policy registry
- Stage F — the executor and DI extensions
- Stage G — HttpClient handler and exception middleware base
- Stage H — integration into landing-page-service, `/health/resilience` endpoint

## [0.1.0] — Initial scaffold (Stage A + Stage B)

### Added

- Solution skeleton
- Abstractions, configuration options, errors, and events contracts
