<!--
filepath: docs/rate-limiter.md
package:  Portfolio.Resilience | since: v0.6.0
purpose:  Explains the rate limiter - four strategies, queue behavior, and rejection metadata.
-->

# Rate Limiter

## What it is

A rate limiter **caps how many calls may proceed in a given time period**. Calls
beyond the cap are rejected immediately (or, if a queue is configured, they wait
briefly for capacity to free up).

The rate limiter is the **outermost** layer in the pipeline:

    Caller -> RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout -> Operation

It sits outside retry on purpose: a rejected call does not consume a retry slot,
and a retry does not multiply the load on a rate-limited dependency.

## Why it exists

Every dependency has a capacity. When a service sends more requests than the
dependency can absorb:

- The dependency slows down, then fails.
- Failed requests get retried, multiplying the load.
- A minor overload becomes a cascading outage.

The rate limiter enforces the cap **at the caller**, before the requests leave
your process. If you have a contractual limit of 100 requests per minute against
an external API, the rate limiter enforces it locally instead of relying on the
remote service to reject you.

Without a rate limiter:
- A burst of traffic (a batch job, a retry storm, a load spike) slams the
  dependency.
- The dependency's own rate limiter returns 429s, which your retry policy may
  then retry, making it worse.
- You may violate a contractual SLA with a third-party API.

With a rate limiter:
- Load is smoothed to a configured ceiling.
- The dependency sees a predictable request rate.
- The caller gets a fast, local rejection instead of a slow remote one.

## When you need it

**Yes:**
- Outbound calls to a **third-party API** with a published rate limit
  (Stripe, OpenAI, GitHub, etc.)
- Calls to a **shared internal service** that is resource-constrained
- **Background jobs** or batch processors that must not overwhelm a downstream
- Any dependency where **429 responses** are a real risk

**No:**
- Calls to a dependency that never rate-limits and has ample capacity
- In-process operations (parsing, math, local logic)
- Operations where every request is already serialized by an upstream queue

## How it works

The rate limiter supports **four strategies**. Each interprets `PermitLimit` and
`WindowSeconds` differently. The strategy is chosen per-policy via
`RateLimiterOptions.Strategy`.

### Strategy-to-field matrix

| Strategy | `PermitLimit` means | `WindowSeconds` means | `QueueLimit` | `QueueTimeoutMs` |
|----------|---------------------|------------------------|--------------|------------------|
| `TokenBucket` | Bucket capacity | Refill period (seconds) | Used | Used |
| `SlidingWindow` | Max calls in any rolling window | Window size (seconds) | Used | Used |
| `FixedWindow` | Max calls per fixed window | Window size (seconds) | Used | Used |
| `ConcurrencyLimit` | Max simultaneous calls | **Ignored** | Used | Used |

### TokenBucket

Tokens refill continuously at a rate of `PermitLimit / WindowSeconds` per
second. Each call consumes one token. The bucket holds at most `PermitLimit`
tokens at once.

**Use when:** you want smooth load with a small tolerance for bursts.

**Example:** `PermitLimit = 100`, `WindowSeconds = 60` → 1.67 tokens/second,
burst up to 100 if the bucket has been idle.

### SlidingWindow

At most `PermitLimit` calls may occur in any rolling `WindowSeconds` window.
The window is a moving target — no boundary effects.

**Use when:** you need precise enforcement over time. This is the **default**
strategy.

**Example:** `PermitLimit = 100`, `WindowSeconds = 60` → no more than 100 calls
in any 60-second interval, regardless of where the interval starts.

### FixedWindow

At most `PermitLimit` calls may occur per fixed `WindowSeconds` bucket. Buckets
are aligned to the moment the first call arrived.

**Use when:** you want the cheapest possible implementation and can tolerate
boundary bursts.

**Caveat:** a caller can send up to **2 × PermitLimit** calls across a boundary
(permit limit in the last moment of window N, permit limit in the first moment
of window N+1). SlidingWindow does not have this behavior.

### ConcurrencyLimit

At most `PermitLimit` calls may be **in flight simultaneously**. No window is
used; `WindowSeconds` is ignored.

**Use when:** the dependency's bottleneck is concurrent connections, not
requests-per-second.

**Example:** `PermitLimit = 20` → never more than 20 concurrent calls.

## Queue behavior

When a call would be rejected, the rate limiter can let it **wait** for capacity
instead. The queue is capped by `QueueLimit`; each queued call waits up to
`QueueTimeoutMs`.

| Strategy | Queue mechanism | Ordering |
|----------|----------------|----------|
| `TokenBucket` | Wait until the next token would refill | Best-effort, no fairness guarantee |
| `SlidingWindow` | Wait until the oldest call leaves the window | Best-effort, no fairness guarantee |
| `FixedWindow` | Wait until the current bucket rolls over | Best-effort, no fairness guarantee |
| `ConcurrencyLimit` | `SemaphoreSlim.WaitAsync(QueueTimeoutMs)` | **FIFO** (native semaphore ordering) |

**`QueueLimit = 0` (the default)** rejects immediately without queuing.

**Queue depth in metadata:** `queue_depth` is reported in the rejection event's
metadata. For `ConcurrencyLimit`, it is the number of calls currently waiting.
For the three windowed strategies, it is the number of calls at the moment of
decision. When `QueueLimit = 0`, `queue_depth` is always `0`.

## Rejection metadata

When the rate limiter rejects a call, it:

1. Emits a `rate_limited` event via the configured `ILogSink`.
2. Throws `ResilienceException` with category = `RejectionCategory`
   (default `Transient`).

The exception carries these metadata keys:

| Key | Type | Meaning |
|-----|------|---------|
| `strategy` | string | `TokenBucket` / `SlidingWindow` / `FixedWindow` / `ConcurrencyLimit` |
| `permit_limit` | int | Configured `PermitLimit` |
| `window_seconds` | int? | Configured window (omitted for `ConcurrencyLimit`) |
| `queue_limit` | int | Configured queue |
| `queue_depth` | int | Queue depth at rejection (0 when `QueueLimit = 0`) |
| `reason` | string | `rejected_immediately` / `queue_timeout` / `queue_full` |

### Reason values

| Value | When |
|-------|------|
| `rejected_immediately` | `QueueLimit = 0` and no permit available |
| `queue_timeout` | Queued, but the wait exceeded `QueueTimeoutMs` |
| `queue_full` | Queued, but the queue was at capacity |

## Configuration

All rate limiter tuning lives in `RateLimiterOptions`, per policy:

| Option | Default | Meaning |
|--------|---------|---------|
| `Enabled` | `false` | When false, the limiter is a pass-through |
| `Strategy` | `SlidingWindow` | Algorithm: `TokenBucket`, `SlidingWindow`, `FixedWindow`, `ConcurrencyLimit` |
| `PermitLimit` | `100` | The limit — meaning varies by strategy |
| `WindowSeconds` | `60` | Window size (ignored for `ConcurrencyLimit`) |
| `QueueLimit` | `0` | Max calls that may wait; `0` = reject immediately |
| `QueueTimeoutMs` | `5000` | Max wait time for a queued call |
| `RejectionCategory` | `Transient` | Error category on rejection |

**Set via `AddPolicy` in `Program.cs`:**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("external-api", p =>
        {
            p.RateLimiter.Enabled            = true;
            p.RateLimiter.Strategy           = RateLimitStrategy.SlidingWindow;
            p.RateLimiter.PermitLimit        = 100;
            p.RateLimiter.WindowSeconds      = 60;
            p.RateLimiter.QueueLimit         = 0;
            p.RateLimiter.RejectionCategory  = ResilienceErrorCategory.Transient;
        }));

**Set via `appsettings.json`:**

    {
      "Resilience": {
        "Policies": {
          "external-api": {
            "RateLimiter": {
              "Enabled": true,
              "Strategy": "SlidingWindow",
              "PermitLimit": 100,
              "WindowSeconds": 60,
              "QueueLimit": 0,
              "QueueTimeoutMs": 5000
            }
          }
        }
      }
    }

**Set via environment variables:**

    Resilience__Policies__external-api__RateLimiter__Enabled=true
    Resilience__Policies__external-api__RateLimiter__PermitLimit=100

### Strategy strings

When binding from configuration, `Strategy` is bound by name (case-insensitive):

- `"TokenBucket"` or `"tokenbucket"`
- `"SlidingWindow"` or `"slidingwindow"`
- `"FixedWindow"` or `"fixedwindow"`
- `"ConcurrencyLimit"` or `"concurrencylimit"`

Underscore-separated forms (`sliding_window`) are **not** recognized and will
silently fall back to the default.

## Choosing values

### PermitLimit

Look at the dependency's published rate limit. If none is published:

- **Internal service:** start with 10× its measured normal RPS.
- **External API:** match the published limit exactly, less a safety margin.
- **Redis / database:** start with 2× its connection pool size.

### WindowSeconds

- **Third-party APIs:** match the window the API publishes (usually 60s or 1s).
- **Internal services:** 60s is a safe default.
- **Bursty workloads:** shorter windows (1–10s) smooth better.

### QueueLimit

- **Default to 0.** Reject immediately. Fast failure is usually correct.
- **Set to >0** when callers can tolerate latency but not rejection (e.g. a
  background job that processes at its own pace).
- **Do not set higher than 2–3× your concurrency** — a deep queue adds latency
  without improving throughput.

### QueueTimeoutMs

- **Reject-fast APIs:** 50–200ms
- **Latency-tolerant background:** 1000–5000ms
- **Never exceed the caller's own timeout** — a queued call that outlives its
  caller is wasted work.

## Validation warnings

`RateLimiterOptions.Validate(policyName)` runs once per policy on first resolve.
Warnings are logged via `Trace.TraceWarning`. They never throw.

| Condition | Warning |
|-----------|---------|
| `PermitLimit <= 0` | "PermitLimit must be greater than 0" |
| `WindowSeconds <= 0` and `Strategy != ConcurrencyLimit` | "WindowSeconds must be greater than 0" |
| `Strategy == ConcurrencyLimit` and `WindowSeconds != 60` | "ConcurrencyLimit ignores WindowSeconds" |
| `QueueLimit < 0` | "QueueLimit must not be negative" |
| `QueueTimeoutMs < 0` | "QueueTimeoutMs must not be negative" |

Warnings are diagnostic — the policy still runs.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Policies/RateLimiterPolicyBuilder.cs` | The rate limiter implementation |
| `src/Portfolio.Resilience/Configuration/RateLimiterOptions.cs` | Configuration model + `Validate()` |
| `src/Portfolio.Resilience/Configuration/RateLimitStrategy.cs` | Strategy enum |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | Places the rate limiter in the pipeline |
| `src/Portfolio.Resilience/Implementation/ResilienceEventEmitter.cs` | Emits `rate_limited` events |

## Common mistakes

**Mistake 1 — using the rate limiter for global throttling.**
The rate limiter is per-process. In a horizontally-scaled deployment, N
instances each enforce their own limit. If you need a global limit, use a shared
store (Redis-based rate limiter) — that is a different tool.

**Mistake 2 — setting `PermitLimit` higher than the dependency's actual limit.**
The rate limiter enforces *your* declared limit. If you declare 1000 and the
dependency's real limit is 100, the dependency will still return 429s. Read the
dependency's documentation.

**Mistake 3 — a deep queue with a long timeout.**
`QueueLimit = 1000` with `QueueTimeoutMs = 30000` means up to 1000 calls wait
up to 30 seconds. This creates a latency bomb: a caller times out before its
queued call ever runs. Keep the queue shallow.

**Mistake 4 — expecting `FixedWindow` to be precise.**
`FixedWindow` allows up to 2× `PermitLimit` across a window boundary. If you
need precision, use `SlidingWindow` (the default).

**Mistake 5 — setting `ConcurrencyLimit` with a `WindowSeconds`.**
`ConcurrencyLimit` ignores `WindowSeconds`. Leaving the default `60` will
produce a startup warning. Set it explicitly to `0` or leave it at the default
and ignore the warning.

**Mistake 6 — expecting `queue_depth` to be meaningful with `QueueLimit = 0`.**
With no queue, `queue_depth` is always 0. Use `reason = "rejected_immediately"`
to identify these rejections.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/RateLimiterPolicyBuilderTests.cs`
(18 tests):

- Null operation/options rejected; whitespace policy name rejected
- Disabled limiter passes through (no state, no rejection)
- **TokenBucket:** allows burst up to capacity, refills over time, rejects when
  empty
- **SlidingWindow:** allows up to permit limit, window slides, capacity returns
- **FixedWindow:** allows up to permit limit, bucket rolls over
- **ConcurrencyLimit:** caps concurrency, releases on success, releases on
  failure, queued call proceeds after slot frees, queue timeout rejects
- Rejection emits `rate_limited` event with correct metadata
- Rejection uses the configured `RejectionCategory`

## See also

- [bulkhead.md](bulkhead.md) — concurrency cap vs rate cap
- [retry.md](retry.md) — how retry interacts with rate limiting
- [timeout.md](timeout.md) — timeouts apply per attempt inside the rate limiter
- [executor.md](executor.md) — where the rate limiter sits in the pipeline
- [error-classification.md](error-classification.md) — what `Transient` means
- [../SPEC.md](../SPEC.md) section 12 — the normative contract
