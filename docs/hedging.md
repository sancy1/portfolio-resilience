<!--
filepath: docs/hedging.md
package:  Portfolio.Resilience | since: v0.7.0
purpose:  Explains hedging - parallel attempts with a latency race, plus safety limits.
-->

# Hedging

## What it is

Hedging fires **parallel attempts of the same operation** with a stagger delay
between them, and returns the first successful result. The primary starts
immediately. If it is slow, a hedge fires after a delay. If a hedge wins the
race, the primary is cancelled.

**Hedging is a latency optimization, not a reliability one.** Retry handles
transient failures. Hedging handles the case where the p99 tail is unacceptable
and a duplicate attempt is cheaper than waiting.

In the pipeline:

    Caller -> RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

Hedging sits between bulkhead and retry. It fires **multiple calls through the
inner layers** (retry, circuit, timeout) when the primary is slow.

## Why it exists

A single slow response ruins a request even when the average is good.

Imagine a payment authorization with `p50 = 40ms` and `p99 = 400ms`. Most users
see fast responses. But 1% of them wait 400ms, and if the SLA is 200ms, that 1%
is a problem. A retry does not help — the operation did not fail, it was just
slow. What helps is **racing a second attempt** and taking whichever finishes
first.

**Hedging reduces tail latency at the cost of extra load.** It is exactly the
right trade for read-heavy critical paths. It is dangerous for non-idempotent
writes.

## ⚠️ Safety — read this first

**Hedging is NOT safe for non-idempotent operations.**

A hedged `POST /charge` can create **two charges** if both attempts reach the
server and the server does not deduplicate. This is not a bug in the library;
it is inherent in the idea of sending the same request twice.

**Use hedging only when:**

- The operation is **idempotent** — calling it twice produces the same result
  as calling it once. (HTTP GET, HTTP PUT, SQL SELECT, SQL DELETE by id.)
- Or the operation is idempotent **because the downstream provider deduplicates**
  on an idempotency key. Stripe, Adyen, and Square all support this.

**Do not use hedging for:**

- `POST /charge` without an idempotency key.
- Any write that appends or creates without a unique constraint.
- Operations with visible side effects (sending an email, sending an SMS).

**At runtime:** when a policy enables hedging, the library logs a warning at
first resolve. It does not throw — that would break legitimate idempotent uses —
but the warning is there so it appears in startup logs. See the "Validation
warnings" section below.

## When you need it

**Yes:**

- Idempotent reads on a latency-critical path (feed, search, catalog lookup).
- Cache-miss fills where the cache is slower than a second call.
- Payment authorization calls **with an idempotency key** (Stripe, Adyen,
  Square all deduplicate).
- Any HTTP GET where the p99 is above the caller's SLA.

**No:**

- Non-idempotent writes without an idempotency key.
- Operations that must run exactly once.
- Dependencies that cannot tolerate extra load. Hedging multiplies request
  volume under tail-latency conditions.
- Operations with a tight rate limit that the extra requests would exhaust.

## How it works

### The race

1. **Attempt 0 fires immediately** — this is the "primary."
2. **Before firing attempt N**, the library waits `delay(N)` milliseconds.
3. **During that wait, if any prior attempt succeeds**, the hedge never fires.
   The winner is returned.
4. **If the delay elapses without a winner**, the hedge fires. It runs in
   parallel with the still-pending attempts.
5. **The first successful attempt wins.** The others are either cancelled
   (`CancelOnSuccess = true`, default) or allowed to complete.

### The stagger delay

The delay for attempt N depends on `HedgingOptions.ExponentialBackoff`:

| `ExponentialBackoff` | `delay(N)` | For `DelayMs = 100` |
|---|---|---|
| `false` (default) | `DelayMs` | 100ms, 100ms, 100ms, ... |
| `true` | `DelayMs * 2^(N-1)` | 100ms, 200ms, 400ms, 800ms, ... |

Exponential backoff reduces the load on a struggling dependency. Use it when
the workload is bursty.

### Worked example

`MaxAttempts = 3`, `DelayMs = 50`, `ExponentialBackoff = true`:

| Time | Event | State |
|---|---|---|
| 0ms | Attempt 1 fires | 1 in flight |
| 50ms | Delay for attempt 2 elapsed; no winner | 2 in flight |
| 150ms | Delay for attempt 3 elapsed; no winner | 3 in flight |
| 90ms | Attempt 2 succeeds | **Winner: attempt 2** |
| 90ms | Attempt 1 cancelled | 0 in flight (after cancel propagates) |
| 150ms | Attempt 3 never fires | — |

**The stagger is not just a fixed wait.** At each hedge's delay, the library
**checks whether an earlier attempt already won**. In the example, attempt 3
would have fired at 150ms, but attempt 2 won at 90ms, so attempt 3 never runs.

### Per-attempt timeout

`AttemptTimeoutMs` bounds a single attempt. This is **different from the
pipeline's timeout layer**, which bounds the whole `ExecuteAsync` call.

- `AttemptTimeoutMs = 0` (default): each attempt runs until the pipeline's
  timeout fires.
- `AttemptTimeoutMs > 0`: each attempt is cancelled individually if it exceeds
  the ceiling. A slow attempt loses the race and the hedge is more likely to
  win.

**Use `AttemptTimeoutMs` when the primary's slowness is the problem.** Set it
shorter than the p99 so slow attempts are killed and hedges take over.

### Winner and loser events

When `EmitAttemptEvents = true` (default), three events are emitted:

| Event | When | Metadata |
|---|---|---|
| `hedge_won` | The winning attempt succeeded | `attempt`, `duration_ms` |
| `hedge_lost` | A loser completed after the winner | `attempt`, `duration_ms` |
| `hedge_cancelled` | A loser was cancelled by the winner | `attempt` |

`attempt` is **1-based**. Attempt 1 is the primary. Attempt 2 is the first hedge.

## Configuration

All hedging tuning lives in `HedgingOptions`, per policy:

| Option | Default | Meaning |
|--------|---------|---------|
| `Enabled` | `false` | When false, hedging is a pass-through |
| `MaxAttempts` | `2` | Total attempts including the primary |
| `DelayMs` | `100` | Base stagger delay between attempts |
| `ExponentialBackoff` | `false` | When true, delay doubles per attempt |
| `AttemptTimeoutMs` | `0` | Per-attempt ceiling; 0 = no ceiling |
| `CancelOnSuccess` | `true` | Cancel losers when a winner succeeds |
| `EmitAttemptEvents` | `true` | Emit `hedge_won` / `hedge_lost` / `hedge_cancelled` |
| `RejectionCategory` | `Transient` | Category when all attempts fail |

**Set via `AddPolicy` in `Program.cs`:**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("catalog-read", p =>
        {
            p.Hedging.Enabled            = true;
            p.Hedging.MaxAttempts        = 2;
            p.Hedging.DelayMs            = 50;
            p.Hedging.AttemptTimeoutMs   = 200;
        }));

**Set via `appsettings.json`:**

    {
      "Resilience": {
        "Policies": {
          "catalog-read": {
            "Hedging": {
              "Enabled": true,
              "MaxAttempts": 2,
              "DelayMs": 50,
              "AttemptTimeoutMs": 200
            }
          }
        }
      }
    }

**Set via environment variables:**

    Resilience__Policies__catalog-read__Hedging__Enabled=true
    Resilience__Policies__catalog-read__Hedging__DelayMs=50

## Choosing values

### MaxAttempts

- **`1`** disables hedging. Same as `Enabled = false`.
- **`2` (recommended)** = primary + one hedge. Best latency/cost ratio for
  most critical reads.
- **`3`** = two hedges. Use only for the most latency-critical reads.
- **`> 3`** — the library logs a warning. Aggressive; consider whether the
  dependency can handle the extra load.

### DelayMs

- **The delay is a latency budget for the primary.** Set it to roughly the
  primary's p95. "If it hasn't answered in 50ms, fire the hedge."
- **Too short** (e.g. 5ms) — the hedge fires on almost every call. Doubles
  the load.
- **Too long** (e.g. 1000ms) — the hedge never fires before the primary
  would have anyway.

### AttemptTimeoutMs

- **`0`** — no per-attempt ceiling. Rely on the pipeline timeout.
- **A value between p99 and the pipeline timeout** — kills slow attempts
  before they consume their full ceiling. Makes hedges more likely to win.
- **Never set above the pipeline's timeout.** The pipeline will cancel first;
  the per-attempt ceiling is then pointless.

### CancelOnSuccess

- **`true` (default)** — cancel losers immediately. Correct for reads.
- **`false`** — let losers complete. Required if the loser's side effects
  matter (for example, a write that must not be interrupted).
- **If you set `false`, the operation must be idempotent.** A cancelled
  request may still have reached the server; a completed one definitely did.

## Validation warnings

`HedgingOptions.Validate(policyName)` runs once per policy on first resolve.
Warnings are logged via `Trace.TraceWarning`. They never throw.

| Condition | Warning |
|-----------|---------|
| `MaxAttempts <= 0` | "MaxAttempts must be greater than 0" |
| `MaxAttempts > 5` | "MaxAttempts = N is aggressive; consider 2-3" |
| `DelayMs < 0` | "DelayMs must not be negative" |
| `AttemptTimeoutMs < 0` | "AttemptTimeoutMs must not be negative" |
| `AttemptTimeoutMs > 0` and `< DelayMs` | "AttemptTimeoutMs is shorter than DelayMs; hedged attempts may never fire" |

**In addition**, when `Hedging.Enabled = true`, the library logs a warning at
first resolve:

    Policy 'catalog-read': Hedging is enabled. Hedging fires parallel attempts
    of the same operation. Ensure the operation is idempotent or carries an
    idempotency key, or set Hedging.Enabled = false.

This is a **fixed warning** — not driven by a validation rule — and it appears
once per policy that enables hedging.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Policies/HedgingPolicyBuilder.cs` | The race implementation |
| `src/Portfolio.Resilience/Configuration/HedgingOptions.cs` | Configuration model + `Validate()` |
| `src/Portfolio.Resilience/Events/ResilienceEventType.cs` | `HedgeWon = 11`, `HedgeLost = 12`, `HedgeCancelled = 13` |
| `src/Portfolio.Resilience/Implementation/ResilienceEventEmitter.cs` | Emits the three hedge events |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | Places hedging in the default pipeline |

## Common mistakes

**Mistake 1 — enabling hedging on a write without an idempotency key.**

The single most dangerous mistake. A hedged `POST /charge` will charge twice.
**Fix:** either add an idempotency key, or set `Hedging.Enabled = false` on
that policy.

**Mistake 2 — treating hedging as a retry replacement.**

Hedging does **not** retry transient failures. It fires duplicate attempts
because the primary is slow. If you need both — for a slow, flaky read — enable
hedging inside retry (the default pipeline order: `Hedging -> Retry`). Retry
wraps each hedge attempt; hedging wraps the operation inside retry.

**Mistake 3 — a very short `DelayMs`.**

`DelayMs = 5` fires the hedge on almost every call. The dependency sees 2×
load. Tail latency improves; the dependency's p99 worsens. **Set delay to
p95 of the primary.**

**Mistake 4 — `MaxAttempts > 3` without justification.**

Every extra attempt is a full duplicate request against the dependency. Beyond
2–3, the return on latency is small and the load is significant.

**Mistake 5 — expecting losers to be "free."**

Cancelled attempts may still have reached the server. If `CancelOnSuccess =
false`, losers definitely reach the server. The cost of hedging includes the
losers.

**Mistake 6 — no `AttemptTimeoutMs` on a slow dependency.**

Without a per-attempt ceiling, a slow attempt occupies a slot until the
pipeline timeout. `AttemptTimeoutMs` frees the slot earlier, letting the hedge
win.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/HedgingPolicyBuilderTests.cs`
(23 tests):

- Null guards for operation, options, policy name
- Disabled hedging and `MaxAttempts = 1` both run only the primary
- Primary wins first — hedge never fires (`attempts == 1`)
- Hedge wins first — primary cancelled, hedge result returned
- `CancelOnSuccess = true` cancels losers
- `CancelOnSuccess = false` lets losers complete
- `AttemptTimeoutMs` cancels a slow attempt
- `CalculateDelayMs` — constant and exponential paths (7 cases)
- `EmitAttemptEvents = true` produces `hedge_won`
- `EmitAttemptEvents = false` produces no events
- All attempts fail — the **first** attempt's exception is thrown
- All attempts fail — other attempts' errors attached to `Data`
- Single-failure case — no `Data` decoration
- Caller cancellation propagates

**Two specific bugs were caught by these tests** during development:

1. Hedge fired unconditionally even when the primary already succeeded.
2. `WaitForFirstSuccessOrAllFailAsync` deadlocked when a hedge won first.

Both would have shipped without the tests.

## See also

- [retry.md](retry.md) — retry for transient failures (different concern)
- [bulkhead.md](bulkhead.md) — hedging multiplies load; combine with bulkhead
- [rate-limiter.md](rate-limiter.md) — hedging multiplies rate; watch the cap
- [timeout.md](timeout.md) — `AttemptTimeoutMs` is per-attempt; the pipeline
  timeout is per-call
- [composition.md](composition.md) — custom pipeline order for hedging
- [executor.md](executor.md) — where hedging sits in the pipeline
- [../SPEC.md](../SPEC.md) section 16 — the normative contract
