<!--
filepath: docs/retry.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Explains the retry policy — backoff formula, jitter, and classifier gating.
-->

# Retry

## What it is

Retry re-executes a failed operation a configurable number of times with an
**exponential backoff** delay between attempts. If the operation eventually
succeeds, the caller sees a success — the retries are invisible.

The retry policy is the **outermost** layer in the resilience pipeline:

    Caller -> Retry -> Circuit -> Timeout -> Operation

This order is deliberate. Every retry attempt gets a fresh circuit check and a
fresh timeout budget. See `docs/executor.md` for why.

## Why it exists

Networks are unreliable. A single HTTP request has maybe a 0.1% chance of a
transient failure (DNS blip, TCP reset, packet loss, momentary overload). Over
thousands of requests, that is thousands of failures that a retry would fix.

Without retry:
- Every transient failure becomes a user-visible error.
- Services that could have succeeded with one more attempt give up immediately.
- Correlation between "the network was busy at 3:15pm" and "the user saw an error"
  is lost.

With retry:
- A single transient failure is absorbed. The user sees nothing.
- Total success rate improves by one or two nines.
- Failures that persist past retry count are genuinely worth alerting on.

**The caveat:** retry is only safe when the operation is **idempotent** — calling
it twice produces the same result as calling it once. Reads are naturally
idempotent. Writes need explicit design (idempotency keys, unique constraints,
upsert semantics).

## When you need it

**Yes:**
- HTTP GET to another service
- HTTP PUT/PATCH (naturally idempotent)
- HTTP POST with an idempotency key
- PostgreSQL SELECT, DELETE by id
- Redis GET, SET with the same key/value

**Careful:**
- HTTP POST without an idempotency key (may double-submit)
- Database writes that append (INSERT without a unique constraint)
- Any operation with visible side effects (sending an email, charging a card)

**No:**
- Operations that must run exactly once
- Operations on a value that has already been consumed (streams, one-time tokens)
- Local in-memory operations with no external dependency
## How it works

### The backoff formula

For the *n*-th retry (1-indexed; the initial call is not a retry):

    delay(n) = min(base_delay * 2^(n-1), max_delay) + jitter

where

    jitter = rand(0, jitter_ratio * base_delay)

**Worked example** with `BaseDelayMs = 100`, `MaxDelayMs = 30_000`, `JitterRatio = 0.3`:

| Retry | Base computation | After cap | Plus jitter (0..30ms) |
|-------|------------------|-----------|-----------------------|
| 1 | 100 * 2^0 = 100 | 100 | 100..130ms |
| 2 | 100 * 2^1 = 200 | 200 | 200..230ms |
| 3 | 100 * 2^2 = 400 | 400 | 400..430ms |
| 4 | 100 * 2^3 = 800 | 800 | 800..830ms |
| 5 | 100 * 2^4 = 1600 | 1600 | 1600..1630ms |
| 6 | 100 * 2^5 = 3200 | 3200 | 3200..3230ms |
| 7 | 100 * 2^6 = 6400 | 6400 | 6400..6430ms |
| 8 | 100 * 2^7 = 12800 | 12800 | 12800..12830ms |
| 9 | 100 * 2^8 = 25600 | 25600 | 25600..25630ms |
| 10 | 100 * 2^9 = 51200 | **30000** (capped) | 30000..30030ms |
| 11+ | 100 * 2^10 = 102400 | **30000** (capped) | 30000..30030ms |

The cap prevents the delay from growing unbounded. With `MaxAttempts = 3`, the
total worst-case time is `100 + 200 + 400 = 700ms` plus jitter — well bounded.

**Attempt count:** `Attempt = 1` is the initial call (no delay). `Attempt = 2` is
the first retry (delay 100..130ms). `Attempt = 3` is the second retry (delay
200..230ms). And so on.

### Jitter: why random, not constant

Imagine 1000 service instances all failing at exactly the same moment (e.g. a
brief network outage). With constant delays, all 1000 instances retry at exactly
the same millisecond. This "thundering herd" overloads the recovering dependency
and causes another failure — a self-inflicted outage.

Jitter spreads retries across a window. With `JitterRatio = 0.3` and `BaseDelayMs
= 100`, retries for the same attempt are spread across a 30ms window. At 1000
instances, that is one retry every 0.03ms on average — no herd.

**Recommended JitterRatio: 0.2 to 0.5.** `0.0` disables jitter (useful in tests).
`1.0` is excessive (retries spread over a full base_delay window).

### Which errors trigger retry

Retry is **gated by the error classifier**. After each failure, the classifier
categorizes the exception:

| Category | Retried? | Why |
|----------|----------|-----|
| `Transient` | ✅ Yes | The whole point |
| `Timeout` (from our policy) | ✅ Yes | A slow dependency may recover |
| `Permanent` | ❌ No | Retrying validation errors wastes time |
| `CircuitOpen` | ❌ No | The circuit will reject until `OpenDurationSeconds` passes |
| `FallbackUsed` | ❌ No | A fallback already produced a value |
| `Unknown` | ❌ No | Conservative default |

**Opt-in:** `RetryOptions.RetryOnPermanent = true` retries everything, including
permanent errors. Use only when you have a specific reason (e.g. a legacy API
that returns 400 for transient conditions).

See `docs/error-classification.md` for how each exception type is categorized.
## Configuration

All retry tuning lives in `RetryOptions`, per policy:

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxAttempts` | `3` | Number of **retries** after the initial call (so max 4 total attempts) |
| `BaseDelayMs` | `500` | Base delay in milliseconds |
| `MaxDelayMs` | `30000` | Cap on the computed delay |
| `JitterRatio` | `0.3` | Jitter as a fraction of `BaseDelayMs` (0.0 = no jitter, 1.0 = full base_delay window) |
| `RetryOnPermanent` | `false` | When true, also retry on `Permanent` classified errors |

**Set via `AddPolicy` in `Program.cs`:**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts      = 3;
            p.Retry.BaseDelayMs      = 200;
            p.Retry.MaxDelayMs       = 10_000;
            p.Retry.JitterRatio      = 0.3;
            p.Retry.RetryOnPermanent = false;
        }));

**Set via `appsettings.json`:**

    {
      "Resilience": {
        "Policies": {
          "auth-service": {
            "Retry": {
              "MaxAttempts": 3,
              "BaseDelayMs": 200,
              "MaxDelayMs": 10000,
              "JitterRatio": 0.3,
              "RetryOnPermanent": false
            }
          }
        }
      }
    }

**Set via environment variables:**

    Resilience__Policies__auth-service__Retry__MaxAttempts=3
    Resilience__Policies__auth-service__Retry__BaseDelayMs=200

## Choosing MaxAttempts

| MaxAttempts | Total attempts | Max cumulative delay (base=100ms, jitter=0.3) |
|-------------|----------------|-----------------------------------------------|
| 0 | 1 | 0ms (no retry) |
| 1 | 2 | ~130ms |
| 2 | 3 | ~360ms |
| 3 (default) | 4 | ~760ms |
| 5 | 6 | ~3.5s |
| 10 | 11 | ~30s (capped) |

**Recommendations by dependency type:**

- **Fast internal services** (`auth-service`): `MaxAttempts = 3`, `BaseDelayMs = 100`
- **External APIs with rate limits**: `MaxAttempts = 5`, `BaseDelayMs = 1000` (respect 429 Retry-After if available)
- **Database writes**: `MaxAttempts = 1` or `2` — writes should be idempotent, and excessive retry can duplicate work
- **Database reads**: `MaxAttempts = 3`
- **Redis**: `MaxAttempts = 2` — Redis failures are usually connection-level, not transient request-level

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Policies/RetryPolicyBuilder.cs` | The retry executor |
| `src/Portfolio.Resilience/Configuration/RetryOptions.cs` | Configuration model |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | Chains retry with circuit and timeout |
| `src/Portfolio.Resilience/Errors/ErrorClassifier.cs` | Determines what is retryable |

## Common mistakes

**Mistake 1 — retrying non-idempotent operations.**
An HTTP POST without an idempotency key, retried three times, might create three
records. Retry assumes the operation is safe to repeat. If it is not, do not retry
it (set `MaxAttempts = 0` for that policy).

**Mistake 2 — setting MaxAttempts to a huge number.**
`MaxAttempts = 100` with exponential backoff caps at `MaxDelayMs` per retry.
100 retries * 30s = 50 minutes. The user has long since given up. Set `MaxAttempts`
to match the acceptable worst-case response time for the caller.

**Mistake 3 — JitterRatio = 0.**
Works fine in unit tests. In production, this is a self-inflicted thundering herd.
Always use at least `0.1`.

**Mistake 4 — retrying the whole HTTP call when only the body is bad.**
If the server rejects a request body with a 400, retrying the same body produces
the same 400. The classifier correctly classifies this as `Permanent` and skips
retry.

**Mistake 5 — assuming retry is free.**
Every retry costs a connection, a slot in the dependency's thread pool, and time.
Retries should be a small number (2–5), not a hammer. If a dependency needs more
than 5 retries to succeed, it is not healthy — fix the dependency, not the retry.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/RetryPolicyBuilderTests.cs` (14 tests):

- Null operation/options rejected
- Success on first attempt → no retry
- Transient failure → retried, succeeds on 2nd attempt
- Retries exhausted → last exception thrown
- `Permanent` errors not retried by default
- `RetryOnPermanent = true` retries permanent errors
- `MaxAttempts = 0` → single attempt, no retry
- Backoff formula: attempt 1 = base + jitter, attempt 2 = 2*base + jitter, attempt 3 = 4*base + jitter
- Cap at `MaxDelayMs`
- Zero jitter is exact
- Jitter stays within bounds over 100 iterations

## See also

- [error-classification.md](error-classification.md) — what counts as retryable
- [timeout.md](timeout.md) — how retry interacts with per-attempt timeouts
- [circuit-breaker.md](circuit-breaker.md) — how retry avoids hammering a broken dependency
- [executor.md](executor.md) — where retry sits in the pipeline
- [../SPEC.md](../SPEC.md) §Retry — the normative formula
