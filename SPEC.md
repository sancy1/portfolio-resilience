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
