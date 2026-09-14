<!--
filepath: docs/timeout.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Explains the timeout policy — ceiling semantics and caller-cancellation distinction.
-->

# Timeout

## What it is

Timeout imposes a **per-attempt ceiling** on an operation. If the operation does
not complete within the configured window, its cancellation token is signaled, and
a `ResilienceException` with category `Timeout` is thrown.

Timeout is the **innermost** layer in the pipeline:

    Caller -> Retry -> Circuit -> Timeout -> Operation

Each retry attempt gets a **fresh timeout window**. If retry has `MaxAttempts = 3`
and timeout is 5000ms, the total worst-case is `4 attempts * 5000ms = 20s` (plus
backoff), not 5000ms total.

## Why it exists

Without a timeout, a slow dependency can hang the caller indefinitely. The thread
blocks. The connection remains open. The caller eventually times out at a higher
layer (HTTP server, load balancer, browser), but by then:

- Dozens or hundreds of threads are stuck
- The connection pool is exhausted
- The service is effectively down, even though CPU and memory look idle

This is the classic "cascading failure via connection exhaustion" pattern. A single
slow dependency brings down its consumers.

With a per-attempt timeout:
- Each attempt is bounded.
- Failed attempts release their resources promptly.
- The circuit breaker sees failures and can open, shedding load.
- The caller gets a clear `ResilienceException(Timeout)` it can react to.

## When you need it

**Always.** Every operation through the executor should have a timeout ceiling.
A missing timeout is a latent outage waiting to happen.

**Recommended ceilings by dependency type:**

| Dependency | Suggested `TimeoutMs` | Why |
|------------|----------------------|-----|
| In-cluster HTTP (auth, notification) | `5000` | Fast services with low latency |
| External HTTP (Stripe, GitHub API) | `15000` | Slower round-trips, variable latency |
| Database read | `10000` | Most queries complete in <100ms; 10s is generous |
| Database write | `15000` | Writes may wait on locks or batch commits |
| Redis | `2000` | Redis is in-memory; if it is slow, something is wrong |
| AI service (LLM inference) | `60000`+ | Inference genuinely takes 10–60s |

**A timeout should be a signal, not a kill switch.** If a call times out regularly,
the ceiling is too low or the dependency is unhealthy. Neither is fixed by raising
the timeout — that only hides the problem.
## How it works

### Linked cancellation tokens

`TimeoutPolicyBuilder` creates a **linked** `CancellationTokenSource` combining
two sources:

1. The **caller's** `CancellationToken` (passed in from the top of the call stack)
2. The **timer** that fires after `TimeoutMs`

Only when **both** are combined can we distinguish "the user gave up" from "our
ceiling fired". If we only used a timer, a user-cancelled call would look identical
to a timeout. If we only used the caller's token, we would never time out.

    var timeoutCts = new CancellationTokenSource();
    var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt, timeoutCts.Token);
    timeoutCts.CancelAfter(options.TimeoutMs);

### Distinguishing caller cancellation from timeout

When the operation throws `OperationCanceledException`, we check **which** token
fired:

    catch (OperationCanceledException)
    {
        if (callerCt.IsCancellationRequested)
        {
            // Real user cancellation — rethrow as-is
            throw;
        }
        // Our timer fired — surface a typed ResilienceException
        throw new ResilienceException(
            category: ResilienceErrorCategory.Timeout,
            ...);
    }

**Why this matters:**

| Scenario | Correct behavior | Wrong behavior |
|----------|------------------|----------------|
| User closes browser mid-request | Rethrow `OperationCanceledException` — caller does not want a result | Retry, log an error, alert ops |
| Our ceiling fires on a slow dependency | Throw `ResilienceException(Timeout)` — retry and circuit may react | Silently return null; hide the outage |

If the timeout policy confused the two, a user closing a tab would trigger retries
and count against the circuit — insane.

### Timeout as `ResilienceException`

Unlike the retry policy (which rethrows the last error), timeout **wraps** its
failure in a `ResilienceException`:

    throw new ResilienceException(
        message: "Operation exceeded timeout of 5000ms (policy 'auth-service').",
        policyName: "auth-service",
        category: ResilienceErrorCategory.Timeout,
        attemptsMade: 1,
        totalDuration: elapsed,
        metadata: new Dictionary<string, object?>
        {
            ["timeout_ms"] = options.TimeoutMs,
            ["elapsed_ms"] = elapsed.TotalMilliseconds
        });

The `Timeout` category is distinct from `Transient`. See
`docs/error-classification.md` §"Design note: why TimeoutException is Transient"
for why we keep them separate.

### Disabling the timeout

`TimeoutMs = 0` (or negative) disables the ceiling entirely. The operation runs
until it completes or the caller's token fires.

**When to disable:**
- Streaming responses (SSE, long-lived gRPC streams)
- Operations that legitimately take minutes (large data exports)
- Tests that need deterministic timing

**When NOT to disable:**
- Any HTTP call to another service
- Any database call
- Any Redis call
## Time budget interaction (v0.8.0)

When the caller sets `timeBudgetMs` on `ExecuteAsync`, the effective per-attempt
ceiling becomes `min(configured TimeoutMs, remaining budget)`. When the
configured timeout is disabled (`TimeoutMs <= 0`) but a budget is active, the
budget becomes the effective ceiling - the caller's SLA wins.

When the budget is already exhausted at entry, the timeout throws immediately
with `Category = Timeout` and `metadata.remaining_ms` recording the value.

Without a budget, timeout behaves exactly as it did in v0.7.0. See
[time-budget.md](time-budget.md) for the full contract.
## Configuration

Timeout tuning is minimal. One option per policy:

| Option | Default | Meaning |
|--------|---------|---------|
| `TimeoutMs` | `10000` | Per-attempt ceiling. `<= 0` disables the ceiling. |

**Set via `AddPolicy`:**

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p =>
        {
            p.Timeout.TimeoutMs = 5000;
        }));

**Set via `appsettings.json`:**

    {
      "Resilience": {
        "Policies": {
          "auth-service": {
            "Timeout": {
              "TimeoutMs": 5000
            }
          }
        }
      }
    }

**Set via environment variable:**

    Resilience__Policies__auth-service__Timeout__TimeoutMs=5000

## Interaction with retry and HttpClient.Timeout

Three timeout-like ceilings can exist in one HTTP call. Understanding their
interaction matters:

| Layer | Where it lives | What it bounds |
|-------|----------------|----------------|
| `HttpClient.Timeout` | `HttpClient` property | The **entire** send (single attempt, no retries) |
| Policy `TimeoutMs` | `PolicyDefinition.Timeout` | **One attempt** |
| Outer HTTP server timeout | Kestrel, nginx, load balancer | The **whole request**, including all retries |

For a policy with `MaxAttempts = 3` and `TimeoutMs = 5000`:

    Total worst-case: 4 attempts * 5000ms + backoff (~0.7s) = ~20.7s

**Recommended relationship:** make `HttpClient.Timeout` **larger** than
`TimeoutMs * (MaxAttempts + 1)`. Otherwise the outer timeout fires first and the
caller sees `TaskCanceledException` instead of the structured `ResilienceException`.

**Example for `auth-service` policy:**

    c.Timeout = TimeSpan.FromSeconds(30);      // HttpClient outer ceiling
    p.Timeout.TimeoutMs = 5000;                // per attempt
    p.Retry.MaxAttempts = 3;                   // 4 total attempts
    // Max total: ~20.7s, well under the 30s outer ceiling

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Policies/TimeoutPolicyBuilder.cs` | The timeout executor |
| `src/Portfolio.Resilience/Configuration/TimeoutOptions.cs` | Configuration model |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | Places timeout innermost |
| `src/Portfolio.Resilience/Errors/ResilienceException.cs` | Wraps the timeout with `Timeout` category |

## Common mistakes

**Mistake 1 — disabling the timeout everywhere.**
"Just to make sure nothing times out" is how services die. A slow dependency with
no ceiling will eventually exhaust the caller's thread pool.

**Mistake 2 — setting HttpClient.Timeout equal to TimeoutMs.**
The first attempt uses the entire budget. Any retry cannot run (the outer timeout
fires first). The caller sees `TaskCanceledException`, not `ResilienceException`.

**Mistake 3 — setting TimeoutMs too low.**
A 500ms timeout on a database that occasionally takes 800ms under load produces
constant failures. Set the ceiling at ~10x the p99 latency of the dependency in
normal operation.

**Mistake 4 — confusing TimeoutException and the Timeout category.**
A `System.TimeoutException` from a socket times out is classified as **Transient**
(retry may help). Only our policy's own ceiling produces `ResilienceErrorCategory.Timeout`.
See `docs/error-classification.md`.

**Mistake 5 — expecting cancellation to propagate cleanly through a timeout.**
If the inner operation ignores its cancellation token, the timer fires but the
operation keeps running in the background. This is a bug in the operation, not
in the timeout policy. Operations must honor `ct.ThrowIfCancellationRequested()`
at await points.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/TimeoutPolicyBuilderTests.cs` (8 tests):

- Null operation/options/policy name rejected
- Fast operation completes, returns result
- Slow operation exceeds ceiling ? `ResilienceException(Timeout)` with `timeout_ms` metadata
- Caller's token fires before the ceiling ? `OperationCanceledException`, not `ResilienceException`
- `TimeoutMs = 0` disables the ceiling (slow operation still completes)
- Near-ceiling operation succeeds when it finishes in time

## See also

- [retry.md](retry.md) — how retry multiplies the total time budget
- [error-classification.md](error-classification.md) — why `Timeout` differs from `Transient`
- [http-integration.md](http-integration.md) — the three-layer timeout interaction
- [executor.md](executor.md) — where timeout sits in the pipeline
- [../SPEC.md](../SPEC.md) §Timeout — the normative semantics

## How to use it — a worked walkthrough

This section walks through adding a timeout to a real call site, one step at
a time. It assumes you already call operations through `IResilienceExecutor`
(or `ResilientHttpMessageHandler`). If you do not, see
[executor.md](executor.md) first.

### Step 1 — Decide what to protect

Suppose you have an HTTP call to an external pricing service. The service is
usually fast (100–200ms) but occasionally slow (2–5s). The call site currently
looks like this:

    var price = await _http.GetFromJsonAsync<Price>($"/api/v1/prices/{sku}", ct);

There is no ceiling. If the pricing service hangs, this call hangs. The
thread stays occupied. Under load, this cascades.

**The operation to protect is the `GetFromJsonAsync` call.**

### Step 2 — Register a policy for that dependency

In `Program.cs`, register a policy that matches the pricing service's
behavior:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("pricing-service", p =>
        {
            p.Timeout.TimeoutMs = 3000;   // 3 seconds — well above p99, still bounded
            p.Retry.MaxAttempts = 2;      // retry transient failures
            p.Retry.BaseDelayMs = 200;
        }));

**Why 3000ms?** The service is normally fast. A 3-second ceiling is generous
enough that it rarely fires, but low enough that a hung call is killed
before it spreads. **The ceiling is a signal, not a target.** If it fires
often, the dependency is unhealthy — the fix is to fix the dependency, not
raise the ceiling.

### Step 3 — Wrap the call site

Replace the direct HTTP call with one through `IResilienceExecutor`:

    public sealed class PricingClient
    {
        private readonly HttpClient _http;
        private readonly IResilienceExecutor _resilience;

        public PricingClient(
            HttpClient http,
            IResilienceExecutor resilience)
        {
            _http = http;
            _resilience = resilience;
        }

        public Task<Price?> GetPriceAsync(string sku, CancellationToken ct)
        {
            return _resilience.ExecuteAsync(
                "pricing-service",
                token => _http.GetFromJsonAsync<Price>(
                    $"/api/v1/prices/{sku}", token),
                ct: ct);
        }
    }

**What changed:**

- The `HttpClient` call now runs inside `ExecuteAsync`.
- The **caller's `ct`** is passed through to `ExecuteAsync` — this is what
  distinguishes "the caller gave up" from "our ceiling fired." If you drop
  the `ct`, a user-closed browser tab becomes indistinguishable from a slow
  service, and the timeout policy may retry work nobody wants.
- The `token` inside the lambda is the **linked** token — it fires when
  either the caller's `ct` fires OR our 3000ms timer fires.

### Step 4 — Understand what happens on each outcome

Given `TimeoutMs = 3000`, `MaxAttempts = 2`, `BaseDelayMs = 200`, four
outcomes are possible:

| Outcome | What the caller sees |
|---------|---------------------|
| Pricing service replies in 150ms | `Price` object — no timeout involved |
| Pricing service takes 3.5s | After 3s, the ceiling fires ? `ResilienceException(Timeout)`. Retry sleeps 200ms, then tries again — a **fresh 3-second window** |
| Pricing service hangs indefinitely | Same as above, but every attempt burns its full 3s. Worst case: 3 attempts × 3s + 2 × 200ms backoff = **~9.4s total**, then `ResilienceException(Timeout)` |
| Caller cancels the request (user closes tab) | `OperationCanceledException` — NOT a `ResilienceException`. No retry, no circuit count, no ops alert |

**The fourth outcome is the important one.** Timeout correctly separates
"caller chose to stop" from "our ceiling fired." If it did not, a user
closing a browser tab would trigger retries against a healthy service.

### Step 5 — Tune based on observed behavior

Once this is running in production, watch two signals:

- **`timeout_breached` events in your `ILogSink`** — how often is the ceiling
  firing?
- **`LatencySnapshot.P99Ms` from `ILatencyTracker`** — where is the actual
  p99?

If the ceiling fires under 0.1% of calls, the value is fine. If it fires
often (say >1%), the dependency is genuinely slow. Two options:

1. **Fix the dependency** (the right answer).
2. **Raise the ceiling** (the wrong answer, unless the dependency's true p99
   has changed and the current ceiling is now below it).

**Never raise the ceiling just to make the warnings stop.** That removes the
signal but not the problem.

### Step 6 — Coordinate with the outer HTTP timeout

If your service is behind nginx, an ALB, or Kestrel, there is a third ceiling
in the path — the whole-request timeout. Its value must be **larger than the
worst-case time of the pipeline**, or the outer timeout fires before our
policy produces its structured exception.

For the pricing example above, the pipeline worst case is ~9.4s. A 15-second
outer timeout leaves comfortable headroom.

For `HttpClient` itself (when used inside the pipeline), set
`HttpClient.Timeout` **larger** than `TimeoutMs × (MaxAttempts + 1)`:

    builder.Services
        .AddHttpClient("pricing-service", c =>
        {
            c.BaseAddress = new Uri("https://pricing.example.com");
            c.Timeout = TimeSpan.FromSeconds(15);   // outer ceiling
        })
        .AddResilientHandler("pricing-service");

    // policy: TimeoutMs = 3000, MaxAttempts = 2
    // worst case: 3 × 3s + backoff ~= 9.4s ? safely under 15s

### Step 7 — What a `Timeout` failure looks like to the caller

If the ceiling does fire and all retries are exhausted, the caller catches:

    try
    {
        var price = await _pricing.GetPriceAsync(sku, ct);
        return price;
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Timeout)
    {
        // We can tell the difference between "the caller cancelled" and
        // "our policy gave up." Here, the policy gave up.
        _logger.LogWarning(
            "Pricing lookup for {Sku} exceeded its budget. Attempts: {Attempts}, elapsed: {Elapsed}ms",
            sku, ex.AttemptsMade, ex.TotalDuration.TotalMilliseconds);

        throw new PricingUnavailableException(sku, ex);
    }

The exception carries:

- `Category = Timeout` — a specific category, not `Transient`.
- `AttemptsMade` — how many attempts actually ran before the ceiling fired.
- `TotalDuration` — wall time from first attempt to final failure.
- `Metadata["timeout_ms"]` — the configured ceiling.
- `Metadata["elapsed_ms"]` — how long the operation actually ran.

**This is why the timeout policy uses a distinct category.** A caller can
differentiate between "retry eventually won" (no exception), "retry exhausted
on a real error" (`Transient` / `Permanent` / etc.), and "our ceiling fired"
(`Timeout`). The last case is usually an operational issue, not a code bug.

### A note on what NOT to do

**Do not disable the timeout for convenience.** The pattern "`TimeoutMs = 0`
because I do not want failures in development" ships to production. In
production, the same missing ceiling that made dev easier becomes the
cascading failure you were trying to prevent.

If you need a longer ceiling in a specific environment, set a value — do not
remove the ceiling entirely.

