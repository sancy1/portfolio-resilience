<!--
filepath: docs/api-stability.md
package:  Portfolio.Resilience | since: v0.7.0
purpose:  The frozen public API surface. Changes to this document require a version bump.
-->

# API Stability

## What this document declares

The following types, interfaces, and methods constitute the **public API** of
the `Portfolio.Resilience` family of packages. They are the contract between
the library and every consumer.

**Rule:** any change to this surface is a **breaking change** and requires a
semantic version bump:

- **Additive** (new types, new optional parameters) — minor bump (`0.7.x` → `0.8.0`)
- **Removal, rename, signature change** — major bump (`0.x` → `1.0`, `1.x` → `2.0`)

## Packages

Three NuGet packages form the family:

| Package | Purpose | Dependencies |
|---------|---------|--------------|
| `Portfolio.Resilience` | Core library: retry, circuit, timeout, fallback, rate limiter, bulkhead, hedging, composition, correlation, logging, metrics | None (uses ASP.NET Core framework reference) |
| `Portfolio.Resilience.OpenTelemetry` | Optional. Exports events as OTel logs and metrics as OTel histograms and counters | `OpenTelemetry.Api`, `Microsoft.Extensions.Logging.Abstractions` |
| `Portfolio.Resilience.Analyzers` | Optional. Roslyn analyzers (PR0001, PR0002) that warn about common mistakes at compile time | `Microsoft.CodeAnalysis.CSharp` (analyzer SDK only) |

## Stability tier

**Every type listed here is stable as of v0.7.0.** There are no experimental or
deprecated APIs in this release.

## Public surface area at a glance

### Core package — `Portfolio.Resilience`

| Folder | Types | Purpose |
|--------|-------|---------|
| `Abstractions/` | 10 | Interfaces and snapshot types |
| `Configuration/` | 14 | Options, strategy enum, and builder |
| `Correlation/` | 2 | Ambient correlation |
| `Errors/` | 3 | Exception, category, classifier |
| `Events/` | 2 | Event record and enum |
| `Extensions/` | 2 | DI and configuration extension methods |
| `HttpClient/` | 2 | HTTP handler and fluent API |
| `Implementation/` | 5 | Concrete services |
| `Middleware/` | 1 | Abstract exception-handling base |
| `Policies/` | 9 | Retry, timeout, circuit, rate limiter, bulkhead, hedging, composite, pipeline |
| `Sinks/` | 6 | Log and metric sinks |

**Core total: 56 public types.**

*(Note: `LatencySnapshot` shares a file with `ILatencyTracker`; `CircuitSnapshot`
and `CircuitState` share a file with `ICircuitBreakerMonitor`. Counted once each.)*

### OpenTelemetry package — `Portfolio.Resilience.OpenTelemetry`

| Folder | Types | Purpose |
|--------|-------|---------|
| `OpenTelemetry/` | 3 | Sinks and DI extensions for OTel |

**OpenTelemetry total: 3 public types.**

### Analyzers package — `Portfolio.Resilience.Analyzers`

| Folder | Types | Purpose |
|--------|-------|---------|
| `Analyzers/` | 2 | Roslyn diagnostic analyzers |

**Analyzers total: 2 public types.**

**Grand total across all three packages: 61 public types.**

---

## Abstractions

Interfaces and read-side snapshot types. Safe to depend on; do not implement
unless you are replacing an implementation.

### `Portfolio.Resilience.Abstractions`

| Type | Kind | Notes |
|------|------|-------|
| `ILogSink` | interface | `void Emit(ResilienceEvent)` |
| `IMetricSink` | interface | `void RecordCall(string, TimeSpan, bool, int)` |
| `ICorrelationAccessor` | interface | `string? Current`, `IDisposable Push(string)` |
| `ICircuitBreakerMonitor` | interface | `Snapshot()`, `Get(string)` |
| `ILatencyTracker` | interface | `Snapshot()`, `Get(string)` |
| `IResilienceExecutor` | interface | The main entry point |
| `IResiliencePolicyRegistry` | interface | `Resolve(string)`, `KnownPolicies` |
| `IResiliencePolicy` | interface | A single pipeline layer. Implemented by every builder; user-implementable for custom layers |
| `CircuitSnapshot` | record | `PolicyName`, `State`, `ConsecutiveFailures`, `OpenedAtUtc`, `NextProbeAtUtc` |
| `CircuitState` | enum | `Closed`, `Open`, `HalfOpen` |
| `LatencySnapshot` | record | `PolicyName`, `TotalCalls`, `FailedCalls`, `ErrorRate`, `P50Ms`, `P95Ms`, `P99Ms`, `AvgMs`, `InFlight` |

*(Note: `CircuitSnapshot` and `CircuitState` are declared in `ICircuitBreakerMonitor.cs`.
`LatencySnapshot` is declared in `ILatencyTracker.cs`.)*

## Configuration

All option classes are plain POCOs. Bind from `appsettings.json`, environment
variables, or construct in code.

### `Portfolio.Resilience.Configuration`

| Type | Kind | Notes |
|------|------|-------|
| `ResilienceOptions` | class | Root options: `Policies` dict + `DefaultPolicy` |
| `PolicyDefinition` | class | A single policy: `Retry`, `Circuit`, `Timeout`, `Fallback`, `Logging`, `RateLimiter`, `Bulkhead`, `Hedging` |
| `RetryOptions` | class | `MaxAttempts`, `BaseDelayMs`, `MaxDelayMs`, `JitterRatio`, `RetryOnPermanent` |
| `CircuitOptions` | class | `FailureThreshold`, `OpenDurationSeconds`, `SuccessThreshold`, `OnlyCountTransient` |
| `TimeoutOptions` | class | `TimeoutMs` |
| `FallbackOptions` | class | `Enabled`, `Reason` |
| `LoggingOptions` | class | Nine per-event-type booleans (includes `EmitHedgeEvents`) |
| `ErrorClassificationOptions` | class | Configurable lists + `TreatCancellationAsPermanent` |
| `RateLimitStrategy` | enum | `TokenBucket`, `SlidingWindow`, `FixedWindow`, `ConcurrencyLimit` |
| `RateLimiterOptions` | class | `Enabled`, `Strategy`, `PermitLimit`, `WindowSeconds`, `QueueLimit`, `QueueTimeoutMs`, `RejectionCategory`, `Validate(string)` |
| `BulkheadOptions` | class | `Enabled`, `MaxConcurrency`, `MaxQueue`, `QueueTimeoutMs`, `RejectionCategory`, `Validate(string)` |
| `HedgingOptions` | class | `Enabled`, `MaxAttempts`, `DelayMs`, `ExponentialBackoff`, `AttemptTimeoutMs`, `CancelOnSuccess`, `EmitAttemptEvents`, `RejectionCategory`, `Validate(string)` |
| `StandardPolicy` | static class | `Name` constant and `Create()` factory for the built-in `"standard"` policy |
| `ResilienceBuilder` | class | Fluent configuration API used by `AddPortfolioResilience` |

## Correlation

### `Portfolio.Resilience.Correlation`

| Type | Kind | Notes |
|------|------|-------|
| `CorrelationContext` | static class | `CurrentId`, `Push(string)`, `NewId()` |
| `AsyncLocalCorrelationAccessor` | sealed class | Implements `ICorrelationAccessor` via `CorrelationContext` |

## Errors

### `Portfolio.Resilience.Errors`

| Type | Kind | Notes |
|------|------|-------|
| `ResilienceException` | sealed class | The wrapper exception. Carries `Category`, `PolicyName`, `AttemptsMade`, `TotalDuration`, `CorrelationId`, `Metadata`, `InnerException` |
| `ResilienceErrorCategory` | enum | `Unknown`, `Transient`, `Permanent`, `CircuitOpen`, `Timeout`, `FallbackUsed` |
| `ErrorClassifier` | sealed class | `Classify(Exception)` → category |

## Events

### `Portfolio.Resilience.Events`

| Type | Kind | Notes |
|------|------|-------|
| `ResilienceEvent` | record | Full structured event. See `docs/logging.md` |
| `ResilienceEventType` | enum | `CallStarted`, `RetryAttempted`, `CallSucceeded`, `CallFailed`, `CircuitOpened`, `CircuitClosed`, `CircuitHalfOpened`, `FallbackUsed`, `TimeoutBreached`, `RateLimited`, `BulkheadRejected`, `HedgeWon`, `HedgeLost`, `HedgeCancelled` |

## Extensions

### `Portfolio.Resilience.Extensions`

| Type | Kind | Notes |
|------|------|-------|
| `ServiceCollectionExtensions` | static class | `IServiceCollection.AddPortfolioResilience(Action<ResilienceBuilder>?)` |
| `ConfigurationExtensions` | static class | `ResilienceBuilder.LoadFromConfiguration(IConfiguration, string)` |

## HttpClient

### `Portfolio.Resilience.HttpClient`

| Type | Kind | Notes |
|------|------|-------|
| `ResilientHttpMessageHandler` | sealed class | `DelegatingHandler`. `PolicyName` property |
| `HttpClientBuilderExtensions` | static class | `IHttpClientBuilder.AddResilientHandler(string)`, `IHttpClientBuilder.AddStandardResilienceHandler(Action<PolicyDefinition>?)` |

## Implementation

### `Portfolio.Resilience.Implementation`

Concrete service implementations. Public because DI requires them to be
resolvable and because health endpoints depend on them. **Do not reference
these types directly in application code — resolve the interfaces instead.**

| Type | Implements |
|------|-----------|
| `ResilienceExecutor` | `IResilienceExecutor` |
| `ResiliencePolicyRegistry` | `IResiliencePolicyRegistry` |
| `ResilienceEventEmitter` | (concrete — builds events) |
| `LatencyTracker` | `ILatencyTracker` |
| `CircuitBreakerMonitor` | `ICircuitBreakerMonitor` |

## Middleware

### `Portfolio.Resilience.Middleware`

| Type | Kind | Notes |
|------|------|-------|
| `ResilienceExceptionMiddlewareBase` | abstract class | Services inherit and override `RenderErrorAsync(HttpContext, ResilienceException)` |

## Policies

### `Portfolio.Resilience.Policies`

| Type | Kind | Notes |
|------|------|-------|
| `RetryPolicyBuilder` | sealed class | `ExecuteAsync<T>`, `CalculateDelay` (public for tests), implements `IResiliencePolicy` |
| `TimeoutPolicyBuilder` | sealed class | `ExecuteAsync<T>`, implements `IResiliencePolicy` |
| `CircuitPolicyBuilder` | sealed class | `ExecuteAsync<T>`, implements `ICircuitBreakerMonitor` and `IResiliencePolicy` |
| `RateLimiterPolicyBuilder` | sealed class | `ExecuteAsync<T>`, four strategies, optional `ResilienceEventEmitter`, implements `IResiliencePolicy` |
| `BulkheadPolicyBuilder` | sealed class | `ExecuteAsync<T>`, semaphore + waiter cap, optional `ResilienceEventEmitter`, implements `IResiliencePolicy` |
| `HedgingPolicyBuilder` | sealed class | `ExecuteAsync<T>`, parallel attempts with stagger, `CalculateDelayMs` (public for tests), implements `IResiliencePolicy` |
| `CompositePolicyBuilder` | sealed class | Default pipeline: RateLimiter → Bulkhead → Hedging → Retry → Circuit → Timeout |
| `ResiliencePipeline` | sealed class | Ordered `IResiliencePolicy` layers, executed outermost-first. `Wrap(...)` static factory, `Layers` property, `ExecuteAsync<T>` |
| `ResiliencePipelineBuilder` | sealed class | Fluent builder: `WithName`, `Add`, `AddIf`, `Build` |

## Sinks

### `Portfolio.Resilience.Sinks`

| Type | Kind | Implements |
|------|------|-----------|
| `ConsoleLogSink` | sealed class | `ILogSink` |
| `FileLogSink` | sealed class | `ILogSink`, `IDisposable` |
| `NullLogSink` | sealed class | `ILogSink` (singleton: `NullLogSink.Instance`) |
| `CompositeLogSink` | sealed class | `ILogSink` |
| `InMemoryMetricSink` | sealed class | `IMetricSink` |
| `CompositeMetricSink` | sealed class | `IMetricSink` |

---

## OpenTelemetry

### `Portfolio.Resilience.OpenTelemetry`

Optional package. Provides two sinks that map the library's events and metrics
to native OpenTelemetry telemetry, plus a one-line DI registration.

| Type | Kind | Notes |
|------|------|-------|
| `OpenTelemetryLogSink` | sealed class | Implements `ILogSink`. Maps `ResilienceEvent` to OTel log records with `resilience.*` attributes |
| `OpenTelemetryMetricSink` | sealed class | Implements `IMetricSink`. Records durations as histogram `resilience.call.duration_ms`; outcomes as counters `resilience.call.succeeded_total` and `resilience.call.failed_total` |
| `OpenTelemetryBuilderExtensions` | static class | `IServiceCollection.AddPortfolioResilienceOpenTelemetry(string? meterName)`, `ResilienceBuilder.AddOpenTelemetrySinks(ILoggerFactory, Meter?)` |

## Analyzers

### `Portfolio.Resilience.Analyzers`

Optional package. Ships two Roslyn analyzers. The package has no runtime
dependency — it is loaded by the compiler only.

| Type | Kind | Notes |
|------|------|-------|
| `HttpClientBypassAnalyzer` | sealed class | PR0001 — warns when an `HttpClient` from `IHttpClientFactory` is used without resilience |
| `MisconfigurationAnalyzer` | sealed class | PR0002 — warns when a policy enables a feature with an invalid companion value |

Both rules are enabled by default and suppressible via `#pragma warning
disable` or `.editorconfig`.

---

## What is NOT part of the public API

The following are implementation details and may change without notice:

- **`internal` types** — `HttpRequestSnapshot` in `HttpClient/` is internal.
- **`private` and `internal` members** of public types.
- **XML doc comments** — describing behavior, but not contract.
- **File and folder layout** — the library may reorganize files without
  changing the API.
- **Assembly name** — currently `Portfolio.Resilience`. Consumers should reference
  the NuGet package ID, not the assembly.
- **Analyzer internals** — the concrete `DiagnosticDescriptor` objects, the
  heuristic thresholds, and the exact set of methods detected by PR0001 may
  change between minor releases as we refine the rules.

## Versioning policy

The library follows **semantic versioning**:

| Change | Example | Version bump |
|--------|---------|--------------|
| Bug fix, no API change | Fix jitter bounds | `0.7.0` → `0.7.1` |
| New optional parameter | Add `bool x = false` to a method | `0.7.0` → `0.8.0` |
| New type, new interface member | Add `IFoo` | `0.7.0` → `0.8.0` |
| Remove or rename public type | Rename `ResilienceBuilder` | `0.x` → `1.0` or `1.x` → `2.0` |
| Change method signature (breaking) | `ExecuteAsync<T>(A, B)` → `ExecuteAsync<T>(A, B, C)` with no default for C | major bump |
| Change event field name | `policy_name` → `policy` | major bump |
| Change metric name | `p50_ms` → `p50` | major bump |

**Spec changes** follow their own versioning. A library release declares which
SPEC version it targets. See `SPEC.md` Appendix A.

## Compatibility contract

**Consumers can rely on:**

- Types and members listed in this document existing with the documented shape.
- Event field names matching `SPEC.md` §7.2 exactly.
- Metric names matching `SPEC.md` §8.1 exactly.
- Error categories matching `SPEC.md` §6.1 exactly.
- Retry formulas matching `SPEC.md` §4.2 exactly.
- Rate limiter strategies and rejection reasons matching `SPEC.md` §12 exactly.
- Bulkhead rejection reasons matching `SPEC.md` §13 exactly.
- `ResiliencePipeline` composition semantics matching `SPEC.md` §14 exactly.
- OTel attribute names matching `SPEC.md` §15 exactly.
- Hedging failure semantics matching `SPEC.md` §16 exactly.
- Analyzer rule IDs `PR0001` and `PR0002` matching `SPEC.md` §17 exactly.

**Consumers cannot rely on:**

- Order of items in `Snapshot()` collections.
- Exact byte content of log output (only the JSON schema is contractual).
- Exact timing of circuit transitions (only the state machine is contractual).
- Exact ordering of queued calls for windowed rate limit strategies
  (documented as best-effort in `docs/rate-limiter.md`).
- Presence of internal diagnostics in exceptions beyond `Metadata`.
- The exact set of methods matched by `PR0001` (may be refined in minor versions).
- The exact text of analyzer messages (only the IDs are contractual).

## See also

- [../SPEC.md](../SPEC.md) — the language-agnostic contract
- [../CHANGELOG.md](../CHANGELOG.md) — historical API changes
- [executor.md](executor.md) — the main entry point
- [composition.md](composition.md) — the composition API
- [rate-limiter.md](rate-limiter.md) — the rate limiter contract
- [bulkhead.md](bulkhead.md) — the bulkhead contract
- [hedging.md](hedging.md) — the hedging contract
- [opentelemetry.md](opentelemetry.md) — the OTel package
- [analyzers.md](analyzers.md) — the analyzers package
- [../README.md](../README.md) — install and quick-start
