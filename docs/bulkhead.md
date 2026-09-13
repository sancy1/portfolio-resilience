<!--
filepath: docs/bulkhead.md
package:  Portfolio.Resilience | since: v0.6.0
purpose:  Explains bulkhead isolation - concurrency cap, queue behavior, and rejection metadata.
-->

# Bulkhead

## What it is

A bulkhead **caps how many calls may run concurrently** against a resource.
Calls beyond the cap may wait briefly in a bounded queue; calls beyond the queue
are rejected immediately.

The bulkhead is the **second** layer in the pipeline:

    Caller -> RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout -> Operation

It sits outside retry so that concurrency is capped before the retry loop
multiplies load.

## Why it exists

When a dependency slows down, calls to it **accumulate**. A caller's thread pool
fills with operations waiting for the dependency. Soon the caller cannot serve
*any* request — even ones that don't touch the slow dependency. This is called
**thread pool starvation**, and it is one of the most common causes of cascading
failures in microservice architectures.

The bulkhead breaks this cycle by capping the number of operations that can be
in flight at once. Calls beyond the cap are rejected quickly, freeing the
caller's resources to serve other requests.

Without a bulkhead:
- A slow dependency exhausts the caller's thread pool.
- The caller slows down for *all* requests, including unrelated ones.
- The dependency's own backlog continues to grow.

With a bulkhead:
- Concurrency against the dependency is bounded.
- The caller's thread pool has spare capacity for other work.
- Rejected calls fail fast, letting the caller retry or fall back.

**The name** comes from ship design: a bulkhead is a partition that keeps water
from a breach in one compartment flowing into others. A bulkhead in software
similarly contains damage.

## When you need it

**Yes:**
- Calls to a **slow or unreliable dependency** (external API, legacy service)
- Calls that hold resources (database connections, HTTP connections)
- Any dependency where a slowdown could exhaust your thread pool
- Services that make **many parallel calls** to the same dependency

**No:**
- Fast, reliable dependencies
- Operations that already run inside a bounded queue
- In-process operations

## How it works

### The concurrency cap

A bulkhead holds a `SemaphoreSlim` sized to `MaxConcurrency`. A call must
acquire a slot before running and releases it when done.

    Call 1 -> acquire -> run -> release
    Call 2 -> acquire -> run -> release
    Call 3 -> acquire -> run -> release
    ...

The semaphore is held **across the operation**, so a slow operation holds its
slot for its full duration. This is the whole point: it prevents slow operations
from accumulating.

### The waiter queue

When all slots are taken, a call may **wait** for one to free up. The number of
simultaneous waiters is capped by `MaxQueue`:

| Condition | Behavior |
|-----------|----------|
| A slot is free | Acquire immediately |
| No slot, `MaxQueue = 0` | Reject immediately with `rejected_immediately` |
| No slot, queue has room | Wait up to `QueueTimeoutMs` |
| No slot, queue is full | Reject immediately with `queue_full` |
| Waited `QueueTimeoutMs` and still no slot | Reject with `queue_timeout` |

**Waiter ordering is FIFO** — `SemaphoreSlim.WaitAsync` uses first-in,
first-out ordering natively.

### Slot release on failure

Slots are released in a `finally` block, so a slot is returned whether the
operation succeeds, throws, or is cancelled. A failed operation does **not**
hold its slot.

## Rejection metadata

When the bulkhead rejects a call, it:

1. Emits a `bulkhead_rejected` event via the configured `ILogSink`.
2. Throws `ResilienceException` with category = `RejectionCategory`
   (default `Transient`).

The exception carries these metadata keys:

| Key | Type | Meaning |
|-----|------|---------|
| `max_concurrency` | int | Configured `MaxConcurrency` |
| `max_queue` | int | Configured `MaxQueue` |
| `queue_depth` | int | Number of calls waiting at the moment of rejection |
| `reason` | string | `rejected_immediately` / `queue_full` / `queue_timeout` |

### Reason values

| Value | When |
|-------|------|
| `rejected_immediately` | `MaxQueue = 0` and no slot available |
| `queue_full` | `MaxQueue > 0` and the queue was already at capacity |
| `queue_timeout` | Waited, but the slot did not free within `QueueTimeoutMs` |

## Difference from rate limiting

Rate limiting and bulkhead isolation look similar but answer different questions:

| Concern | Rate Limiter | Bulkhead |
|---------|--------------|----------|
| Question answered | "How many calls per unit time?" | "How many calls at once?" |
| Unit | Calls / second | Concurrent calls |
| Persistence | Time-based state (window, token bucket) | Counter (semaphore) |
| When it helps | Burst protection | Slowdown protection |
| When it fires | Too many calls in a window | Too many in-flight calls |

**They compose.** A common pattern:

    p.RateLimiter.PermitLimit  = 100;   // max 100 calls/minute
    p.Bulkhead.MaxConcurrency  = 20;    // max 20 in flight

This means: at most 100 calls per minute, and at most 20 of them running at
the same time. Together they bound both the rate and the concurrency of load
against the dependency.

## Configuration

All bulkhead tuning lives in `BulkheadOptions`, per policy:

| Option | Default | Meaning |
|--------|---------|---------|
| `Enabled` | `false` | When false, the bulkhead is a pass-through |
| `MaxConcurrency` | `20` | Max simultaneous calls |
| `MaxQueue` | `100` | Max callers that may wait for a slot |
| `QueueTimeoutMs` | `5000` | Max wait time for a queued caller |
| `RejectionCategory` | `Transient` | Error category on rejection |

**Set via `AddPolicy` in `Program.cs`:**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("external-api", p =>
        {
            p.Bulkhead.Enabled            = true;
            p.Bulkhead.MaxConcurrency     = 20;
            p.Bulkhead.MaxQueue           = 100;
            p.Bulkhead.QueueTimeoutMs     = 5000;
            p.Bulkhead.RejectionCategory  = ResilienceErrorCategory.Transient;
        }));

**Set via `appsettings.json`:**

    {
      "Resilience": {
        "Policies": {
          "external-api": {
            "Bulkhead": {
              "Enabled": true,
              "MaxConcurrency": 20,
              "MaxQueue": 100,
              "QueueTimeoutMs": 5000
            }
          }
        }
      }
    }

**Set via environment variables:**

    Resilience__Policies__external-api__Bulkhead__Enabled=true
    Resilience__Policies__external-api__Bulkhead__MaxConcurrency=20

## Choosing values

### MaxConcurrency

Start with the number of **simultaneous operations the dependency can handle**.
If unknown, use a proxy:

- **HTTP calls to a well-provisioned service:** 20–50
- **Database queries:** 2× the connection pool size
- **External third-party API:** 10–20 (most APIs tolerate fewer connections than
  their published rate limit suggests)
- **Redis:** 10–50

**Never set higher than the caller's thread pool can afford.** If your service
has 100 threads and 5 policies with `MaxConcurrency = 50`, a slowdown on all 5
can still starve the pool.

### MaxQueue

- **Default to 0.** Reject immediately — fast failure is usually correct.
- **Set to >0** when callers can tolerate latency but not rejection.
- **Rule of thumb:** `MaxQueue = 2 × MaxConcurrency`. Deeper queues add
  latency without adding throughput.

### QueueTimeoutMs

- **Reject-fast callers:** 50–200ms
- **Latency-tolerant background jobs:** 1000–5000ms
- **Never exceed the caller's own timeout** — a queued call that outlives its
  caller is wasted work.

## Validation warnings

`BulkheadOptions.Validate(policyName)` runs once per policy on first resolve.
Warnings are logged via `Trace.TraceWarning`. They never throw.

| Condition | Warning |
|-----------|---------|
| `MaxConcurrency <= 0` | "MaxConcurrency must be greater than 0" |
| `MaxQueue < 0` | "MaxQueue must not be negative" |
| `QueueTimeoutMs < 0` | "QueueTimeoutMs must not be negative" |

Warnings are diagnostic — the policy still runs.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Policies/BulkheadPolicyBuilder.cs` | The bulkhead implementation |
| `src/Portfolio.Resilience/Configuration/BulkheadOptions.cs` | Configuration model + `Validate()` |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | Places the bulkhead in the pipeline |
| `src/Portfolio.Resilience/Implementation/ResilienceEventEmitter.cs` | Emits `bulkhead_rejected` events |

## Common mistakes

**Mistake 1 — setting `MaxConcurrency` higher than the thread pool can afford.**
A bulkhead protects a single resource. If you have many bulkheaded resources and
their caps sum to more than your thread pool, starvation is still possible.
Budget your threads across policies.

**Mistake 2 — a deep queue with a long timeout.**
`MaxQueue = 1000` with `QueueTimeoutMs = 30000` creates a latency bomb.
Keep queues shallow and timeouts short.

**Mistake 3 — expecting the bulkhead to smooth request rate.**
A bulkhead caps *concurrency*, not *rate*. If you send 10,000 fast requests in
a second with `MaxConcurrency = 20`, most complete instantly, so throughput is
not limited. Use a rate limiter for rate.

**Mistake 4 — setting `MaxQueue > 0` but `QueueTimeoutMs = 0`.**
A zero timeout means queued calls are rejected immediately. Effectively
equivalent to `MaxQueue = 0` but with more expensive bookkeeping.

**Mistake 5 — using a bulkhead for a fast, reliable dependency.**
The bulkhead's semaphore adds small overhead to every call. If the dependency
is fast and reliable, the bulkhead adds cost without meaningful protection.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/BulkheadPolicyBuilderTests.cs`
(13 tests):

- Null operation/options rejected; whitespace policy name rejected
- Disabled bulkhead passes through
- Concurrency cap enforced (2nd call rejects when 1st holds the slot)
- Slot released on success
- Slot released on failure
- Queued call proceeds after slot frees
- Queue timeout rejects with `queue_timeout`
- Queue full rejects with `queue_full`
- Rejection emits `bulkhead_rejected` event with correct metadata
- Rejection uses the configured `RejectionCategory`
- Independent policies have independent semaphores

## See also

- [rate-limiter.md](rate-limiter.md) — rate cap vs concurrency cap
- [retry.md](retry.md) — how retry interacts with the bulkhead
- [circuit-breaker.md](circuit-breaker.md) — a complementary protection
- [timeout.md](timeout.md) — bounds a single attempt inside the bulkhead
- [executor.md](executor.md) — where the bulkhead sits in the pipeline
- [error-classification.md](error-classification.md) — what `Transient` means
- [../SPEC.md](../SPEC.md) section 13 — the normative contract
