<!--
filepath: CHANGELOG.md
package:  Portfolio.Resilience | since: v0.2.0
purpose:  Version history for the resilience library.
-->

# Changelog

All notable changes to `Portfolio.Resilience`. Format follows [Keep a Changelog](https://keepachangelog.com/),
versioning follows [Semantic Versioning](https://semver.org/).

## [0.8.0] - 2026-09-14

### Added - Idempotency key propagation

- **`IdempotencyContext`** (`Correlation/`) - ambient `AsyncLocal` store for the idempotency key of the current flow. Mirrors `CorrelationContext`'s push/pop semantics. New method `GenerateFromCorrelation()` derives a stable key from the ambient correlation ID.
- **`HttpClientOptions`** (`Configuration/`) - configurable HTTP header name for the idempotency key. Default: `Idempotency-Key` (Stripe, Adyen, Square convention). `Validate()` returns human-readable warnings.
- **`IResilienceExecutor.ExecuteAsync`** - new optional parameters `idempotencyKey` and `timeBudgetMs` on both overloads. Backward compatible; existing call sites compile unchanged.
- **`ResilienceExecutor`** - accepts the idempotency key, honors an existing ambient key, otherwise derives one from the correlation ID, pushes the ambient before the pipeline runs, and pops it in `finally`.
- **`ResilientHttpMessageHandler`** - reads `IdempotencyContext.CurrentKey` and adds the configured header on **every attempt** - the primary request and every retried request. New `HttpClientOptions?` constructor parameter.
- **`HttpClientBuilderExtensions.AddResilientHandler`** - new overload accepting `Action<HttpClientOptions>?` for per-client configuration. Existing two-arg overload remains.
- **`docs/idempotency.md`** - new doc. Payment + generic microservice examples. **Dual framing:** this feature serves any non-idempotent write, not just payments.
- **`SPEC.md` section 18** - new normative section.

### Added - PCI-safe event scrubbing

- **`IEventScrubber`** (`Abstractions/`) - contract for redacting sensitive patterns from events before any sink sees them.
- **`DefaultPciScrubber`** (`Sinks/`) - regex-based default. Masks PAN (13-19 digits with optional spaces/dashes), CVV/CVC (3-4 digits near "cvv"/"cvc"), and SSN (`ddd-dd-dddd`). Replacement token: `[REDACTED]`.
- **`CompositeLogSink`** - now accepts an optional `IEventScrubber` and applies it once at the top of `Emit`, before child fan-out. New `HasScrubber` property.
- **`LoggingOptions.ScrubSensitiveData`** - new opt-in flag (default `false`). When true on any policy, the DI wiring installs the default scrubber globally and wraps single sinks in a composite so the scrubber always runs.
- **`ResilienceOptionsExtensions.AnyPolicyScrubsSensitiveData()`** - helper used by DI wiring.
- **`docs/pci-scrubbing.md`** - new doc. **Dual framing:** any service handling sensitive data (PII, tokens, card numbers), not just payments.
- **`SPEC.md` section 19** - new normative section.

### Added - Timeout budget propagation

- **`TimeBudgetContext`** (`Correlation/`) - ambient `AsyncLocal<DateTime?>` storing an absolute deadline. `RemainingMs`, `IsExhausted`, `RemainingOrMax()`, `Push(int budgetMs)`. Nested scopes restore the previous deadline.
- **`IResilienceExecutor.ExecuteAsync`** - new optional parameter `timeBudgetMs`. When set, the executor pushes the ambient before running the pipeline.
- **`ResilienceExecutor`** - accepts `timeBudgetMs`, pushes and pops the ambient scope in the call.
- **`RetryPolicyBuilder`** - stops retrying when `delay + floor` cannot fit in the remaining budget. Preserves the original exception type.
- **`TimeoutPolicyBuilder`** - effective ceiling becomes `min(configured, remaining)`. When `TimeoutMs <= 0` and a budget is active, the budget wins. Exhausted budget throws immediately with `metadata.remaining_ms`.
- **`HedgingPolicyBuilder`** - does not fire a new hedge when `delay + attemptTimeout` exceeds the remaining budget.
- **`docs/time-budget.md`** - new doc. **Dual framing:** any caller with an SLA, not just payments.
- **`SPEC.md` section 20** - new normative section.

### Added - Payment-safe pipeline preset

- **`ResiliencePipeline.WithPaymentSafeDefaults()`** - static factory returning a `ResiliencePipeline` with the safe layer order: `RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation`. Both a no-arg overload (fresh instances) and an overload accepting optional layer instances.
- **`docs/composition.md`** - new preset section. Corrected the default-order diagram (the doc predated v0.7.0's hedging addition).
- **`SPEC.md` section 21** - new normative section.

### Changed

- **`docs/composition.md`** - default pipeline diagram corrected to include the Hedging layer in the middle position (`RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout`). The previous diagram omitted hedging.
- **`README.md`** - new "How we compare with Polly" section replaces the previous "Comparison with other libraries" section. Cloud sinks are now documented as ecosystem-supported via the OTel package, not as first-party v0.8.0 deliverables.
- **`docs/executor.md`** - pipeline diagram corrected to the six-layer order. DI registration table extended to list every registered service.
- **`docs/logging.md`** - event list extended to all fourteen types. Configuration section rewritten to describe every `LoggingOptions` toggle. Removed stale "Stage C" placeholder text.
- **`docs/api-stability.md`** - public type count verified by reflection against the compiled v0.8.0 assembly. All v0.8.0 types listed.

### Fixed

- **`ResiliencePolicyRegistry.Clone`** was silently dropping `EmitRateLimited`, `EmitBulkheadRejected`, and `EmitHedgeEvents` on every policy resolve (latent since v0.6.0/v0.7.0). Now copied in full.
- **`ResiliencePolicyRegistry.ValidateOnce`** did not call `Hedging.Validate`, so hedging misconfigurations were never warned about at startup (latent since v0.7.0). Now called.
- **`ResilienceExecutor`** was overwriting an existing ambient idempotency key when no explicit key was passed. Now honors the ambient key first, then falls back to correlation-derived generation.

### Compatibility

- **No breaking changes.** All additions are additive. Existing consumers upgrade from `0.7.0` to `0.8.0` without code changes.
- Two-arg `AddResilientHandler(builder, policyName)` remains; the three-arg overload is new.
- The two-arg `ResilientHttpMessageHandler(executor, policyName)` constructor remains; the three-arg overload is new.
- The default behavior for calls without an idempotency key or budget parameter is byte-for-byte identical to v0.7.0.
- Backward compatibility is verified by `BackwardCompatibilityV07Tests.cs` - every v0.7.0-era call shape compiles and behaves identically under v0.8.0.

### Test suite

- **523 tests, 0 failures, 0 warnings.** Up from 417 in v0.7.0 (+106 new tests across the four v0.8.0 capabilities, plus the backward-compatibility suite).
- Breakdown: 468 core tests + 37 OpenTelemetry tests + 18 analyzer tests.
## [0.7.0] - 2026-09-14

### Added � Policy Composition

- **`IResiliencePolicy`** (`Abstractions/`) � a single interface that every policy builder implements. Enables custom pipeline composition.
- **`ResiliencePipeline`** (`Policies/`) � an ordered sequence of `IResiliencePolicy` layers, executed outermost-first. Constructed via `ResiliencePipeline.Wrap(...)` or the constructor.
- **`ResiliencePipelineBuilder`** (`Policies/`) � fluent builder (`Add`, `AddIf`, `WithName`, `Build`) for constructing a pipeline with conditionals.
- `RetryPolicyBuilder`, `CircuitPolicyBuilder`, `TimeoutPolicyBuilder`, `RateLimiterPolicyBuilder`, and `BulkheadPolicyBuilder` now implement `IResiliencePolicy`.

### Added � OpenTelemetry integration

- **New NuGet package: `Portfolio.Resilience.OpenTelemetry`** � optional package that exports resilience events and metrics via OpenTelemetry.
- **`OpenTelemetryLogSink`** � maps every `ResilienceEvent` to an OTel log record with structured `resilience.*` attributes.
- **`OpenTelemetryMetricSink`** � maps `IMetricSink.RecordCall` to OTel histogram `resilience.call.duration_ms` and counters `resilience.call.succeeded_total` / `resilience.call.failed_total`.
- **`OpenTelemetryBuilderExtensions`** � one-line registration: `services.AddPortfolioResilienceOpenTelemetry()`.
- The order-independent composition pattern allows calling `AddPortfolioResilienceOpenTelemetry()` before or after `AddPortfolioResilience(...)`.
- Dependency on `OpenTelemetry.Api` 1.15.3 (patched version; earlier 1.11.0 was vulnerable per GHSA-8785-wc3w-h8q6 and GHSA-g94r-2vxg-569j).

### Added � Hedging

- **`HedgingOptions`** (`Configuration/`) � 8 fields: `Enabled`, `MaxAttempts`, `DelayMs`, `ExponentialBackoff`, `AttemptTimeoutMs`, `CancelOnSuccess`, `EmitAttemptEvents`, `RejectionCategory`. Includes a `Validate(string policyName)` method.
- **`HedgingPolicyBuilder`** (`Policies/`) � fires parallel attempts with a stagger delay; the first success wins. Hedges only fire if no prior attempt has already succeeded.
- **Three new event types**: `ResilienceEventType.HedgeWon = 11`, `HedgeLost = 12`, `HedgeCancelled = 13`.
- **`LoggingOptions.EmitHedgeEvents`** � one toggle for all three hedge events (default: `true`).
- **`PolicyDefinition.Hedging`** � new property.
- **`ResilienceEventEmitter.EmitHedgeWon/EmitHedgeLost/EmitHedgeCancelled`** � three new convenience emitters.
- **Safety:** hedging is not safe for non-idempotent operations unless the downstream deduplicates on an idempotency key. Documented in `docs/hedging.md` with a prominent warning. A startup warning is logged once per policy that enables hedging.

### Added � Standard handler

- **`HttpClientBuilderExtensions.AddStandardResilienceHandler(Action<PolicyDefinition>? configure = null)`** � registers a `"standard"` policy with sensible defaults (retry + circuit + timeout; rate limiter, bulkhead, and hedging off) and wires every request through it.
- **`StandardPolicy`** (`Configuration/`) � the built-in policy definition (`Name = "standard"`) used by the handler.
- **User overrides win.** If a caller registers their own `"standard"` policy via `AddPolicy`, the extension does not overwrite it.
- **Order-independent.** Registering the handler before or after `AddPortfolioResilience` both work; the post-configure pattern resolves the policy at container-build time.

### Added � Roslyn analyzers

- **New NuGet package: `Portfolio.Resilience.Analyzers`** � optional package that ships two compile-time analyzers.
- **PR0001 � `HttpClientBypassAnalyzer`** � warns when a class obtains an `HttpClient` from `IHttpClientFactory` and calls it directly without going through the resilience pipeline. Includes an `IResilienceExecutor` guard to prevent false positives.
- **PR0002 � `MisconfigurationAnalyzer`** � warns when an `AddPolicy` lambda enables a feature (`RateLimiter`, `Bulkhead`, `Hedging`) but sets its companion value to `0` or negative, or sets `Timeout.TimeoutMs` / `Retry.MaxAttempts` to a negative value.
- Both rules are enabled by default and suppressible via `#pragma warning disable` or `.editorconfig`.
- The analyzer project targets `netstandard2.0` (required for Roslyn analyzers) and ships with `<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>`.

### Changed

- **`CompositePolicyBuilder`** � pipeline is now assembled by composing `IResiliencePolicy` layers via `ResiliencePipeline`. The default order remains `RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout`. Behavior is byte-for-byte identical to v0.6.x; only the internal mechanism changed.
- **`ServiceCollectionExtensions.AddPortfolioResilience`** � the `IResiliencePolicyRegistry` registration now applies all registered `Action<ResilienceOptions>` singletons before constructing the registry. This enables post-registration policy injection by extension packages (such as `AddStandardResilienceHandler`).
- **`ConfigurationExtensions.LoadFromConfiguration`** � binds the new `Hedging` section.

### Fixed

- **Hedging deadlock** � `HedgingPolicyBuilder.WaitForFirstSuccessOrAllFailAsync` could deadlock when one hedged attempt succeeded while another was pending indefinitely. Fixed by checking for already-completed winners before awaiting the pending set.
- **Hedging duplicate-fire** � the initial implementation fired all attempts unconditionally. Now each hedge fires only if no prior attempt has succeeded within its stagger delay.
- **Hedging per-attempt timeout** � `HedgingOptions.AttemptTimeoutMs` was silently ignored in an early iteration. Now honored via a linked `CancellationTokenSource` per attempt.
- **`OpenTelemetryLogSink` event filtering** � the sink no longer short-circuits on `ILogger.IsEnabled`. Filtering is the logger pipeline's responsibility. `call_started` maps to `Debug` (was `Trace`, which is filtered by most loggers).
- **Security** � `OpenTelemetry.Api` bumped from `1.11.0` (two advisories) to `1.15.3`.

### Documentation

- **New:** `docs/composition.md` � pipeline composition, both `Wrap` and builder styles, real-world patterns, custom layers.
- **New:** `docs/hedging.md` � hedging semantics with a prominent non-idempotency safety section.
- **New:** `docs/opentelemetry.md` � the OTel package's sinks, attribute mappings, one-liner registration.
- **New:** `docs/analyzers.md` � the analyzer package, PR0001 and PR0002 walkthroughs, `.editorconfig` examples, troubleshooting.
- **Updated:** `docs/timeout.md`, `docs/metrics.md`, `docs/error-classification.md` � appended "How to use it � a worked walkthrough" sections.
- **Updated:** `docs/metrics.md` � removed a duplicated `## Test coverage` section; corrected stale "Stage C/E/F/H" references.
- **Updated:** `docs/http-integration.md` � new section on `AddStandardResilienceHandler()`.
- **Updated:** `docs/README.md`, `docs/api-stability.md`, `README.md` � new doc and feature entries.
- **Updated:** `SPEC.md` � new �14 (Policy composition), �15 (OpenTelemetry integration), �16 (Hedging), �17 (Analyzers).

### Compatibility

- **No breaking changes.** All additions are additive. Existing consumers can upgrade from `0.6.x` to `0.7.0` without code changes.
- The `ResilienceEventType` enum gains values `11`, `12`, and `13`. Existing numeric values `0`�`10` are unchanged.
- The `PolicyDefinition` class gains a `Hedging` property. Existing code that does not use it is unaffected.
- `CompositePolicyBuilder`'s constructor gains an optional `HedgingPolicyBuilder? hedging = null` parameter. Existing construction sites are unaffected.
- Existing policies that do not set `Hedging.Enabled = true` behave exactly as in `0.6.x`.

### Known limitations

- **`LoggingOptions` toggles are not yet consulted by the emitter** � this is unchanged from `0.6.0`. Wiring the toggles to emission is a future item.

### Test suite

- **417 tests, 0 failures, 0 warnings.** Up from 391 in v0.6.1 (+26: composition, hedging, standard handler, analyzers, and end-to-end integration tests).
## [0.6.1] - 2026-09-13

### Documentation

- **README.md: new "What ships in the box" section** - a single consolidated
  table of every concern the library provides, with the type(s) that implement
  it and the doc that explains it. Placed immediately after the
  cross-cutting-concerns table so a reader sees the full surface in one view.
- **README.md: corrected test count** from 273 to 301. The count was accurate
  when v0.6.0's README was first drafted but drifted when the integration tests
  landed in a later commit. Corrected in both the repository-structure tree and
  the test-suite section.

### Compatibility

- **No code changes.** Binary-compatible with 0.6.0. All 301 tests pass.
## [0.6.0] - 2026-09-13

### Added � Rate Limiter

- **`RateLimiterOptions`** � configuration model with `Enabled`, `Strategy`,
  `PermitLimit`, `WindowSeconds`, `QueueLimit`, `QueueTimeoutMs`,
  `RejectionCategory`, and a `Validate(string policyName)` method that returns
  human-readable warnings for suspicious combinations.
- **`RateLimitStrategy`** � enum for the four strategies: `TokenBucket`,
  `SlidingWindow`, `FixedWindow`, `ConcurrencyLimit`.
- **`RateLimiterPolicyBuilder`** � executes an operation under a rate limiter.
  Each strategy has its own acquire path; rejections emit a `rate_limited`
  event and throw `ResilienceException` with the configured
  `RejectionCategory`.
- 18 tests in `RateLimiterPolicyBuilderTests.cs` covering all four strategies,
  queue behavior, rejection metadata, and category configuration.

### Added � Bulkhead

- **`BulkheadOptions`** � configuration model with `Enabled`, `MaxConcurrency`,
  `MaxQueue`, `QueueTimeoutMs`, `RejectionCategory`, and `Validate`.
- **`BulkheadPolicyBuilder`** � caps concurrent calls via a `SemaphoreSlim`
  held across the operation. Excess callers wait in a bounded queue; callers
  beyond the queue are rejected with `queue_full`.
- 13 tests in `BulkheadPolicyBuilderTests.cs` covering the concurrency cap,
  queue behavior, slot release on success and failure, and rejection metadata.

### Added � Events and pipeline

- **`ResilienceEventType.RateLimited = 9`** and
  **`ResilienceEventType.BulkheadRejected = 10`** � new event types.
- **`ResilienceEventEmitter.EmitRateLimited(...)`** and
  **`EmitBulkheadRejected(...)`** � convenience emitters with the field names
  defined in SPEC �12 and �13.
- **`LoggingOptions.EmitRateLimited`** and **`EmitBulkheadRejected`** �
  per-event-type toggles (both default to `true`).
- **`CompositePolicyBuilder`** now chains
  `RateLimiter ? Bulkhead ? Retry ? Circuit ? Timeout` and gains two optional
  constructor parameters for the new builders. Existing construction sites are
  unaffected.
- **`PolicyDefinition`** gains `RateLimiter` and `Bulkhead` properties.

### Added � Configuration

- **`ConfigurationExtensions`** now binds `RateLimiter` and `Bulkhead`
  sections from `IConfiguration`.
- **`ResiliencePolicyRegistry`** validates each policy once on first resolve
  and forwards warnings to an injected `Action<string>? warn` delegate.
  `ServiceCollectionExtensions` routes warnings through `Trace.TraceWarning`.

### Changed

- **`CompositePolicyBuilder`** � pipeline order is now
  `RateLimiter ? Bulkhead ? Retry ? Circuit ? Timeout`. When `Enabled = true`
  is set on a policy but the corresponding builder was not provided to the
  pipeline, execution throws `InvalidOperationException` (fail loud, not
  silent non-enforcement).
- **`ResilienceEventEmitter`** gains an optional `ILogSink` parameter (already
  existed) and two new public methods.
- **`ResiliencePolicyRegistry.Clone`** now clones `RateLimiter` and `Bulkhead`
  options in addition to the five existing sections.

### Documentation

- **`docs/rate-limiter.md`** � full doc: four strategies, strategy-to-field
  matrix, queue behavior, rejection metadata, configuration, validation
  warnings, common mistakes, testing.
- **`docs/bulkhead.md`** � full doc: concurrency cap, waiter queue, slot
  release semantics, difference from rate limiting, configuration,
  validation warnings, common mistakes, testing.
- **`docs/README.md`** � refreshed index: all current docs listed, stale
  `Stage D/E/F/G/H` markers removed, test count corrected.
- **`SPEC.md`** � �7.1 event table extended with `rate_limited` and
  `bulkhead_rejected`; new �12 (Rate limiter) and �13 (Bulkhead); version
  bumped to `0.6.0`.
- **`README.md`** � feature table, quick-start, pipeline diagram, comparison
  table, roadmap, and test count all updated for v0.6.0.
- **`docs/api-stability.md`** � new public types added; total count corrected
  to 52; compatibility contract extended with rate limiter and bulkhead
  clauses.

### Compatibility

- **No breaking changes.** All additions are additive. Existing consumers can
  upgrade from `0.5.1` to `0.6.0` without code changes.
- Existing policies that do not set `RateLimiter.Enabled = true` or
  `Bulkhead.Enabled = true` see identical behavior to v0.5.1.

### Known limitations

- **`LoggingOptions` toggles are not yet consulted by the emitter.** The nine
  per-event-type booleans (including the two new ones, `EmitRateLimited` and
  `EmitBulkheadRejected`) are part of the stable public API, but every
  configured event currently fires unconditionally. Wiring the toggles to
  emission is a v0.7.0 item. See `docs/logging.md` for details.

### Test suite

- **273 tests, 0 failures, 0 warnings.** Up from 237 in v0.5.1
  (+36 new tests: 18 rate limiter, 13 bulkhead, 5 composite integration).
## [0.5.1] - 2026-09-12

### Documentation

- **README.md: new "Logging scenarios" section** � five configurations for
  choosing where resilience events go:
  - Local-only (ConsoleLogSink or FileLogSink)
  - Cloud-only (custom ILogSink implementation)
  - Hybrid (local + cloud via CompositeLogSink)
  - Silent (NullLogSink � no configuration)
  - Includes a decision table mapping each scenario to the API call.
- **README.md: expanded "Comparison with other libraries"** � added side-by-side
  feature table with Polly and Microsoft.Extensions.Http.Resilience.
- **README.md: enriched "Roadmap" section** � versioned plan through v1.x.

### Internal

- **PLANNING.md** � new private planning document (not committed; see `.gitignore`).
- **.gitignore** � added private planning files.

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
correlation header, and structured logging are shared. See SPEC.md �ResponseEnvelopeOwnership.

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


## [0.2.0] � Stage C complete

### Added

- `Correlation/CorrelationContext.cs` � ambient `AsyncLocal` correlation primitive
- `Correlation/AsyncLocalCorrelationAccessor.cs` � DI adapter implementing `ICorrelationAccessor`
- `Sinks/ConsoleLogSink.cs` � compact JSON Lines to stdout
- `Sinks/FileLogSink.cs` � daily-rotating JSON Lines to file (`resilience-YYYY-MM-DD.jsonl`)
- `Sinks/NullLogSink.cs` � silent no-op sink
- `Sinks/CompositeLogSink.cs` � fan-out log sink with per-child exception isolation
- `Sinks/InMemoryMetricSink.cs` � bounded rolling window with p50/p95/p99 and error rate
- `Sinks/CompositeMetricSink.cs` � fan-out metric sink with per-child exception isolation
- 67 passing tests covering every sink, the correlation primitive, and the DI adapter

### Contract (Stage B, unchanged)

- `Abstractions/` � interfaces: `ILogSink`, `IMetricSink`, `ICorrelationAccessor`,
  `ICircuitBreakerMonitor`, `ILatencyTracker`, `IResilienceExecutor`, `IResiliencePolicyRegistry`
- `Configuration/` � options classes for retry, circuit, timeout, fallback, logging
- `Errors/` � `ResilienceException`, `ResilienceErrorCategory`
- `Events/` � `ResilienceEvent`, `ResilienceEventType`

### Documentation

- `docs/README.md` � index
- `docs/correlation.md` � correlation concern
- `docs/logging.md` � logging concern
- `docs/metrics.md` � metrics concern

### Not yet implemented (future stages)

- Stage D � error classification + policy builders
- Stage E � circuit monitor, latency tracker, event emitter, policy registry
- Stage F � the executor and DI extensions
- Stage G � HttpClient handler and exception middleware base
- Stage H � integration into landing-page-service, `/health/resilience` endpoint

## [0.1.0] � Initial scaffold (Stage A + Stage B)

### Added

- Solution skeleton
- Abstractions, configuration options, errors, and events contracts
