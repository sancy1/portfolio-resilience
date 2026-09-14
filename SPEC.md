<!--
filepath: SPEC.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Language-agnostic contract. Every implementation (C#, Python, Go, etc.)
          MUST satisfy this specification so events, metrics, retry formulas,
          error categories, and correlation IDs are identical across languages.
-->

# Portfolio.Resilience — Specification

**Status:** v0.5.0 — first public release
**Applies to:** every language implementation (`.NET`, future Python, future Go)
**Rule:** if a doc and this spec disagree, **the spec wins**.

---

## 1. Purpose of this specification

Any developer implementing `Portfolio.Resilience` in a new language **must produce
a library that satisfies every requirement below**. This guarantees:

- **Cross-language event schemas.** A `retry_attempted` event emitted by a
  Python service parses identically to a `retry_attempted` event emitted by a
  .NET service.
- **Cross-language metric names.** A dashboard querying `p95_ms` works whether the
  metric was produced by .NET, Python, or Go.
- **Cross-language error categories.** A `Permanent` error in one language is
  `Permanent` in every language. Alerting rules that key on category do not need
  per-language branches.
- **Cross-language retry semantics.** A retry with `MaxAttempts = 3` and
  `BaseDelayMs = 100` produces the same delay sequence in every language.

An implementation that deviates from the spec is **not** a valid implementation
of this library — even if it compiles and runs.

---

## 2. Policy naming

A **policy** is a named configuration bundle: retry tuning, circuit thresholds,
timeout ceiling, fallback behavior, and per-event logging toggles.

### 2.1 Grammar

Policy names are strings. Two forms are permitted:

**Flat form (recommended for most cases):**

    <service-or-dependency>

Examples: `auth-service`, `notification-service`, `stripe-api`, `redis-cache`.

**Structured form (recommended for multi-tenant or per-operation policies):**

    <domain>.<resource>.<action>

Examples: `db.contact.read`, `db.contact.write`, `http.auth.users.get`.

Both forms are valid. Structured form is not parsed; it is a naming convention
only. The library treats the entire string as an opaque identifier.

### 2.2 Case sensitivity

Policy names **must be treated case-insensitively** by every implementation.

`Auth-Service`, `auth-service`, and `AUTH-SERVICE` refer to the same policy.

### 2.3 Length limit

Policy names **must be at most 200 characters**. Longer names may be truncated by
metric systems (Prometheus label limits, etc.).

### 2.4 Unknown policies

If `Resolve(name)` is called with a name that has no explicit policy definition,
the implementation **must** return the value of `ResilienceOptions.DefaultPolicy`,
but with the `Name` field overridden to the requested name.

Example: `Resolve("never-registered")` returns a policy with
`Name = "never-registered"` and the values from `DefaultPolicy`.

---

## 3. Default policies

Every implementation **should** ship a small set of default policies so that a
service can get started without configuring every dependency. These defaults
**must be overridable** by explicit policy definitions.

### 3.1 Fallback default policy

The `DefaultPolicy` used for unknown names:

    retry:
      max_attempts: 3
      base_delay_ms: 500
      max_delay_ms: 30000
      jitter_ratio: 0.3
      retry_on_permanent: false
    circuit:
      failure_threshold: 5
      open_duration_seconds: 30
      success_threshold: 1
      only_count_transient: true
    timeout:
      timeout_ms: 10000
    fallback:
      enabled: false
    logging:
      emit_call_started: false
      emit_retry_attempted: true
      emit_call_succeeded: true
      emit_call_failed: true
      emit_circuit_events: true
      emit_fallback_used: true
      emit_timeout_breached: true

### 3.2 Reserved policy name prefixes

The following prefixes are **reserved for future use** by the library. Do not
assign them to user policies:

    resilience.*
    portfolio.*
    _internal.*

An implementation may log a warning when a user registers a policy with a
reserved prefix, but must still honor the registration.
---

## 4. Retry algorithm

### 4.1 Attempt numbering

- **Attempt 1** is the initial call. No delay precedes it.
- **Attempt 2** is the first retry. Delay is `delay(1)`.
- **Attempt N** (N ≥ 2) is the (N-1)th retry. Delay is `delay(N-1)`.

The retry count `MaxAttempts` is the number of retries **after** the initial call.
So `MaxAttempts = 3` means a maximum of 4 total attempts.

### 4.2 Backoff formula

For the *n*-th retry (1-indexed):

    delay(n) = min(base_delay_ms * 2^(n-1), max_delay_ms) + jitter(n)

where

    jitter(n) = uniform_random(0, jitter_ratio * base_delay_ms)

**The random number generator must be seeded non-deterministically** in
production. Tests may inject a deterministic RNG.

**All numeric values are non-negative.** Negative or missing values fall back to
defaults.

### 4.3 Worked example

With `base_delay_ms = 100`, `max_delay_ms = 30000`, `jitter_ratio = 0.3`:

| Retry | Base | Capped | Jitter range | Actual delay range |
|-------|------|--------|--------------|-------------------|
| 1 | 100 | 100 | 0..30 | 100..130ms |
| 2 | 200 | 200 | 0..30 | 200..230ms |
| 3 | 400 | 400 | 0..30 | 400..430ms |
| 4 | 800 | 800 | 0..30 | 800..830ms |
| 5 | 1600 | 1600 | 0..30 | 1600..1630ms |
| 6 | 3200 | 3200 | 0..30 | 3200..3230ms |
| 7 | 6400 | 6400 | 0..30 | 6400..6430ms |
| 8 | 12800 | 12800 | 0..30 | 12800..12830ms |
| 9 | 25600 | 25600 | 0..30 | 25600..25630ms |
| 10 | 51200 | 30000 | 0..30 | 30000..30030ms |
| 11+ | ... | 30000 | 0..30 | 30000..30030ms |

### 4.4 Retry gating

The retry layer **must not retry** an attempt when the classification is:

- `Permanent` (unless `retry_on_permanent = true`)
- `CircuitOpen`
- `FallbackUsed`
- `Unknown`

The retry layer **must retry** when the classification is:

- `Transient`
- `Timeout`

See §6 for classification rules.

### 4.5 Retry exhaustion

When all attempts are exhausted, the retry layer **must rethrow the last exception**
(without wrapping it) to the caller. This is different from the timeout policy,
which wraps its failure in `ResilienceException(Timeout)`.

The composite pipeline may still wrap the exception at the executor level if the
service uses `ResilienceException` uniformly. See §10.

---

## 5. Circuit breaker state machine

### 5.1 States

A circuit is always in exactly one of three states:

- `Closed` — normal operation
- `Open` — rejecting calls
- `HalfOpen` — probing for recovery

State names are **exactly** the strings `"Closed"`, `"Open"`, `"HalfOpen"` in
metric and event outputs (PascalCase).

### 5.2 Transitions

| From | To | Trigger | Side effect |
|------|----|---------|-------------|
| `Closed` | `Open` | `consecutive_failures >= failure_threshold` | Record `opened_at_utc = now` |
| `Open` | `HalfOpen` | `now - opened_at_utc >= open_duration_seconds` | Mark probe slot available |
| `HalfOpen` | `Closed` | Probe succeeds | Reset `consecutive_failures = 0` |
| `HalfOpen` | `Open` | Probe fails | Reset `opened_at_utc = now` |

**The transition from `Open` to `HalfOpen` is time-driven, not call-driven.**
A call arriving while `Open` and after `open_duration_seconds` triggers the
transition to `HalfOpen` and consumes the probe slot. A call arriving while
`Open` and before the duration **must be rejected** without transitioning.

### 5.3 Failure counting

A failure **must increment `consecutive_failures`** when:

- Its classification is `Transient` **and** `only_count_transient = true`
  (the default), **or**
- `only_count_transient = false` (any failure counts)

A failure **must not increment `consecutive_failures`** when:

- Its classification is `Permanent` and `only_count_transient = true`.

**Exception:** a failed probe in `HalfOpen` always causes a transition to `Open`,
regardless of classification. The probe's purpose is to test the dependency —
if it fails, the dependency is not healthy.

### 5.4 Concurrency

Only **one probe** may be in-flight while the circuit is `HalfOpen`. Other calls
arriving during the probe **must be rejected** with `CircuitOpen`.

Implementations **must be thread-safe**. Concurrent calls against the same policy
must see consistent state.

### 5.5 State scope

Circuit state is **per-process by default**. A horizontally scaled deployment has
one circuit state per instance.

Implementations **may** provide a shared-state variant (Redis, etc.), but the
default must be per-process and the interface (`ICircuitBreakerMonitor` /
equivalent) must not assume either.
---

## 6. Error categories

### 6.1 The six categories

Every failure is classified into exactly one:

| Category | Retryable | Counts toward circuit (default) | Example |
|----------|-----------|--------------------------------|---------|
| `Transient` | Yes | Yes | HTTP 503, socket reset, deadlock |
| `Permanent` | No | No | HTTP 401, `ArgumentException` |
| `CircuitOpen` | No | No (circuit already open) | Any call while circuit is Open |
| `Timeout` | Yes | Yes | TimeoutPolicyBuilder ceiling fired |
| `FallbackUsed` | No | No (fallback succeeded) | Any call that used a fallback |
| `Unknown` | No | No | Only pre-classification; indicates a bug |

### 6.2 Classification priority

Classification examines an exception in **this exact order**. First match wins.

1. **Already-classified `ResilienceException`** → passthrough `Category`.
2. **`OperationCanceledException`** → `Permanent` if caller's token requested
   cancellation; else `Timeout`.
3. **HTTP status code** (from `HttpRequestException.StatusCode` or equivalent)
   → mapped per §6.3.
4. **SQLSTATE** (from `SqlState` property, duck-typed) → mapped per §6.4.
5. **Exception type name** → matched against §6.5 lists.
6. **Fallback** → `Permanent`.

### 6.3 HTTP status mapping

**Transient:** `408, 425, 429, 500, 502, 503, 504, 507, 509`
**Permanent:** `400, 401, 403, 404, 405, 406, 407, 409, 410, 411, 412, 413, 414, 415, 416, 417, 418, 421, 422, 423, 424, 426, 428, 431, 451`
**Class fallback:** unlisted `5xx` → `Transient`; unlisted `4xx` → `Permanent`

### 6.4 PostgreSQL SQLSTATE mapping (transient)

`08000, 08003, 08006, 08001, 08004, 08007, 40001, 40P01, 57P01, 57P02, 57P03, 53300, 55P03`

All other SQLSTATEs → `Permanent`.

### 6.5 Exception type names (transient)

- `System.Net.Http.HttpRequestException`
- `System.Net.Sockets.SocketException`
- `System.IO.IOException`
- `System.TimeoutException`
- `Npgsql.NpgsqlException`
- `StackExchange.Redis.RedisConnectionException`
- `StackExchange.Redis.RedisTimeoutException`

**Exception type names (permanent):**

- `System.ArgumentException`
- `System.ArgumentNullException`
- `System.ArgumentOutOfRangeException`
- `System.InvalidOperationException`
- `System.NotSupportedException`
- `System.FormatException`
- `System.ValidationException`

All other types → `Permanent` (conservative default).

### 6.6 Configuration

Every list above **must be overridable**. Each implementation must expose an
options object or equivalent with:

- `transient_http_status_codes`
- `permanent_http_status_codes`
- `transient_sql_states`
- `transient_exception_type_names`
- `permanent_exception_type_names`
- `treat_cancellation_as_permanent` (default `true`)

---

## 7. Events

### 7.1 Event types

Nine event types. Names are **exactly** as shown (snake_case):

| Type | Meaning |
|------|---------|
| `call_started` | An operation is about to be attempted |
| `retry_attempted` | A retry is about to run after a delay |
| `call_succeeded` | The operation returned successfully |
| `call_failed` | The operation failed (all retries exhausted, or non-retryable) |
| `circuit_opened` | Circuit transitioned Closed → Open |
| `circuit_closed` | Circuit transitioned HalfOpen → Closed |
| `circuit_half_opened` | Circuit transitioned Open → HalfOpen |
| `fallback_used` | The caller-provided fallback produced a value |
| `timeout_breached` | The timeout ceiling fired |
| `rate_limited` | A rate limiter rejected a call (no permit, or queue full/timeout) |
| `bulkhead_rejected` | A bulkhead rejected a call (no concurrency slot, queue full, or queue timeout) |

### 7.2 Event fields

Every event has these fields. Field names are **exactly** as shown (snake_case).
Nullable fields may be omitted if null.

| Field | Type | Notes |
|-------|------|-------|
| `event_type` | string | One of §7.1 |
| `policy_name` | string | The policy this event belongs to |
| `correlation_id` | string? | From `CorrelationContext.CurrentId` |
| `timestamp_utc` | ISO-8601 string | UTC |
| `attempt` | int? | 1-indexed attempt number |
| `duration_ms` | float? | Elapsed time in milliseconds |
| `error_category` | string? | `Transient`, `Permanent`, etc. |
| `error_message` | string? | Human-readable |
| `error_type` | string? | Exception type full name |
| `metadata` | object | Policy-specific key/value pairs |

### 7.3 Example events (JSON Lines)

    {"event_type":"call_started","policy_name":"auth-service","correlation_id":"abc-123","timestamp_utc":"2026-09-12T13:00:00.000Z","metadata":{}}
    {"event_type":"retry_attempted","policy_name":"auth-service","correlation_id":"abc-123","timestamp_utc":"2026-09-12T13:00:00.200Z","attempt":2,"metadata":{"delay_ms":120}}
    {"event_type":"call_succeeded","policy_name":"auth-service","correlation_id":"abc-123","timestamp_utc":"2026-09-12T13:00:00.350Z","attempt":2,"duration_ms":350.5}
    {"event_type":"circuit_opened","policy_name":"auth-service","correlation_id":null,"timestamp_utc":"2026-09-12T13:05:12.000Z","metadata":{"consecutive_failures":5,"opened_at_utc":"2026-09-12T13:05:12.000Z"}}

### 7.4 Sink contract

Every implementation must provide a sink interface with a single method that
receives events. The library must ship at minimum:

- A **console sink** (JSON Lines to stdout)
- A **file sink** (JSON Lines to daily rotating file)
- A **null sink** (silent)
- A **composite sink** (fan-out)

Any additional sinks (Datadog, OpenTelemetry, Application Insights, etc.) are
implementation-specific.
---

## 8. Metrics

### 8.1 Metric names

Every implementation must expose these metric names, per policy. Names are
**exactly** as shown (snake_case):

| Metric | Type | Meaning |
|--------|------|---------|
| `total_calls` | counter | Cumulative call count (never reset) |
| `failed_calls` | counter | Cumulative failed call count |
| `error_rate` | gauge | `failed_calls / total_calls` (0.0 to 1.0) |
| `p50_ms` | gauge | 50th percentile latency over rolling window |
| `p95_ms` | gauge | 95th percentile latency over rolling window |
| `p99_ms` | gauge | 99th percentile latency over rolling window |
| `avg_ms` | gauge | Mean latency over rolling window |
| `in_flight` | gauge | Currently executing operations |

### 8.2 Percentile algorithm

Percentiles use **linear interpolation** (R-7 method). For a sorted window of `N`
samples and percentile `p` (0.0 to 1.0):

    rank  = p * (N - 1)
    lower = floor(rank)
    upper = ceil(rank)
    if lower == upper:
        result = sorted[lower]
    else:
        weight = rank - lower
        result = sorted[lower] * (1 - weight) + sorted[upper] * weight

### 8.3 Rolling window

The metric sink must keep the last N samples per policy for percentile
computation. **N = 1000** by default, configurable.

The window is a **count-based** ring buffer, not time-based. Older samples are
discarded as new ones arrive.

Cumulative counters (`total_calls`, `failed_calls`) are **not** windowed — they
count every call since process start.

### 8.4 When metrics are recorded

- On **success** (including after retries): one sample with the total duration
  of the operation, `success = true`.
- On **failure**: one sample with the total duration, `success = false`.
- On **fallback used**: the operation is recorded as a failure (the primary path
  failed), but the caller sees the fallback's value.

In-flight counts are incremented at the start of `ExecuteAsync` and decremented
in a `finally` block.

---

## 9. Correlation

### 9.1 Header name

The HTTP header for correlation IDs is **exactly** `X-Correlation-Id`. This is
a cross-service contract — it must not vary by language or service.

### 9.2 Propagation rules

- **Inbound:** if the request carries `X-Correlation-Id`, adopt it as the ambient
  correlation ID.
- **If absent:** generate a new ID (32-character lowercase hex, no dashes, UUID-v4
  shape) and adopt it.
- **Outbound HTTP:** every outbound call **must** propagate the ambient correlation
  ID in the `X-Correlation-Id` header.
- **Response:** the service's response **must** include the correlation ID in the
  `X-Correlation-Id` header, echoing what the request contained.

### 9.3 Storage mechanism

Every implementation must provide **ambient** correlation storage that survives
async boundaries (`await` in C#, `async`/`await` in Python, goroutine context in
Go). The specific primitive is language-dependent:

- C#: `AsyncLocal<string?>`
- Python: `contextvars.ContextVar`
- Go: `context.Context` value

**Test requirement:** concurrent async flows must not see each other's correlation
IDs.

---

## 10. Fallback

### 10.1 Fallback resolution order

When the pipeline fails, the executor resolves a fallback in this order:

1. **Per-call fallback** — passed as the `fallback` argument to `ExecuteAsync`.
2. **Policy fallback** — if `PolicyDefinition.Fallback.Enabled = true` and the
   policy defines a static fallback value. *Future* — not implemented in v0.5.0.
3. **No fallback** — rethrow the last exception.

Per-call always wins.

### 10.2 Fallback is outside the pipeline

The fallback runs **outside** retry, circuit, and timeout. It receives the caller's
`CancellationToken` directly. It is **not** subject to retries, circuit gating, or
its own timeout by default.

If the fallback itself needs resilience, wrap it explicitly.

### 10.3 Fallback failure

If the fallback throws, the executor must:

1. Log the original pipeline failure (already logged via `call_failed`).
2. Emit `fallback_used` is **not** emitted (fallback did not produce a value).
3. Throw a single `ResilienceException` with:
   - `Category` inherited from the original failure
   - `Metadata["fallback_error"]` = fallback exception message
   - `InnerException` = fallback exception

---

## 11. Response envelope ownership

This is the **most important non-obvious rule.** It defines what is shared vs.
per-service.

### 11.1 Shared (from the library)

Every implementation **must** guarantee:

- The `ResilienceException` type name and field names
- The `ResilienceErrorCategory` enum values
- The `X-Correlation-Id` header name
- Structured event type names and field names
- Metric names

### 11.2 NOT shared (owned by each service)

The following are **explicitly per-service**. The library **must not** impose a
single response envelope shape:

- HTTP status code mapping (a 500 for one service, a 503 for another)
- Error response body schema
- Error code strings (`"upstream_unavailable"` vs `"UPSTREAM_TIMEOUT"`)
- Consumer-facing error messages
- Pagination, DTO shapes, API versioning
- Authentication scheme
- Route conventions

### 11.3 Why this matters

An early design mistake is to force every service into a single response envelope
so that "clients can consume errors uniformly." This fails because:

- Different services have different consumers with different needs.
- A single envelope couples every service's API evolution to every other service.
- Consumer-visible messages are product decisions, not infrastructure decisions.

**The library shares the exception type. Each service renders it into whatever
shape makes sense for its consumers.**

Implementations **must** provide an abstract middleware (or equivalent) that
services inherit to render their own errors. The middleware handles:
- Catching `ResilienceException`
- Setting `X-Correlation-Id` on the response
- Structured logging

The service implements a single abstract method to render the body.

---

## 12. Rate limiter

### 12.1 Purpose

Cap how many calls may proceed in a given time period. Calls beyond the cap are
rejected with a `ResilienceException` carrying `RejectionCategory` (default
`Transient`), or — if a queue is configured — they wait briefly for capacity.

The rate limiter is the **outermost** pipeline layer. A rejected call does not
consume a retry slot, and a retry does not multiply the load on a rate-limited
dependency.

### 12.2 Strategies

Four strategies. Each interprets `PermitLimit` and `WindowSeconds` differently:

| Strategy | PermitLimit | WindowSeconds |
|----------|-------------|---------------|
| `TokenBucket` | Bucket capacity | Refill period (seconds) |
| `SlidingWindow` | Max calls in any rolling window | Window size (seconds) |
| `FixedWindow` | Max calls per fixed bucket | Bucket size (seconds) |
| `ConcurrencyLimit` | Max simultaneous calls | Ignored |

**TokenBucket.** Tokens refill continuously at rate `PermitLimit / WindowSeconds`
per second. Each call consumes one token. The bucket holds at most
`PermitLimit` tokens.

**SlidingWindow.** At most `PermitLimit` calls may occur in any rolling
`WindowSeconds` window. No boundary effects.

**FixedWindow.** At most `PermitLimit` calls may occur per fixed `WindowSeconds`
bucket. Buckets are aligned to the moment the first call arrived. Callers may
observe up to `2 * PermitLimit` calls across a bucket boundary.

**ConcurrencyLimit.** At most `PermitLimit` calls may be in flight
simultaneously. No window is used; `WindowSeconds` is ignored.

### 12.3 Queue behavior

When a call would be rejected, the limiter may let it wait, capped by
`QueueLimit` and `QueueTimeoutMs`.

| Strategy | Queue mechanism | Ordering |
|----------|-----------------|----------|
| `TokenBucket` | Wait until the next token would refill | Best-effort |
| `SlidingWindow` | Wait until the oldest call leaves the window | Best-effort |
| `FixedWindow` | Wait until the current bucket rolls over | Best-effort |
| `ConcurrencyLimit` | Semaphore wait with timeout | FIFO |

`QueueLimit = 0` (the default) rejects immediately without queuing.

### 12.4 Configuration keys

Binding from `IConfiguration` (snake_case in JSON, PascalCase in C# — bound
case-insensitively by the .NET binder):

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `enabled` | bool | `false` | When false, pass-through |
| `strategy` | string | `SlidingWindow` | One of the four strategy names |
| `permit_limit` | int | `100` | The limit |
| `window_seconds` | int | `60` | Window; ignored for `ConcurrencyLimit` |
| `queue_limit` | int | `0` | Max waiting calls; 0 rejects immediately |
| `queue_timeout_ms` | int | `5000` | Max wait time for a queued call |
| `rejection_category` | string | `Transient` | Error category on rejection |

### 12.5 Event fields

A `rate_limited` event is emitted on rejection. Fields (per §7.2 conventions):

| Field | Type | Notes |
|-------|------|-------|
| `event_type` | string | `"rate_limited"` |
| `policy_name` | string | The policy name |
| `correlation_id` | string? | From `CorrelationContext` |
| `timestamp_utc` | ISO-8601 | UTC |
| `metadata.strategy` | string | `TokenBucket` / `SlidingWindow` / `FixedWindow` / `ConcurrencyLimit` |
| `metadata.permit_limit` | int | Configured `PermitLimit` |
| `metadata.window_seconds` | int? | Omitted for `ConcurrencyLimit` |
| `metadata.queue_limit` | int | Configured `QueueLimit` |
| `metadata.queue_depth` | int | Queue depth at rejection; `0` when `QueueLimit = 0` |
| `metadata.reason` | string | `rejected_immediately` / `queue_timeout` / `queue_full` |

### 12.6 Error category on rejection

The exception carries the policy's configured `RejectionCategory` (default
`Transient`). The reason is always present in `metadata.reason`.

### 12.7 Associated files

- `src/Portfolio.Resilience/Policies/RateLimiterPolicyBuilder.cs`
- `src/Portfolio.Resilience/Configuration/RateLimiterOptions.cs`
- `src/Portfolio.Resilience/Configuration/RateLimitStrategy.cs`
- `src/Portfolio.Resilience/Events/ResilienceEventType.cs` (value `RateLimited = 9`)

---

## 13. Bulkhead

### 13.1 Purpose

Cap how many calls may run concurrently against a resource. Calls beyond the cap
may wait in a bounded queue; calls beyond the queue are rejected with a
`ResilienceException` carrying `RejectionCategory` (default `Transient`).

The bulkhead is the **second** pipeline layer, outside retry. It caps
concurrency before the retry loop multiplies load.

### 13.2 Algorithm

A semaphore sized to `MaxConcurrency` gates the operation. The semaphore is held
**across** the operation and released in a `finally` block — so slots are
returned whether the operation succeeds, fails, or is cancelled.

When the semaphore is exhausted, the caller may wait in a bounded queue:

| Condition | Behavior |
|-----------|----------|
| A slot is free | Acquire immediately |
| No slot, `MaxQueue = 0` | Reject with `rejected_immediately` |
| No slot, queue has room | Wait up to `QueueTimeoutMs` |
| No slot, queue full | Reject with `queue_full` |
| Waited `QueueTimeoutMs`, still no slot | Reject with `queue_timeout` |

Waiter ordering is FIFO (native `SemaphoreSlim` behavior).

### 13.3 Configuration keys

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `enabled` | bool | `false` | When false, pass-through |
| `max_concurrency` | int | `20` | Max simultaneous calls |
| `max_queue` | int | `100` | Max callers that may wait |
| `queue_timeout_ms` | int | `5000` | Max wait time for a queued caller |
| `rejection_category` | string | `Transient` | Error category on rejection |

### 13.4 Event fields

A `bulkhead_rejected` event is emitted on rejection.

| Field | Type | Notes |
|-------|------|-------|
| `event_type` | string | `"bulkhead_rejected"` |
| `policy_name` | string | The policy name |
| `correlation_id` | string? | From `CorrelationContext` |
| `timestamp_utc` | ISO-8601 | UTC |
| `metadata.max_concurrency` | int | Configured `MaxConcurrency` |
| `metadata.max_queue` | int | Configured `MaxQueue` |
| `metadata.queue_depth` | int | Waiters at the moment of rejection |
| `metadata.reason` | string | `rejected_immediately` / `queue_full` / `queue_timeout` |

### 13.5 Error category on rejection

The exception carries the policy's configured `RejectionCategory` (default
`Transient`). The reason is always present in `metadata.reason`.

### 13.6 Associated files

- `src/Portfolio.Resilience/Policies/BulkheadPolicyBuilder.cs`
- `src/Portfolio.Resilience/Configuration/BulkheadOptions.cs`
- `src/Portfolio.Resilience/Events/ResilienceEventType.cs` (value `BulkheadRejected = 10`)

---
## 14. Policy composition

### 14.1 Purpose

Build a resilience pipeline in any order, from any subset of available layers.
Where the default pipeline is fixed at

    RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout -> Operation

composition lets a caller choose. Every policy builder implements
`IResiliencePolicy`, and a `ResiliencePipeline` executes an ordered list of
layers outermost-first.

### 14.2 The `IResiliencePolicy` interface

    Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        PolicyDefinition definition,
        CancellationToken ct = default);

Implementations:

- Read their own options from `definition`.
- Pass through to `operation` without behavior when disabled.
- Must be thread-safe.

### 14.3 `ResiliencePipeline`

- Constructed from an ordered array of `IResiliencePolicy`.
- Executes outermost-first: `Wrap(A, B, C)` produces `A -> B -> C -> operation`.
- Immutable after construction. Thread-safe.
- Exposes `Layers` as a read-only view.
- `Wrap(...)` is the factory name; it is identical to the constructor.

### 14.4 `ResiliencePipelineBuilder`

- Fluent, mutable builder for a `ResiliencePipeline`.
- Methods: `WithName(string)`, `Add(IResiliencePolicy)`,
  `AddIf(bool, IResiliencePolicy)`, `Build()`.
- Not thread-safe. Build once, share the resulting pipeline.
- `Build()` throws `InvalidOperationException` when no layers have been added.

### 14.5 Relationship to `CompositePolicyBuilder`

`CompositePolicyBuilder` retains the default pipeline order for compatibility
and continues to **fail loud** when a policy enables a feature whose builder
was not provided. Internally it composes the layer list and delegates to
`ResiliencePipeline`.

`ResiliencePipeline` itself does **not** validate that all features enabled in
the definition are present in the pipeline. A layer that is not in the
pipeline is not executed — this is the documented behavior of the pipeline
container.

### 14.6 Custom layers

Any type implementing `IResiliencePolicy` may be composed. The library
imposes no minimum interface beyond what is defined in §14.2. Custom layers
should honor the caller's `CancellationToken` and must not call
`operation` more than once.

### 14.7 Associated files

- `src/Portfolio.Resilience/Abstractions/IResiliencePolicy.cs`
- `src/Portfolio.Resilience/Policies/ResiliencePipeline.cs`
- `src/Portfolio.Resilience/Policies/ResiliencePipelineBuilder.cs`
- `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs`

---
## 15. OpenTelemetry integration

### 15.1 Purpose

Export resilience events and metrics as native OpenTelemetry telemetry. The
integration is optional: the core library remains zero-dependency, and the
OTel sinks live in a separate package, `Portfolio.Resilience.OpenTelemetry`.

### 15.2 Log sink

`OpenTelemetryLogSink` implements `ILogSink`. It emits one OTel log record per
`ResilienceEvent`.

**Attribute names** (all prefixed `resilience.`):

| Attribute | Source field | When present |
|-----------|--------------|--------------|
| `resilience.event_type` | `EventType` (snake_case) | Always |
| `resilience.policy_name` | `PolicyName` | Always |
| `resilience.timestamp_utc` | `TimestampUtc` | Always |
| `resilience.correlation_id` | `CorrelationId` | When non-null |
| `resilience.attempt` | `Attempt` | When non-null |
| `resilience.duration_ms` | `DurationMs` | When non-null |
| `resilience.error_category` | `ErrorCategory` | When non-null |
| `resilience.error_message` | `ErrorMessage` | When non-null |
| `resilience.error_type` | `ErrorType` | When non-null |
| `resilience.metadata.<key>` | `Metadata[key]` | One per entry |

**Log level mapping:**

| Event type | Log level |
|------------|-----------|
| `call_started` | `Debug` |
| `call_succeeded` | `Information` |
| `call_failed` | `Error` |
| `retry_attempted` | `Warning` |
| `circuit_opened` | `Warning` |
| `circuit_closed` | `Information` |
| `circuit_half_opened` | `Information` |
| `fallback_used` | `Information` |
| `timeout_breached` | `Warning` |
| `rate_limited` | `Warning` |
| `bulkhead_rejected` | `Warning` |

The sink does **not** short-circuit on `ILogger.IsEnabled`. Filtering is the
logger pipeline's responsibility, not the sink's.

### 15.3 Metric sink

`OpenTelemetryMetricSink` implements `IMetricSink`. Each `RecordCall` invocation
produces three instrument recordings:

| Instrument | Type | Unit | Tags |
|------------|------|------|------|
| `resilience.call.duration_ms` | Histogram (double) | `ms` | `policy_name`, `success` |
| `resilience.call.succeeded_total` | Counter (long) | `{call}` | `policy_name`, `attempts` |
| `resilience.call.failed_total` | Counter (long) | `{call}` | `policy_name`, `attempts` |

**In-flight tracking is not exported.** The `IMetricSink` interface exposes only
`RecordCall`; the in-flight gauge on `InMemoryMetricSink` is outside the
interface.

### 15.4 Package boundary

The OTel integration ships as a separate NuGet package. It depends on:

- `Portfolio.Resilience` (the core)
- `OpenTelemetry.Api`
- `Microsoft.Extensions.Logging.Abstractions`

The core `Portfolio.Resilience` package is unaffected. It remains zero
third-party dependencies.

### 15.5 DI registration

Two entry points are provided:

- `IServiceCollection.AddPortfolioResilienceOpenTelemetry(string? meterName)`
  - Composes the OTel sinks with any existing `ILogSink` / `IMetricSink`
    registration.
  - Order-independent relative to `AddPortfolioResilience`.
- `ResilienceBuilder.AddOpenTelemetrySinks(ILoggerFactory, Meter?)`
  - Directly adds the sinks to the builder.
  - For callers who own the logger factory and meter.

### 15.6 Associated files

- `src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryLogSink.cs`
- `src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryMetricSink.cs`
- `src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryBuilderExtensions.cs`

---
## 16. Hedging

### 16.1 Purpose

Fire parallel attempts of the same operation with a stagger delay between
them, and return the first successful result. A latency optimization, not a
reliability one. Retry handles transient failures; hedging handles tail
latency.

**Safety:** hedging is **not safe for non-idempotent operations** unless the
downstream provider deduplicates on an idempotency key. A hedged
`POST /charge` can create two charges. Implementations must warn when a policy
enables hedging.

### 16.2 The race

1. Attempt 0 (the "primary") fires immediately.
2. Before firing attempt N, wait `delay(N)` milliseconds. During the wait, if
   any prior attempt has already succeeded, the hedge does not fire.
3. If the delay elapses without a winner, attempt N fires. It runs in parallel
   with the still-pending attempts.
4. The first successful attempt wins. The others are either cancelled
   (`CancelOnSuccess = true`, default) or allowed to complete.

### 16.3 Delay calculation

For attempt index N (0-based):

| `ExponentialBackoff` | `delay(N)` |
|----------------------|------------|
| `false` (default) | `DelayMs` for N >= 1; 0 for N = 0 |
| `true` | `DelayMs * 2^(N-1)` for N >= 1; 0 for N = 0 |

### 16.4 Configuration keys

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `enabled` | bool | `false` | When false, hedging is a pass-through |
| `max_attempts` | int | `2` | Total attempts including the primary |
| `delay_ms` | int | `100` | Base stagger delay |
| `exponential_backoff` | bool | `false` | Doubles the delay per attempt |
| `attempt_timeout_ms` | int | `0` | Per-attempt ceiling; 0 = none |
| `cancel_on_success` | bool | `true` | Cancel losers when a winner succeeds |
| `emit_attempt_events` | bool | `true` | Emit hedge_won / hedge_lost / hedge_cancelled |
| `rejection_category` | string | `Transient` | Category when all attempts fail |

### 16.5 Events

Three event types are added at values 11, 12, and 13:

| Value | Event type | Meaning |
|-------|------------|---------|
| 11 | `hedge_won` | The winning attempt succeeded |
| 12 | `hedge_lost` | A loser completed after the winner |
| 13 | `hedge_cancelled` | A loser was cancelled by the winner |

**Event fields** (in addition to §7.2):

| Field | Type | Notes |
|-------|------|-------|
| `attempt` | int | 1-based attempt number (primary = 1) |
| `duration_ms` | float? | Present on `hedge_won` and `hedge_lost` |

### 16.6 Failure semantics

When all attempts fail, the exception thrown is the one from the **first**
attempt. Other attempts' failures are attached to the exception's
`Data` dictionary:

- `Data["hedge_attempt_N"]` — the N-th attempt's error message (string)
- `Data["hedge_attempt_N_exception"]` — the N-th attempt's exception (Exception)

This preserves the original exception type for `catch` clauses while retaining
full diagnostic information.

### 16.7 Validation warnings

Implementations must warn (not throw) at policy resolve for:

| Condition | Warning |
|-----------|---------|
| `max_attempts <= 0` | "MaxAttempts must be greater than 0" |
| `max_attempts > 5` | "MaxAttempts = N is aggressive; consider 2-3" |
| `delay_ms < 0` | "DelayMs must not be negative" |
| `attempt_timeout_ms < 0` | "AttemptTimeoutMs must not be negative" |
| `attempt_timeout_ms > 0` and `< delay_ms` | "AttemptTimeoutMs is shorter than DelayMs; hedged attempts may never fire" |

**In addition**, implementations must warn once per policy when
`hedging.enabled` is true:

    Policy 'X': Hedging is enabled. Ensure the operation is idempotent or
    carries an idempotency key, or set Hedging.Enabled = false.

### 16.8 Associated files

- `src/Portfolio.Resilience/Policies/HedgingPolicyBuilder.cs`
- `src/Portfolio.Resilience/Configuration/HedgingOptions.cs`
- `src/Portfolio.Resilience/Events/ResilienceEventType.cs` (values 11, 12, 13)

---
## 17. Analyzers

### 17.1 Purpose

Ship Roslyn analyzers that warn at compile time when a code pattern almost
certainly bypasses the resilience pipeline, or when a policy is configured
with a value that cannot be valid. The analyzers are optional: they live in a
separate package, `Portfolio.Resilience.Analyzers`, and do not affect the
runtime behavior of the core library.

### 17.2 Rules

| Rule ID | Severity | Category | Detects |
|---------|----------|----------|---------|
| `PR0001` | Warning | Reliability | An `HttpClient` obtained from `IHttpClientFactory` is called directly, bypassing the resilience pipeline |
| `PR0002` | Warning | Reliability | A policy enables a feature but sets a companion value to `0` or negative |

Both rules are enabled by default and suppressible via `#pragma warning
disable` or `.editorconfig`.

### 17.3 PR0001 - HttpClient bypass

**Detection:**

1. Locate a field or property of type `System.Net.Http.HttpClient`.
2. Recognize an assignment from `IHttpClientFactory.CreateClient(...)`,
   either in a field initializer or in a constructor body.
3. Detect a call to any of: `SendAsync`, `GetAsync`, `PostAsync`,
   `PutAsync`, `DeleteAsync`, `PatchAsync`, `GetStringAsync`,
   `GetByteArrayAsync`, `GetStreamAsync`, `GetFromJsonAsync`,
   `PostAsJsonAsync`, `PutAsJsonAsync`, `DeleteFromJsonAsync` on that field.
4. Emit a diagnostic if the enclosing class has no field, property, or
   constructor parameter of type
   `Portfolio.Resilience.Abstractions.IResilienceExecutor`.

**Why the `IResilienceExecutor` guard:** a class that already depends on the
executor knows about resilience. The guard eliminates false positives.

### 17.4 PR0002 - Policy misconfiguration

**Detection:**

1. Find an invocation of `AddPolicy(string, Action<PolicyDefinition>)` on a
   `ResilienceBuilder`.
2. Collect every assignment inside the lambda's body where the left-hand side
   is a chain of the form `<param>.<Options>.<Property>` and the right-hand
   side is a literal number or boolean.
3. Emit a diagnostic when an `Enabled = true` assignment and a companion
   value assignment for the same feature appear in the same lambda and the
   companion value is `<= 0`.
4. Separately, emit a diagnostic when a negative value is assigned to
   `Timeout.TimeoutMs` or `Retry.MaxAttempts`.

**Companion pairs:**

| Feature | Enabled key | Companion key | Rule |
|---------|-------------|---------------|------|
| Rate limiter | `RateLimiter.Enabled` | `RateLimiter.PermitLimit` | `> 0` |
| Bulkhead | `Bulkhead.Enabled` | `Bulkhead.MaxConcurrency` | `> 0` |
| Hedging | `Hedging.Enabled` | `Hedging.MaxAttempts` | `> 0` |
| Timeout | - | `Timeout.TimeoutMs` | `>= 0` |
| Retry | - | `Retry.MaxAttempts` | `>= 0` |

**Limits:** the rule reads literal values only. Values loaded from
configuration are validated at runtime by the options classes' `Validate`
methods, not by the analyzer.

### 17.5 Associated files

- `src/Portfolio.Resilience.Analyzers/HttpClientBypassAnalyzer.cs`
- `src/Portfolio.Resilience.Analyzers/MisconfigurationAnalyzer.cs`
- `src/Portfolio.Resilience.Analyzers/AnalyzerReleases.Unshipped.md`

---
## Appendix A — Versioning

This spec follows semantic versioning:

- **Patch** (`0.5.x`): editorial clarifications, no behavior change
- **Minor** (`0.x.0`): additive changes, existing implementations remain valid
- **Major** (`x.0.0`): breaking changes; every implementation must bump

Every implementation's `CHANGELOG.md` **must** record which SPEC version it
targets.

**Current:** SPEC `0.6.0`. Implementations compatible with SPEC `0.6.0` must
declare it in their README.

## Appendix B — Testing requirements

Every implementation **must** include tests covering:

- Retry attempt counting for `MaxAttempts = 0, 1, 2, 3`
- Backoff formula for `n = 1..5` (via a fake clock or injectable RNG)
- Jitter bounds over 100 iterations (all delays within `[base, base + jitter]`)
- Circuit transitions: Closed → Open → HalfOpen → Closed, and HalfOpen → Open
- Circuit rejects while Open without invoking the operation
- Circuit HalfOpen allows exactly one probe
- Classification of HTTP codes, SQLSTATEs, and exception type names
- Correlation ID flows across async boundaries and is isolated per flow
- Fallback invoked when pipeline fails; fallback error wrapped correctly
- Event field names match §7.2 exactly

Deviations from the spec **must** be documented in the implementation's README,
with a version bump if any behavior changes.

---

## 18. Idempotency key propagation

### 18.1 Purpose

Attach a stable idempotency key to **every attempt** of an operation - the
primary call, every retry, and every hedged attempt - so that downstream
services that deduplicate on the key observe a single logical write.

**This applies to any operation with the property "running it twice produces
two effects"** - charges, order creation, event publishing, notification
sends - not only to payments. Payments are the strictest case, so an
implementation that satisfies this section for payments satisfies it for the
softer cases.

### 18.2 Resolution order

The effective key for a call is resolved in this order. First non-null wins.

1. **Explicit parameter.** `ExecuteAsync(..., idempotencyKey: "order-12345")`.
2. **Ambient key.** A value already present in the ambient idempotency store.
3. **Auto-derived from correlation ID.** `"idem-" + correlation_id`.
4. **Auto-derived from a fresh GUID.** `"idem-" + uuid4_hex`.

The resolution is stable for the duration of the call: the same key is visible
to every retry and every hedged attempt.

### 18.3 Ambient storage

The idempotency key is stored in an ambient, async-safe primitive that survives
await boundaries and is isolated between concurrent flows. The primitive is
language-dependent:

- C#: `AsyncLocal<string?>`
- Python: `contextvars.ContextVar`
- Go: `context.Context` value

The push/pop semantics must be nested: pushing a new key inside an existing
scope restores the previous value on pop.

### 18.4 HTTP header

When an HTTP client is used inside the pipeline, the ambient key is emitted as
an HTTP header on **every request the handler sends for this call** - the
primary request and every retried request.

- **Default header name:** `Idempotency-Key`.
- **Configurable** per HTTP client. Implementations must allow overriding the
  header name for providers with a different convention.

The header must be present on every attempt, and the value must be identical
across attempts for one logical call.

### 18.5 Configuration keys

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `idempotency_header_name` | string | `Idempotency-Key` | Header name used by the HTTP handler |

### 18.6 Event fields

No new event types are introduced. The key is not exposed in event metadata by
default - it belongs to the operation, not to the resilience layer. Callers
who want the key in their audit trail should add it to their own application
logs.

### 18.7 Failure semantics

- If the header name is null or whitespace, the implementation must log a
  warning and default to `Idempotency-Key` at runtime. It must not throw.
- If the ambient store is unavailable (not applicable in C#), the
  implementation must skip header emission rather than crash.
- If the explicit parameter is null or whitespace, the implementation must
  treat it as "not provided" and fall through to the next resolution step.

### 18.8 Associated files

- `src/Portfolio.Resilience/Correlation/IdempotencyContext.cs`
- `src/Portfolio.Resilience/Configuration/HttpClientOptions.cs`
- `src/Portfolio.Resilience/HttpClient/ResilientHttpMessageHandler.cs`

---

## 19. PCI-safe event scrubbing

### 19.1 Purpose

Redact sensitive patterns from every `ResilienceEvent` **before any log sink
sees it**. This applies to any service that handles sensitive data - card
numbers, PII, tokens, API keys - not only to payments.

### 19.2 The `IEventScrubber` contract

    ResilienceEvent Scrub(ResilienceEvent evt)

- Returns a **new** event instance; never mutates the input.
- Must be thread-safe.
- Must not throw. A thrown exception propagates to the emitter and can break
  the pipeline's logging path.

### 19.3 Default scrubber

Implementations **must** ship a default scrubber that masks at minimum:

| Family | Pattern | Example |
|--------|---------|---------|
| PAN | 13-19 consecutive digits, optionally separated by single spaces or dashes | `4111111111111111`, `4111-1111-1111-1111` |
| CVV/CVC | 3-4 digits adjacent to the tokens `cvv` or `cvc` (case-insensitive) | `cvv 123`, `CVC: 4567` |
| SSN | US Social Security Number in canonical `ddd-dd-dddd` format | `123-45-6789` |

Matches are replaced with the literal token `[REDACTED]`.

### 19.4 Scope of scrubbing

The scrubber **must** apply to:

- `ResilienceEvent.ErrorMessage`
- `ResilienceEvent.ErrorType`
- Every string value in `ResilienceEvent.Metadata`

Non-string metadata values **must** pass through unchanged.

### 19.5 Where scrubbing runs

Scrubbing runs **once per event**, before the sink fan-out. Every sink
receives the scrubbed copy - no sink ever sees the original.

When scrubbing is enabled and a single sink is registered, the implementation
**must** still route events through the scrubber. The composite-sink wrapper
exists for exactly this case; it is not merely a fan-out mechanism.

### 19.6 Configuration key

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `scrub_sensitive_data` | bool | `false` | When true (on any policy), installs the default scrubber globally |

The flag is a **signal** that at least one policy needs scrubbing. Once the
flag is true anywhere, the implementation **must** scrub every event -
including events from policies that did not opt in.

### 19.7 Custom scrubbers

Implementations **must** expose the `IEventScrubber` interface as a public
extension point. A custom scrubber replaces the default one entirely.

### 19.8 What this section does NOT guarantee

- That sensitive data is not present in events (the library cannot inspect
  operation internals).
- That all locale-specific identity formats are masked.
- That non-string metadata values are redacted.

The scrubber is a **last line of defense**, not a compliance guarantee.

### 19.9 Associated files

- `src/Portfolio.Resilience/Abstractions/IEventScrubber.cs`
- `src/Portfolio.Resilience/Sinks/DefaultPciScrubber.cs`
- `src/Portfolio.Resilience/Sinks/CompositeLogSink.cs`
- `src/Portfolio.Resilience/Configuration/LoggingOptions.cs`

---

## 20. Time budget propagation

### 20.1 Purpose

Enforce a **total wall-clock budget** for an operation across every layer of
the pipeline. The budget spans the initial call, every retry, and every hedge.
When the budget expires, layers stop adding new work and surface the last
failure.

**This applies to any operation with the property "the caller has an SLA"** -
payments (3s end-to-end), search (500ms), user-facing reads, or any path with
a scheduler-driven deadline - not only to payments.

### 20.2 Ambient storage

The budget is stored in an ambient, async-safe primitive that survives await
boundaries and is isolated between concurrent flows. The primitive is
language-dependent:

- C#: `AsyncLocal<DateTime?>` holding an absolute deadline
- Python: `contextvars.ContextVar`
- Go: `context.Context` value

The **absolute deadline** is computed at the moment the executor pushes the
budget. Remaining time is `deadline - now`, evaluated at each layer's
consultation point.

### 20.3 Resolution semantics

A call's budget is set by the caller. When null, no budget scope exists and
every layer must behave as if the feature is not present. Implementations
**must not** invent a default budget.

### 20.4 Layer behavior

Every layer that can multiply wall-clock time **must** consult the budget:

| Layer | Behavior when budget is active |
|-------|-------------------------------|
| Retry | Do not start a new attempt if `delay + floor` exceeds remaining budget |
| Timeout | Use `min(configured_timeout, remaining_budget)` as the effective ceiling |
| Hedging | Do not fire a new hedge if `delay + attempt_timeout` exceeds remaining budget |
| Circuit | No change - delegates to the inner timeout |

The **floor** for retry is implementation-defined; a reasonable choice is
`base_delay_ms`. The floor ensures the next attempt has a minimum runtime
budget rather than running with zero time.

### 20.5 Disabled timeout interaction

When `timeout.timeout_ms <= 0` (disabled) **and** a budget is active, the
budget becomes the effective per-attempt ceiling. This is deliberate: the
caller's SLA is non-negotiable even when the policy disables its own
per-attempt timeout.

When `timeout.timeout_ms <= 0` and **no budget** is active, behavior is
unchanged - the operation runs to completion or until the caller's own token
fires.

### 20.6 Exhausted budget at entry

If the budget is already exhausted when a layer consults it:

- **Timeout** throws `ResilienceException(Timeout)` immediately with
  `metadata.remaining_ms` recording the (non-positive) remaining value.
- **Retry** rethrows the last exception without scheduling another attempt.
- **Hedging** skips the hedge and waits for in-flight attempts to resolve.

### 20.7 Event fields

No new event types are introduced. When a retry is stopped by budget
exhaustion, the emitted `call_failed` event carries the last error's category
as usual. The budget itself is not emitted as a separate event.

### 20.8 Configuration key

| Key | Type | Default | Meaning |
|-----|------|---------|---------|
| `time_budget_ms` | int? | `null` | Total wall-clock budget for the call. Null disables the feature |

The key is a **call-site parameter**, not a policy setting. The same policy
may be called with different budgets from different call sites.

### 20.9 Associated files

- `src/Portfolio.Resilience/Correlation/TimeBudgetContext.cs`
- `src/Portfolio.Resilience/Implementation/ResilienceExecutor.cs`
- `src/Portfolio.Resilience/Policies/RetryPolicyBuilder.cs`
- `src/Portfolio.Resilience/Policies/TimeoutPolicyBuilder.cs`
- `src/Portfolio.Resilience/Policies/HedgingPolicyBuilder.cs`

---

## 21. Payment-safe pipeline preset

### 21.1 Purpose

Provide a **named factory** that returns a `ResiliencePipeline` with a fixed
safe layer order for critical write paths. The preset is intended for any
operation where the property "an accidental multi-attempt race is harmful"
holds - charges, refunds, order creation, event publishing, notification
sends - not only for payments.

### 21.2 The safe order

    RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation

Compared to the default pipeline order

    RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

the preset moves **Circuit outside Hedging and Retry**. A circuit rejection
therefore never spawns additional attempts.

### 21.3 Enforces order, not enablement

The preset **must** include all six layers unconditionally. Each layer reads
its own flag from the `PolicyDefinition` and passes through when disabled:

- `RateLimiter.Enabled = false` → rate limiter passes through
- `Bulkhead.Enabled = false` → bulkhead passes through
- `Hedging.Enabled = false` → hedging passes through
- `Retry.MaxAttempts = 0` → retry runs the operation once
- `Timeout.TimeoutMs <= 0` → timeout is disabled
- Circuit has no `Enabled` flag; it is always active

The preset **must not** conditionally include or exclude layers based on
`PolicyDefinition` - inclusion is fixed by the factory, and each layer's
runtime behavior is what honors the flags.

### 21.4 API shape

    ResiliencePipeline.WithPaymentSafeDefaults()
    ResiliencePipeline.WithPaymentSafeDefaults(
        RateLimiterPolicyBuilder? rateLimiter = null,
        BulkheadPolicyBuilder? bulkhead = null,
        CircuitPolicyBuilder? circuit = null,
        HedgingPolicyBuilder? hedging = null,
        RetryPolicyBuilder? retry = null,
        TimeoutPolicyBuilder? timeout = null)

Any null argument **must** be replaced with a fresh instance so callers can
customize a subset of layers without supplying the rest.

### 21.5 When to use

**Recommended** for any non-idempotent write path with a critical SLA.

**Not recommended** for general reads. The default pipeline is the right
choice for reads - it places hedging before retry, which is the desired
behavior when the operation is safe to duplicate.

### 21.6 Associated files

- `src/Portfolio.Resilience/Policies/ResiliencePipeline.cs`
- `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` (default order)
- `src/Portfolio.Resilience/Abstractions/IResiliencePolicy.cs`
