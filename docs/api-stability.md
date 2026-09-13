<!--
filepath: docs/api-stability.md
package:  Portfolio.Resilience | since: v0.6.0
purpose:  The frozen public API surface. Changes to this document require a version bump.
-->

# API Stability

## What this document declares

The following types, interfaces, and methods constitute the **public API** of
`Portfolio.Resilience`. They are the contract between the library and every
consumer.

**Rule:** any change to this surface is a **breaking change** and requires a
semantic version bump:

- **Additive** (new types, new optional parameters) — minor bump (`0.6.x` → `0.7.0`)
- **Removal, rename, signature change** — major bump (`0.x` → `1.0`, `1.x` → `2.0`)

## Stability tier

**Every type listed here is stable as of v0.6.0.** There are no experimental or
deprecated APIs in this release.

## Public surface area at a glance

| Folder | Types | Purpose |
|--------|-------|---------|
| `Abstractions/` | 9 | Interfaces and snapshot types |
| `Configuration/` | 12 | Options, strategy enum, and builder |
| `Correlation/` | 2 | Ambient correlation |
| `Errors/` | 3 | Exception, category, classifier |
| `Events/` | 2 | Event record and enum |
| `Extensions/` | 2 | DI and configuration extension methods |
| `HttpClient/` | 2 | HTTP handler and fluent API |
| `Implementation/` | 5 | Concrete services |
| `Middleware/` | 1 | Abstract exception-handling base |
| `Policies/` | 6 | Retry, timeout, circuit, rate limiter, bulkhead, composite |
| `Sinks/` | 6 | Log and metric sinks |

**Total: 52 public types.**

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
| `PolicyDefinition` | class | A single policy: `Retry`, `Circuit`, `Timeout`, `Fallback`, `Logging`, `RateLimiter`, `Bulkhead` |
| `RetryOptions` | class | `MaxAttempts`, `BaseDelayMs`, `MaxDelayMs`, `JitterRatio`, `RetryOnPermanent` |
| `CircuitOptions` | class | `FailureThreshold`, `OpenDurationSeconds`, `SuccessThreshold`, `OnlyCountTransient` |
| `TimeoutOptions` | class | `TimeoutMs` |
| `FallbackOptions` | class | `Enabled`, `Reason` |
| `LoggingOptions` | class | Seven per-event-type booleans |
| `ErrorClassificationOptions` | class | Configurable lists + `TreatCancellationAsPermanent` |
| `RateLimitStrategy` | enum | `TokenBucket`, `SlidingWindow`, `FixedWindow`, `ConcurrencyLimit` |
| `RateLimiterOptions` | class | `Enabled`, `Strategy`, `PermitLimit`, `WindowSeconds`, `QueueLimit`, `QueueTimeoutMs`, `RejectionCategory`, `Validate(string)` |
| `BulkheadOptions` | class | `Enabled`, `MaxConcurrency`, `MaxQueue`, `QueueTimeoutMs`, `RejectionCategory`, `Validate(string)` |
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
| `ResilienceEventType` | enum | `CallStarted`, `RetryAttempted`, `CallSucceeded`, `CallFailed`, `CircuitOpened`, `CircuitClosed`, `CircuitHalfOpened`, `FallbackUsed`, `TimeoutBreached`, `RateLimited`, `BulkheadRejected` |

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
| `HttpClientBuilderExtensions` | static class | `IHttpClientBuilder.AddResilientHandler(string)` |

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
| `RetryPolicyBuilder` | sealed class | `ExecuteAsync<T>`, `CalculateDelay` (public for tests) |
| `TimeoutPolicyBuilder` | sealed class | `ExecuteAsync<T>` |
| `CircuitPolicyBuilder` | sealed class | `ExecuteAsync<T>`, implements `ICircuitBreakerMonitor` |
| `RateLimiterPolicyBuilder` | sealed class | `ExecuteAsync<T>`, four strategies, optional `ResilienceEventEmitter` |
| `BulkheadPolicyBuilder` | sealed class | `ExecuteAsync<T>`, semaphore + waiter cap, optional `ResilienceEventEmitter` |
| `CompositePolicyBuilder` | sealed class | Chains RateLimiter → Bulkhead → Retry → Circuit → Timeout |

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

## What is NOT part of the public API

The following are implementation details and may change without notice:

- **`internal` types** — `HttpRequestSnapshot` in `HttpClient/` is internal.
- **`private` and `internal` members** of public types.
- **XML doc comments** — describing behavior, but not contract.
- **File and folder layout** — the library may reorganize files without
  changing the API.
- **Assembly name** — currently `Portfolio.Resilience`. Consumers should reference
  the NuGet package ID, not the assembly.

## Versioning policy

The library follows **semantic versioning**:

| Change | Example | Version bump |
|--------|---------|--------------|
| Bug fix, no API change | Fix jitter bounds | `0.6.0` → `0.6.1` |
| New optional parameter | Add `bool x = false` to a method | `0.6.0` → `0.7.0` |
| New type, new interface member | Add `IFoo` | `0.6.0` → `0.7.0` |
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

**Consumers cannot rely on:**

- Order of items in `Snapshot()` collections.
- Exact byte content of log output (only the JSON schema is contractual).
- Exact timing of circuit transitions (only the state machine is contractual).
- Exact ordering of queued calls for windowed rate limit strategies
  (documented as best-effort in `docs/rate-limiter.md`).
- Presence of internal diagnostics in exceptions beyond `Metadata`.

## See also

- [../SPEC.md](../SPEC.md) — the language-agnostic contract
- [../CHANGELOG.md](../CHANGELOG.md) — historical API changes
- [executor.md](executor.md) — the main entry point
- [rate-limiter.md](rate-limiter.md) — the rate limiter contract
- [bulkhead.md](bulkhead.md) — the bulkhead contract
- [../README.md](../README.md) — install and quick-start
