<!--
filepath: dotnet/samples/Samples.App/FEATURES.md
package:  Samples.App | since: n/a
purpose:  Exhaustive reference for every Portfolio.Resilience feature, with copy-paste examples and explanations
-->

# FEATURES - Portfolio.Resilience in depth

This document is the deep reference. `QUICKSTART.md` is the fast path - read it first if you want to get running in five minutes. This file covers every capability of the library in detail, with copy-paste examples and the reasoning behind each option.

Every code example here is verified against `Portfolio.Resilience 0.8.0` from nuget.org. Where behavior differs from the documentation, the code in the sample scenarios is the source of truth.

---

## 1. Overview - when to use this library

Portfolio.Resilience is a resilience library for .NET that wraps outbound calls (HTTP, database, cache, message bus) in a configurable pipeline of protective layers. You inject one interface, `IResilienceExecutor`, and every call site becomes a single `ExecuteAsync` invocation.

Use it when:

- You have outbound calls that can fail transiently or overwhelm a dependency
- You want retry, circuit breaking, timeout, rate limiting, bulkhead isolation, and hedging available on every call site without hand-rolling each one
- You want structured events, metrics, correlation IDs, and idempotency propagation built in rather than assembled from separate packages
- You are building payment, ordering, or any write path where a duplicate attempt is worse than a failure

Do not use it when:

- You only have in-process work with no external dependency (a pure CPU function does not need resilience)
- You already have a well-tuned pipeline in a framework that does not want a second one (you can use both - they compose - but know why you are adding the second)

The library is 100% managed C# targeting `net10.0`. Zero third-party runtime dependencies.

---

## 2. Install and register

### 2.1 Install the package

    dotnet add package Portfolio.Resilience

Optional companions:

    dotnet add package Portfolio.Resilience.OpenTelemetry
    dotnet add package Portfolio.Resilience.Analyzers

Published packages:

- https://www.nuget.org/packages/Portfolio.Resilience
- https://www.nuget.org/packages/Portfolio.Resilience#versions-tab
- https://www.nuget.org/packages/Portfolio.Resilience.OpenTelemetry#versions-tab
- https://www.nuget.org/packages/Portfolio.Resilience.Analyzers#versions-tab

### 2.2 Register in Program.cs

The library is DI-first. One call registers every service.

    using Portfolio.Resilience.Extensions;
    using Portfolio.Resilience.Sinks;

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts           = 3;
            p.Retry.BaseDelayMs           = 100;
            p.Circuit.FailureThreshold    = 5;
            p.Circuit.OpenDurationSeconds = 30;
            p.Timeout.TimeoutMs           = 5000;
        }));

The lambda receives a `ResilienceBuilder`. Chain as many calls as you want:

- `.AddLogSink(ILogSink)` - register a sink (repeatable; composes into a `CompositeLogSink`)
- `.AddMetricSink(IMetricSink)` - register an additional metric sink (in-memory is always present)
- `.AddPolicy(string, Action<PolicyDefinition>)` - register a named policy
- `.UseDefaultPolicy(Action<PolicyDefinition>)` - configure the policy used for unknown names

Return type is `IServiceCollection`, so further `.AddXxx()` calls chain naturally.

### 2.3 What AddPortfolioResilience registers

After the call, the DI container can resolve:

- `IResilienceExecutor` - the entry point every call site uses
- `ICircuitBreakerMonitor` - read-only circuit state access (used by health endpoints)
- `ILatencyTracker` - p50 / p95 / p99, error rate, in-flight counts per policy
- `IResiliencePolicyRegistry` - the registry of registered policies

None of them need to be requested directly except `IResilienceExecutor` and (for observability) `ICircuitBreakerMonitor`.

### 2.4 Binding from configuration (optional)

If you prefer `appsettings.json` over fluent registration:

    using Portfolio.Resilience.Extensions;

    builder.Services.AddPortfolioResilience(r => r
        .LoadFromConfiguration(builder.Configuration, "Resilience"));

Then in `appsettings.json`:

    {
      "Resilience": {
        "Policies": {
          "auth-service": {
            "Retry": { "MaxAttempts": 3, "BaseDelayMs": 100 },
            "Circuit": { "FailureThreshold": 5, "OpenDurationSeconds": 30 },
            "Timeout": { "TimeoutMs": 5000 }
          }
        }
      }
    }

Configuration entries override on collision. Policies registered in code are preserved.

---

## 3. The executor and the ExecuteAsync contract

### 3.1 The interface

    namespace Portfolio.Resilience.Abstractions;

    public interface IResilienceExecutor
    {
        Task<T> ExecuteAsync<T>(
            string policyName,
            Func<CancellationToken, Task<T>> operation,
            Func<CancellationToken, Task<T>>? fallback = null,
            string? idempotencyKey = null,
            int? timeBudgetMs = null,
            CancellationToken ct = default);

        Task ExecuteAsync(
            string policyName,
            Func<CancellationToken, Task> operation,
            Func<CancellationToken, Task>? fallback = null,
            string? idempotencyKey = null,
            int? timeBudgetMs = null,
            CancellationToken ct = default);
    }

Two overloads. The generic one returns a value; the non-generic one returns nothing. The parameter lists are identical.

### 3.2 Parameter reference

| Parameter | Type | Default | Purpose |
|-----------|------|---------|---------|
| `policyName` | `string` | required | Which registered policy to run under |
| `operation` | `Func<CancellationToken, Task<T>>` | required | Your work; receives a token tied to pipeline cancellation |
| `fallback` | `Func<CancellationToken, Task<T>>?` | `null` | Runs outside the pipeline when the pipeline fails |
| `idempotencyKey` | `string?` | `null` | Propagated to every attempt; auto-derived if null |
| `timeBudgetMs` | `int?` | `null` | Total wall-clock budget across retries and hedges |
| `ct` | `CancellationToken` | `default` | Caller cancellation token |

Every parameter after `operation` is optional. **Always use named arguments** - positional calls silently misassign values that look alike.

### 3.3 The canonical call shape

    await _resilience.ExecuteAsync<int>(
        policyName: "payment-safe-policy",
        operation: async token =>
        {
            using var response = await _http.PostAsJsonAsync("/charge", request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<int>(cancellationToken: token);
        },
        fallback: token => Task.FromResult(-1),
        idempotencyKey: $"charge-{orderId}",
        timeBudgetMs: 3000,
        ct: ct);

This is what a real payment call looks like. Every parameter is used. Every parameter is named.

### 3.4 What happens inside ExecuteAsync

The executor does the following, in order, on every call:

1. Resolves the `PolicyDefinition` for `policyName` from the registry
2. Generates or derives an idempotency key (if none was supplied)
3. Pushes the idempotency key onto the ambient `IdempotencyContext`
4. Pushes the time budget onto the ambient `TimeBudgetContext` (if `timeBudgetMs` was supplied)
5. Runs the operation through the composite pipeline in the order the policy specifies
6. Emits `CallStarted` on entry; `CallSucceeded` or `CallFailed` on exit
7. Records the call in the metric sink (duration, success, attempts)
8. Restores the ambient contexts

The operation receives a token linked to the pipeline's internal cancellation. If you honour that token, timeouts and cancellations propagate correctly. If you ignore it, the operation runs to completion regardless.

### 3.5 Where the operation must honour the token

Inside the operation lambda, pass the token everywhere:

    operation: async token =>
    {
        // correct - token flows into the HTTP call
        return await _http.GetFromJsonAsync<UserDto>("/users/me", token);
    },

Not:

    operation: async token =>
    {
        // wrong - CancellationToken.None ignores pipeline cancellation
        return await _http.GetFromJsonAsync<UserDto>("/users/me", CancellationToken.None);
    },

If the token is not honoured, the timeout and time-budget layers cannot cancel the operation early. The pipeline will still return control after the ceiling fires, but the underlying work keeps running - wasting resources and possibly duplicating side effects.

### 3.6 What throws and when

The pipeline throws `ResilienceException` when all layers exhaust and no fallback is present. The exception carries:

- `Category` - `Transient`, `Permanent`, `CircuitOpen`, `Timeout`, `FallbackUsed`, or `Unknown`
- `PolicyName` - the policy that ran
- `AttemptsMade` - how many times the operation was invoked
- `TotalDuration` - wall-clock time across all layers
- `CorrelationId` - the ambient correlation ID at the time of the call
- `InnerException` - the last raw exception from the operation

Catch by category when the response shape depends on the failure kind:

    try
    {
        var charge = await _resilience.ExecuteAsync<ChargeResult>(
            policyName: "payment-safe-policy",
            operation: ChargeAsync,
            idempotencyKey: id,
            ct: ct);
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.CircuitOpen)
    {
        // fail fast - the dependency is known to be down
        return Results.Json(new { error = "payment provider temporarily unavailable" }, statusCode: 503);
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Timeout)
    {
        // the ceiling fired - the operation may have completed on the provider side
        // if an idempotency key was sent, a retry is safe
        return Results.Json(new { error = "payment request timed out" }, statusCode: 504);
    }
    catch (ResilienceException ex)
    {
        _logger.LogError(ex, "charge failed for order {OrderId}", orderId);
        return Results.Json(new { error = "payment failed" }, statusCode: 502);
    }

That catches every exit shape the pipeline produces.

### 3.7 The fallback - when and how

The fallback runs **outside** the pipeline, after the pipeline has already failed. It is a per-call degraded response, not a retry. It is the last chance to return something useful.

    var user = await _resilience.ExecuteAsync<UserDto?>(
        policyName: "auth-service",
        operation: FetchUserAsync,
        fallback: _ => Task.FromResult<UserDto?>(UserDto.Anonymous),
        ct: ct);

If the operation fails and a fallback is provided, the pipeline emits `FallbackUsed` and returns the fallback's result. If the fallback itself throws, the pipeline wraps the fallback exception in a `ResilienceException` and rethrows.

Fallbacks are per-call, not per-policy. Two call sites under the same policy can have different fallbacks, or none at all.

### 3.8 The non-generic overload

For operations that return nothing:

    await _resilience.ExecuteAsync(
        policyName: "event-publisher",
        operation: async token => await _publisher.PublishAsync(evt, token),
        idempotencyKey: evt.Id,
        ct: ct);

Same shape. Same semantics. The library wraps the operation in a generic call that returns `null` and unwraps the result transparently.
---

## 4. Retry

The retry layer absorbs transient failures. When the operation throws an exception that is classified as transient, the layer waits and invokes the operation again, up to `MaxAttempts` total invocations.

### 4.1 The options

    p.Retry.MaxAttempts  = 3;       // total attempts including the first
    p.Retry.BaseDelayMs  = 100;     // first retry delay; subsequent delays multiply
    p.Retry.MaxDelayMs   = 5000;    // cap on the exponential backoff
    p.Retry.JitterRatio  = 0.2;     // 0.0 to 1.0 - adds randomness to delay

All four are optional. Defaults: `MaxAttempts = 3`, `BaseDelayMs = 100`, `MaxDelayMs = 5000`, `JitterRatio = 0.0`.

**`MaxAttempts` means total attempts, not retries.** `MaxAttempts = 3` allows up to 3 invocations of the operation: the first, plus two retries. `MaxAttempts = 1` means one invocation, no retry.

### 4.2 The backoff formula

The delay before retry `n` (1-based, where `n = 1` is the first retry) is:

    delay(n) = min(BaseDelayMs * 2^(n-1), MaxDelayMs) + jitter(0, JitterRatio * BaseDelayMs)

Worked example with `BaseDelayMs = 100`, `MaxDelayMs = 5000`, `JitterRatio = 0.0`:

- Retry 1 (after first failure): min(100 * 1, 5000) + 0 = **100ms**
- Retry 2 (after second failure): min(100 * 2, 5000) + 0 = **200ms**
- Retry 3: min(100 * 4, 5000) + 0 = **400ms**
- Retry 4: min(100 * 8, 5000) + 0 = **800ms**
- Retry 5: min(100 * 16, 5000) + 0 = **1600ms**
- Retry 6: min(100 * 32, 5000) + 0 = **3200ms**
- Retry 7: min(100 * 64, 5000) + 0 = **5000ms** (capped)
- Retry 8+: **5000ms** (capped)

With `JitterRatio = 0.2`, each delay gains a random 0–20ms addition (20% of `BaseDelayMs`). The jitter helps prevent synchronized retries across replicas from forming a thundering herd.

### 4.3 Classification gating - only transient errors retry

The library classifies every exception into one of six categories. Retry fires only for:

- `ResilienceErrorCategory.Transient` - network blips, connection resets, HTTP 5xx
- `ResilienceErrorCategory.Timeout` - your operation's own timeout threw

Permanent errors (400 responses, `ArgumentException`, validation failures) fail immediately. Retrying a validation failure wastes time and load. The classifier is what makes this decision.

If you need a policy that retries permanent errors as well:

    p.Retry.RetryOnPermanent = true;    // only for very specific cases

**Use this sparingly.** A 400 will always be a 400 on retry. Only use it if your downstream returns spurious 4xx under load.

### 4.4 Canonical example - transient HTTP failure

    public Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct) =>
        _resilience.ExecuteAsync<UserDto?>(
            policyName: "auth-service",
            operation: async token =>
            {
                using var response = await _http.GetAsync($"/users/{id}", token);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable
                    || response.StatusCode == HttpStatusCode.GatewayTimeout)
                {
                    // throws an exception classified as Transient
                    response.EnsureSuccessStatusCode();
                }
                return await response.Content.ReadFromJsonAsync<UserDto>(cancellationToken: token);
            },
            ct: ct);

If the first call returns 503, the retry layer waits 100ms and tries again. If the second succeeds, the caller never sees the failure. If the third also fails, the pipeline throws a `ResilienceException` with `Category = Transient` after 3 attempts.

### 4.5 Reading the retry from the event stream

If you want to observe retries from the events, note that `RetryAttempted` events are emitted by the emitter but are **not delivered to `ILogSink` implementations in 0.8.0** (see the sample README's findings). The correct way to observe retries is:

- `CallSucceeded.Attempt` carries the total number of attempts made
- `CallFailed.AttemptsMade` carries the same number on failure
- The metric sink's `RecordCall(policy, duration, success, attempts)` records the same value

Example using the metric sink:

    var metrics = provider.GetRequiredService<InMemoryMetricSink>();
    var snapshot = metrics.Get("auth-service");
    Console.WriteLine($"p95 = {snapshot.P95Ms}ms, error rate = {snapshot.ErrorRate:P1}, total calls = {snapshot.TotalCalls}");

### 4.6 Tuning guidance

| Situation | Suggested configuration |
|-----------|------------------------|
| Network calls to a healthy service | `MaxAttempts = 3`, `BaseDelayMs = 50`, `JitterRatio = 0.3` |
| Network calls to a flaky service | `MaxAttempts = 5`, `BaseDelayMs = 100`, `MaxDelayMs = 5000`, `JitterRatio = 0.2` |
| Database writes on a primary | `MaxAttempts = 2`, `BaseDelayMs = 100`, `JitterRatio = 0.0` |
| Cache reads | `MaxAttempts = 3`, `BaseDelayMs = 10` |
| Payment writes | `MaxAttempts = 3`, `BaseDelayMs = 100`, always with an idempotency key |
| External API with rate limits | `MaxAttempts = 2`, `BaseDelayMs = 1000`, plus a rate limiter |

**Rule of thumb:** retry more aggressively on reads, less on writes. Duplicate writes are worse than slower reads.

### 4.7 What retry does not do

- **Retry does not bypass the circuit.** If the circuit is open, retry cannot invoke the operation. See Section 14 for the payment-safe order, where the circuit sits outside retry for this exact reason.
- **Retry does not retry timeouts that are the caller's cancellation.** If the caller's `CancellationToken` fired, the pipeline rethrows `OperationCanceledException` immediately. No retry, no circuit count.
- **Retry does not count failures differently based on the failure type.** Every failed attempt counts toward the attempt budget. A 5xx and a 4xx both consume one attempt, even though only the 5xx is retried.

### 4.8 Interaction with the time budget

If a time budget is active (`timeBudgetMs` was supplied to `ExecuteAsync`), the retry layer checks the remaining budget before each retry. If the next attempt's delay cannot fit within the remaining time, the retry layer stops and surfaces the last failure instead of delaying.

Concretely, with `BaseDelayMs = 100` and a 300ms budget: after the first failure, the retry layer computes delay = 100ms + jitter. Remaining budget ≈ 200ms. 200ms < 100ms + 100ms = 200ms - borderline. The retry layer's check is `remaining < delay + BaseDelayMs`. If remaining is exactly 200, the check is `200 < 200` = false, so one retry fires. After the second failure, delay = 200ms. Remaining ≈ 100ms. 100 < 200 + 100 = 300 = true. Stop retrying.

The result: at most 2 attempts under a 300ms budget with 100ms base delay. See scenario 09 in the sample for a runnable example.
---

## 5. Circuit breaker

The circuit breaker fails fast when a dependency is genuinely broken. Instead of letting every caller wait for a timeout, the circuit "opens" after a threshold of consecutive failures and rejects subsequent calls instantly - without invoking the operation at all.

### 5.1 The three states

    Closed    - normal operation. Calls pass through. Failures increment a counter.
    Open      - reject everything. No calls reach the operation.
    HalfOpen  - probe mode. A limited number of calls are allowed through to test recovery.

### 5.2 The state machine

    Closed  --(N consecutive failures)-->  Open
    Open    --(OpenDurationSeconds elapsed)-->  HalfOpen
    HalfOpen --(probe succeeds)-->              Closed
    HalfOpen --(probe fails)-->                 Open

Every transition is deterministic and thread-safe.

### 5.3 The options

    p.Circuit.FailureThreshold      = 5;      // consecutive failures to open
    p.Circuit.OpenDurationSeconds   = 30;     // time Open before HalfOpen probe
    p.Circuit.SuccessThreshold      = 1;      // successes in HalfOpen to close
    p.Circuit.OnlyCountTransient    = true;   // see 5.4

Defaults: `FailureThreshold = 5`, `OpenDurationSeconds = 30`, `SuccessThreshold = 1`, `OnlyCountTransient = true`.

### 5.4 The OnlyCountTransient trap

**This is the most common surprise with the circuit.** By default, only exceptions classified as `Transient` count toward `FailureThreshold`. A `Permanent` failure (400 response, `ArgumentException`, validation failure) does not count. The circuit does not open.

If your operation throws `InvalidOperationException` on every call and the circuit never opens, this is why.

Two ways to fix it:

1. Set `p.Circuit.OnlyCountTransient = false;` - all failures count, regardless of category
2. Ensure your operation throws a transient-classified exception (`TimeoutException`, `HttpRequestException`, custom transient exception registered with the classifier)

For a circuit that should open on any failure:

    p.Circuit.FailureThreshold   = 3;
    p.Circuit.OpenDurationSeconds = 30;
    p.Circuit.OnlyCountTransient = false;

For a circuit that should only open on infrastructure failures (and ignore validation errors):

    p.Circuit.OnlyCountTransient = true;   // the default

### 5.5 Canonical example - circuit-protected dependency

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("payment-provider", p =>
        {
            p.Retry.MaxAttempts           = 1;     // no retry - circuit handles failures
            p.Circuit.FailureThreshold    = 3;
            p.Circuit.OpenDurationSeconds = 30;
            p.Circuit.SuccessThreshold    = 1;
            p.Circuit.OnlyCountTransient  = true;
            p.Timeout.TimeoutMs           = 5000;
        }));

Then every call to `payment-provider` runs through the circuit. After 3 consecutive transient failures, the circuit opens. Calls during the next 30 seconds are rejected instantly with `Category = CircuitOpen`.

### 5.6 Catching circuit rejections

    try
    {
        var result = await _resilience.ExecuteAsync<PaymentResult>(
            policyName: "payment-provider",
            operation: ChargeAsync,
            idempotencyKey: orderId,
            ct: ct);
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.CircuitOpen)
    {
        // no attempt was made - the circuit is open
        // safe to queue for later, return a 503, or fall back to an alternate provider
    }

An `CircuitOpen` rejection carries `AttemptsMade = 0` because the operation was never invoked. That is how you distinguish it from a failure that actually reached the provider.

### 5.7 Reading circuit state at runtime

The `ICircuitBreakerMonitor` is resolvable from DI and gives read-only access to every circuit's state.

    using Portfolio.Resilience.Abstractions;

    public sealed class HealthController
    {
        private readonly ICircuitBreakerMonitor _monitor;
        private readonly ILatencyTracker _latency;

        public HealthController(ICircuitBreakerMonitor monitor, ILatencyTracker latency)
        {
            _monitor = monitor;
            _latency = latency;
        }

        [HttpGet("/health/resilience")]
        public IActionResult Get()
        {
            var circuits = _monitor.Snapshot()
                .Select(c => new
                {
                    name = c.PolicyName,
                    state = c.State.ToString(),
                    failures = c.ConsecutiveFailures,
                    openedAt = c.OpenedAtUtc,
                    nextProbe = c.NextProbeAtUtc
                });

            var policies = _latency.Snapshot()
                .Select(p => new
                {
                    name = p.PolicyName,
                    p50 = p.P50Ms,
                    p95 = p.P95Ms,
                    p99 = p.P99Ms,
                    errorRate = p.ErrorRate,
                    inFlight = p.InFlight
                });

            return Ok(new { circuits, policies });
        }
    }

### 5.8 The CircuitState enum

    public enum CircuitState
    {
        Closed = 0,
        Open = 1,
        HalfOpen = 2
    }

### 5.9 The CircuitSnapshot record

    public sealed record CircuitSnapshot(
        string PolicyName,
        CircuitState State,
        int ConsecutiveFailures,
        DateTime? OpenedAtUtc,
        DateTime? NextProbeAtUtc);

### 5.10 The circuit is per-policy, not per-call

Every call to `ExecuteAsync(policyName: "X", ...)` shares the same circuit for policy `X`. If ten different methods in your code all use `"payment-provider"`, they all contribute failures to the same circuit and are all rejected when it opens.

That is intentional. The circuit reflects the health of the **dependency**, not of a single method. If you want per-method isolation, use per-method policy names.

### 5.11 Circuit and retry - the ordering matters

The default pipeline order is:

    RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

Circuit sits **inside** retry. When retry fires another attempt, it goes through the circuit again. If the circuit is Closed, the retry reaches the operation. If the circuit opens mid-retry, subsequent retries are rejected.

The payment-safe order is:

    RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation

Circuit sits **outside** retry and hedging. Once the circuit opens, no retries and no hedges spawn. This prevents duplicate writes. See Section 14.

### 5.12 Tuning guidance

| Dependency health | Suggested configuration |
|-------------------|------------------------|
| Healthy service with occasional blips | `FailureThreshold = 10`, `OpenDurationSeconds = 15`, `SuccessThreshold = 2` |
| New service with unstable reliability | `FailureThreshold = 3`, `OpenDurationSeconds = 30`, `SuccessThreshold = 1` |
| Payment provider with strict SLAs | `FailureThreshold = 3`, `OpenDurationSeconds = 60`, `OnlyCountTransient = false` |
| Internal service behind a load balancer | `FailureThreshold = 20`, `OpenDurationSeconds = 10` |

**Rule of thumb:** open earlier if failures are expensive (payments); open later if the dependency is usually fine and blips are rare.

### 5.13 What the circuit does not do

- **Does not retry the operation.** Retry is a separate layer. Compose them (or not) as you see fit.
- **Does not distinguish between callers.** If the circuit is open, every caller is rejected, not just the one that caused the failures.
- **Does not persist state across process restarts.** Each process has its own circuit state. For distributed circuit state across replicas, see the roadmap in the root README (deferred to a future release).
- **Does not reject permanently.** After `OpenDurationSeconds`, the circuit transitions to HalfOpen and tries a probe. If the probe succeeds, it closes. If not, it opens again.

### 5.14 Interaction with the time budget

If a time budget is active, the circuit still functions independently. A `CircuitOpen` rejection happens before any timeout logic because the operation is never invoked. The budget's effect on the circuit is negligible - the circuit is about attempt admission, not about attempt duration.
---

## 6. Timeout

The timeout layer bounds every attempt. If the operation does not complete within `TimeoutMs`, its cancellation token is signaled and a `ResilienceException` with `Category = Timeout` is thrown.

### 6.1 The option

    p.Timeout.TimeoutMs = 5000;     // per-attempt ceiling, in milliseconds

Default: `TimeoutMs = 5000`. Set to `0` or negative to disable the ceiling - the operation runs until the caller's token fires or the operation completes.

### 6.2 Per-attempt, not per-pipeline

`TimeoutMs` bounds **each attempt**. Under retry, a policy with `TimeoutMs = 5000` and `MaxAttempts = 3` can take up to 15 seconds of wall-clock time (3 attempts × 5 seconds each). That is intentional: the ceiling protects against a single stalled attempt, not against the total pipeline duration.

If you need a total ceiling, use the time budget (Section 13).

### 6.3 Canonical example

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("slow-dependency", p =>
        {
            p.Timeout.TimeoutMs  = 2000;    // 2 seconds per attempt
            p.Retry.MaxAttempts  = 3;
            p.Retry.BaseDelayMs  = 100;
        }));

Every invocation of the operation gets at most 2 seconds. If it stalls longer, the timeout fires, the operation is cancelled, and a `Timeout` categorized exception surfaces to the retry layer. If the operation throws a transient exception inside that window, retry fires normally.

### 6.4 Caller cancellation vs. our timeout

Two different cancellation signals can fire:

- **Caller cancellation** - the `ct` passed to `ExecuteAsync` was cancelled by the caller (user closed the browser, request aborted, parent task cancelled)
- **Our timeout** - the per-attempt ceiling elapsed

The timeout layer distinguishes them. If the caller's token fires first, the pipeline rethrows `OperationCanceledException` unchanged. It does **not** wrap the exception, does **not** retry, and does **not** count against the circuit. The caller's cancellation is treated as a real "stop" signal, not as a failure.

If our timer fires first, the pipeline throws `ResilienceException` with `Category = Timeout`. Retry may fire. Circuit may count the failure.

### 6.5 Handling timeouts at the boundary

    try
    {
        var result = await _resilience.ExecuteAsync<Result>(
            policyName: "slow-dependency",
            operation: SlowOperation,
            ct: ct);
    }
    catch (OperationCanceledException)
    {
        // the caller's token fired - propagate as-is
        throw;
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Timeout)
    {
        // our timeout fired - the operation may have partially completed
        // if the operation is a write, an idempotency key makes retry safe
        return Results.Json(new { error = "request timed out" }, statusCode: 504);
    }

### 6.6 The operation must honour the token

The timeout layer can only cancel the operation if the operation passes its token through to the underlying work:

    operation: async token =>
    {
        // correct - the token flows into the HTTP call
        return await _http.GetAsync("/slow-endpoint", token);
    }

Not:

    operation: async token =>
    {
        // wrong - ignores the token, runs to completion regardless
        await Task.Delay(10000);
        return await _http.GetAsync("/slow-endpoint");
    }

If the token is ignored, the timeout fires from the caller's perspective, but the underlying work continues. That means:

- Resource leak - the work holds threads, connections, memory after the caller has given up
- Duplicate side effects - the operation may complete a write after the caller saw a timeout

Always honour the token.

### 6.7 What timeout does not do

- **Does not bound the total pipeline.** Use time budget for that.
- **Does not override caller cancellation.** If the caller's token fires, no retry, no circuit count.
- **Does not throw `TaskCanceledException` or `TimeoutException` on its own.** It throws `ResilienceException` with `Category = Timeout`. This is consistent across the library.

### 6.8 Interaction with other layers

| Layer | Interaction |
|-------|-------------|
| Retry | Each retry gets a fresh `TimeoutMs` window |
| Circuit | A timeout counts as a `Timeout`-classified failure; whether it opens the circuit depends on `OnlyCountTransient` |
| Hedging | Each hedged attempt gets its own `TimeoutMs` window |
| Time budget | If a budget is active, the effective timeout is `min(TimeoutMs, remaining budget)` |

---

## 7. Rate limiter

The rate limiter caps how many calls may proceed in a given time period. Rejections are fast and local - the dependency never sees the rejected request. This protects downstream services from bursts and keeps your service within negotiated API quotas.

### 7.1 Enable and configure

    p.RateLimiter.Enabled       = true;
    p.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;
    p.RateLimiter.PermitLimit   = 100;
    p.RateLimiter.WindowSeconds = 60;
    p.RateLimiter.QueueLimit    = 0;         // 0 = reject immediately
    p.RateLimiter.QueueTimeoutMs = 0;

The rate limiter is disabled by default. If you do not set `Enabled = true`, it is a pass-through.

### 7.2 The four strategies

**`TokenBucket`** - smooth refill, allows bursts up to capacity.

- `PermitLimit` = bucket capacity
- `WindowSeconds` = refill period
- Tokens refill continuously. A call consumes one token. If no tokens remain, the call is rejected.
- Use when: you want to allow occasional bursts but cap the long-term rate.

Example: `PermitLimit = 10, WindowSeconds = 60` - 10 tokens per minute, replenished continuously. A burst of 10 at t=0 succeeds; a burst of 20 at t=0 succeeds only for 10 (the bucket was full). After 1 minute, the bucket is full again.

**`SlidingWindow`** - precise, no boundary effects. **This is the default.**

- `PermitLimit` = max calls in any rolling `WindowSeconds` window
- Use when: you want an exact count with no edge cases.

Example: `PermitLimit = 100, WindowSeconds = 60` - at most 100 calls in any 60-second window, checked continuously.

**`FixedWindow`** - cheapest to implement, allows up to 2x burst at boundaries.

- `PermitLimit` = max calls per fixed `WindowSeconds` bucket
- Use when: cost matters more than precision, and 2x burst at window edges is acceptable.

Example: `PermitLimit = 100, WindowSeconds = 60` - 100 calls per calendar minute. A burst of 100 at 12:00:59 and another 100 at 12:01:00 both succeed, giving 200 calls in one second.

**`ConcurrencyLimit`** - caps simultaneous calls, not rate.

- `PermitLimit` = max simultaneous calls
- `WindowSeconds` is **ignored**
- Use when: you want to prevent thread pool exhaustion by a slow dependency, not limit total volume.

Example: `PermitLimit = 20` - at most 20 calls in flight at once. Sequential bursts never trigger the limit; only concurrent overlap does.

### 7.3 The rejection category

When a call is rejected, the pipeline throws `ResilienceException` with `Category` set to `RateLimiterOptions.RejectionCategory`. Default is `Transient`.

You can override:

    p.RateLimiter.RejectionCategory = ResilienceErrorCategory.Permanent;   // rate-limit rejections are "permanent" from the caller's perspective
    p.RateLimiter.RejectionCategory = ResilienceErrorCategory.Unknown;    // for callers that want a distinct handling path

### 7.4 The queue (optional)

By default, rejections are immediate - the call fails the moment no permit is available. If you want rejected calls to wait briefly, configure a queue:

    p.RateLimiter.QueueLimit    = 10;     // up to 10 calls may wait
    p.RateLimiter.QueueTimeoutMs = 500;   // wait up to 500ms before rejecting

With this configuration, up to 10 calls can wait in a queue when no permit is available. Each waits up to 500ms. If a permit frees within the window, the call proceeds. Otherwise, it is rejected.

**Most policies should leave `QueueLimit = 0`.** Queuing changes behavior in subtle ways - a caller thinks it is proceeding immediately, but may wait up to 500ms. Use the queue only when the downstream can tolerate it and the caller's SLA allows it.

### 7.5 Canonical examples - one per strategy

**TokenBucket** - allow bursts to a partner API that meters per minute:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("partner-api", p =>
        {
            p.RateLimiter.Enabled       = true;
            p.RateLimiter.Strategy      = RateLimitStrategy.TokenBucket;
            p.RateLimiter.PermitLimit   = 100;
            p.RateLimiter.WindowSeconds = 60;
            p.Timeout.TimeoutMs         = 5000;
        }));

**SlidingWindow** - strictly respect a vendor's contract:

    p.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;
    p.RateLimiter.PermitLimit   = 1000;
    p.RateLimiter.WindowSeconds = 3600;   // 1000 per hour

**FixedWindow** - cheap rate limiting with acceptable burst tolerance:

    p.RateLimiter.Strategy      = RateLimitStrategy.FixedWindow;
    p.RateLimiter.PermitLimit   = 100;
    p.RateLimiter.WindowSeconds = 60;

**ConcurrencyLimit** - cap concurrent calls to a database:

    p.RateLimiter.Strategy      = RateLimitStrategy.ConcurrencyLimit;
    p.RateLimiter.PermitLimit   = 20;     // 20 in-flight max

### 7.6 Catching rate-limit rejections

    try
    {
        await _resilience.ExecuteAsync<int>(
            policyName: "partner-api",
            operation: CallPartnerAsync,
            ct: ct);
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Transient)
    {
        // could be rate limit, could be a transient network failure - the classifier saw Transient
        // for exact detection, inspect the exception metadata or configure RejectionCategory
    }

Rate-limit rejections are indistinguishable from other transient failures at the exception level unless you set a distinct `RejectionCategory`.

**To distinguish clearly:**

    p.RateLimiter.RejectionCategory = ResilienceErrorCategory.Unknown;

Then:

    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Unknown)
    {
        // this was a rate-limit rejection
    }

### 7.7 The RateLimited event

Every rate-limit rejection emits a `RateLimited` event. The event carries the strategy, permit limit, window, queue depth, and reason in its metadata. See Section 17 for how to read events.

### 7.8 Per-policy vs. per-process

The rate limiter is scoped **per-policy per-process**. Every call to `ExecuteAsync(policyName: "partner-api", ...)` in this process shares the same limiter. Two policies with the same name in two different processes have independent limiters.

If you need a shared rate limiter across replicas, that is a distributed-systems problem outside the scope of this library. A Redis-backed limiter is the usual solution.

### 7.9 Tuning guidance

| Use case | Strategy | PermitLimit | WindowSeconds |
|----------|----------|-------------|---------------|
| External partner API with monthly quota | SlidingWindow | contract amount | period in seconds |
| Internal service behind a load balancer | SlidingWindow | 1000 | 60 |
| Database write path | ConcurrencyLimit | 20-50 | ignored |
| Cache with token-bucket semantics | TokenBucket | cache capacity | refill period |
| Cheapest option for coarse rate limit | FixedWindow | any | any |

**Rule of thumb:** rate limiters protect the dependency; bulkheads protect you. Use both.

### 7.10 What the rate limiter does not do

- **Does not queue by default.** `QueueLimit = 0` means immediate rejection.
- **Does not distinguish callers.** Every call to the same policy shares the same limit.
- **Does not persist across process restarts.** State is in-memory.
- **Does not distribute across replicas.** Each process has its own limits.
---

## 8. Bulkhead

The bulkhead caps how many calls may run concurrently against a resource, and how many callers may wait for a slot. It protects the caller's thread pool from being exhausted by a slow dependency.

The name comes from shipbuilding: a bulkhead is a partition that keeps a flooded compartment from sinking the whole ship. In software, the bulkhead keeps one slow dependency from starving threads that other dependencies need.

### 8.1 The options

    p.Bulkhead.Enabled        = true;
    p.Bulkhead.MaxConcurrency = 20;      // simultaneous calls allowed
    p.Bulkhead.MaxQueue       = 10;      // additional callers that may wait
    p.Bulkhead.QueueTimeoutMs = 2000;    // max wait before rejection

Disabled by default. `Enabled = true` turns it on.

### 8.2 How a call flows

    Caller
      |
      +-- slot available?         -> run immediately
      +-- no slot, queue space?   -> wait up to QueueTimeoutMs
      +-- no slot, queue full?    -> reject immediately (BulkheadRejected)

The semaphore is held across the entire operation and released in a `finally` block, so slots return whether the operation succeeds, fails, or is cancelled.

### 8.3 Canonical example

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("database", p =>
        {
            p.Bulkhead.Enabled        = true;
            p.Bulkhead.MaxConcurrency = 20;
            p.Bulkhead.MaxQueue       = 50;
            p.Bulkhead.QueueTimeoutMs = 2000;
            p.Timeout.TimeoutMs       = 5000;
        }));

With this configuration:

- Up to 20 database queries run concurrently
- Up to 50 additional queries wait in the queue
- A queued query waits up to 2 seconds for a slot
- Queries beyond the 70th are rejected immediately
- Each running query is bounded by the 5-second timeout

### 8.4 The BulkheadRejected event

Every bulkhead rejection emits a `BulkheadRejected` event. The event carries `MaxConcurrency`, `ActiveCount`, `QueueDepth`, and a `Reason` in metadata.

### 8.5 Rejection category

    p.Bulkhead.RejectionCategory = ResilienceErrorCategory.Transient;    // default
    p.Bulkhead.RejectionCategory = ResilienceErrorCategory.Permanent;

Same mechanism as the rate limiter. Set a distinct category if callers need to distinguish bulkhead rejections from other transient failures.

### 8.6 Catching rejections

    try
    {
        await _resilience.ExecuteAsync<int>(
            policyName: "database",
            operation: QueryAsync,
            ct: ct);
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Transient)
    {
        // bulkhead rejection or transient network failure - indistinguishable at the exception level
        // set a distinct RejectionCategory on the bulkhead to distinguish
    }

### 8.7 Bulkhead vs. rate limiter (ConcurrencyLimit)

Both cap concurrency. The difference:

| Aspect | Bulkhead | Rate limiter (ConcurrencyLimit) |
|--------|----------|--------------------------------|
| Purpose | Protect the caller's thread pool | Protect the dependency |
| Queue | Explicit bounded queue with timeout | No queue (reject immediately) |
| Events | `BulkheadRejected` | `RateLimited` |
| Typical position | Middle of the pipeline | Outer edge of the pipeline |

**Use the bulkhead for the caller's protection. Use the rate limiter for the dependency's protection.** In a real pipeline, you may want both.

### 8.8 Tuning guidance

| Situation | MaxConcurrency | MaxQueue | QueueTimeoutMs |
|-----------|----------------|----------|----------------|
| Database connection pool | pool size | pool size * 2 | 1000-2000 |
| External API with strict concurrency | contract amount | 0 (no queue) | 0 |
| Internal service behind LB | 50-100 | 100 | 3000 |
| File I/O or disk-bound work | 4-8 | 8 | 1000 |

**Rule of thumb:** `MaxConcurrency` should match the resource's real capacity. If your database has 20 connections, use 20.

### 8.9 What the bulkhead does not do

- **Does not prioritize.** Every caller competes for the same slots.
- **Does not persist across process restarts.** In-memory semaphore.
- **Does not distribute.** Each process has its own bulkhead.
- **Does not cancel queued callers if the running call fails.** The queued caller waits its turn regardless.

### 8.10 Interaction with other layers

| Layer | Interaction |
|-------|-------------|
| Timeout | Applies per-attempt, starting when the operation begins (after slot acquisition) |
| Retry | Each retry attempt acquires a slot fresh |
| Circuit | Circuit rejects before bulkhead is consulted |
| Rate limiter | Rate limiter sits outside bulkhead in the default order |

---

## 9. Hedging

Hedging fires **parallel attempts** of the same operation with a stagger delay between them. The first attempt to succeed wins. Losers are cancelled (or allowed to finish, depending on configuration).

Hedging is a **latency optimization**, not a reliability one. It reduces p99 latency by racing a slow primary attempt against a fresh hedge. It does not absorb transient failures - use retry for that.

### 9.1 The options

    p.Hedging.Enabled           = true;
    p.Hedging.MaxAttempts       = 2;       // total attempts including the primary
    p.Hedging.DelayMs           = 50;      // stagger delay before each hedge
    p.Hedging.ExponentialBackoff = false;  // if true, delay doubles per attempt
    p.Hedging.AttemptTimeoutMs  = 0;       // 0 = no per-attempt ceiling
    p.Hedging.CancelOnSuccess   = true;    // default - cancel losers when winner found
    p.Hedging.RejectionCategory = ResilienceErrorCategory.Transient;

Disabled by default. Hedging is not safe for non-idempotent operations - see 9.6.

### 9.2 How a call flows

    t=0ms     primary attempt starts
    t=50ms    if primary still running, hedge 1 starts
    t=100ms   if primary and hedge 1 still running, hedge 2 starts
    t=?       first attempt to succeed wins; losers are cancelled

The primary starts immediately. Each hedge waits `DelayMs` (or `DelayMs * 2^n` with `ExponentialBackoff`) before checking whether a prior attempt has already won. If so, the hedge never fires.

### 9.3 Canonical example - fast reads

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("cache-read", p =>
        {
            p.Hedging.Enabled    = true;
            p.Hedging.MaxAttempts = 3;
            p.Hedging.DelayMs     = 30;
            p.Timeout.TimeoutMs   = 500;
        }));

A cache lookup that returns in 5ms normally completes on the primary. A cache lookup that takes 40ms (rare) has a hedge fired at 30ms that completes faster. The caller sees the fast response.

**The purpose:** smooth the p99. Slow-but-not-broken reads get a second chance without waiting for the first to finish or time out.

### 9.4 When hedging is the right tool

- Idempotent reads: `GET` requests, cache lookups, database selects, external API queries
- Operations where a duplicate attempt has no side effects
- Operations where a fast response matters more than a slightly higher load on the dependency

### 9.5 When hedging is the wrong tool

- Non-idempotent writes: charges, refunds, order submissions, event publishes
- Operations where the downstream cannot deduplicate
- Operations that are cheap on the caller but expensive on the dependency

**A hedged `POST /charge` can create two charges.** The library logs a warning at startup when a policy enables hedging, precisely to flag this.

### 9.6 The idempotency key - hedging's safety net

If you must hedge a write, combine it with an idempotency key:

    await _resilience.ExecuteAsync<int>(
        policyName: "payment-provider",
        operation: ChargeAsync,
        idempotencyKey: $"charge-{orderId}",
        ct: ct);

Every hedged attempt carries the same key. A downstream provider that deduplicates on the key will reject the duplicate, so the second charge never lands.

**But this requires the downstream to deduplicate.** If your provider does not honour idempotency keys, do not hedge the write.

### 9.7 CancelOnSuccess and losers

By default, when one attempt wins, the others are cancelled:

    p.Hedging.CancelOnSuccess = true;    // default

If you have a rare case where losers should finish anyway (for metrics, or because the operation is cheap):

    p.Hedging.CancelOnSuccess = false;

With `false`, losers continue running. The caller still gets the winner's result. Use this only if the operation is idempotent and completing the losers is harmless.

### 9.8 Per-attempt timeout

    p.Hedging.AttemptTimeoutMs = 500;

If set, each hedged attempt is individually bounded. When `0` (the default), each attempt runs until the pipeline's own timeout fires.

### 9.9 Hedge events

Three events fire during a race:

- `HedgeWon` - an attempt succeeded first
- `HedgeLost` - an attempt completed after the winner; its result was discarded
- `HedgeCancelled` - an attempt was cancelled when the winner completed

See Section 17 for how to read events.

### 9.10 Tuning guidance

| Situation | MaxAttempts | DelayMs |
|-----------|-------------|---------|
| Cache with p99 spikes | 2 | 30 |
| External read with slow tail | 3 | 50 |
| Internal service with stable latency | do not hedge | n/a |
| Write path | do not hedge without an idempotency key | n/a |

**Rule of thumb:** hedge only when the p99 is meaningfully worse than the median. If your p50 and p99 are both 5ms, hedging does nothing. If p50 = 5ms and p99 = 500ms, hedging can help.

### 9.11 What hedging does not do

- **Does not retry failures.** If the primary fails, the hedge is not "retry". Hedging fires on **time**, not on failure.
- **Does not wait for the operation to time out.** The hedge fires after `DelayMs`, not after the timeout ceiling.
- **Does not guarantee the loser stops.** If the operation ignores cancellation, the loser runs to completion. Honour the token.
- **Does not protect against non-idempotent writes.** See 9.5.

### 9.12 Hedging and the pipeline order

In the default pipeline, hedging sits **outside** retry:

    RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

That means: if hedging fires two attempts and both fail, retry may still fire additional attempts inside each. That is intentional - hedging smooths the tail, retry absorbs failures.

In the payment-safe pipeline, hedging sits **inside** circuit:

    RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation

Circuit opens before hedging fires, so once a dependency is unhealthy, no hedges spawn.
---

## 10. Fallback

A fallback is a degraded response returned when the pipeline fails. It is a per-call argument, not a policy option. Two calls under the same policy can have different fallbacks or none.

### 10.1 The shape

    fallback: Func<CancellationToken, Task<T>>?

The fallback is a function that returns the same type as the operation. The pipeline invokes it after all layers have exhausted and the pipeline has failed.

### 10.2 Canonical example

    var user = await _resilience.ExecuteAsync<UserDto?>(
        policyName: "auth-service",
        operation: async token => await _http.GetFromJsonAsync<UserDto>($"/users/{id}", token),
        fallback: _ => Task.FromResult<UserDto?>(UserDto.Anonymous),
        ct: ct);

If the operation succeeds, the fallback is never called. If the operation fails after retries (and, if configured, after circuit, timeout, rate limit, bulkhead), the fallback runs and its result is returned to the caller as if it were the operation's success.

### 10.3 The FallbackUsed event

When a fallback runs, the pipeline emits `FallbackUsed`. The event's metadata carries the reason (the classified category of the pipeline failure).

### 10.4 What happens if the fallback throws

The pipeline wraps the fallback's exception in a `ResilienceException` with the original category, sets `InnerException` to the fallback's exception, and adds a `fallback_error` key to metadata. The caller sees a normal `ResilienceException`.

### 10.5 When to use a fallback

- Read paths where a degraded response is acceptable: user profile, product catalog, recommendation list
- Operations where an alternate data source exists: primary cache down, fall back to database
- Endpoints where returning "unknown" beats returning an error

### 10.6 When not to use a fallback

- Payment writes: returning a fake "success" would corrupt state. Let the error propagate.
- Any operation where the caller must know the true outcome
- Operations where the fallback would mask a systemic problem

### 10.7 Fallback does not run under caller cancellation

If the caller's token fires, `OperationCanceledException` propagates out of `ExecuteAsync` unchanged. The fallback does not run. Caller cancellation is a real "stop" signal, not a pipeline failure.

---

## 11. Idempotency keys (v0.8.0)

An idempotency key is a unique string that identifies **one logical operation**. It is propagated to every attempt of the call - retries, hedges, fallback runs. Downstream services that honour the key deduplicate on it, so a retried write reaches the destination as one logical operation.

This is the foundation of safe retries on non-idempotent writes.

### 11.1 The two modes

**Mode A - explicit.** You supply the key:

    await _resilience.ExecuteAsync<ChargeResult>(
        policyName: "payment-provider",
        operation: ChargeAsync,
        idempotencyKey: $"charge-{orderId}",
        ct: ct);

Every retry and hedged attempt of this call carries `charge-{orderId}`. The downstream provider sees one logical operation.

**Mode B - derived.** You pass `null`:

    await _resilience.ExecuteAsync<ChargeResult>(
        policyName: "payment-provider",
        operation: ChargeAsync,
        idempotencyKey: null,
        ct: ct);

The executor derives a stable key from the ambient correlation ID. Retries and hedged attempts of **this** call share the derived key. Two separate calls to `ExecuteAsync` get different derived keys.

### 11.2 How to design the key

The key must uniquely identify one logical operation and be stable across retries. Common patterns:

- `charge-{orderId}` - one charge per order
- `refund-{refundId}` - one refund per refund record
- `transfer-{txnId}` - one transfer per transaction
- `{service}-{operation}-{entityId}` - general shape

**Do not use** a fresh GUID generated inside the call site. Every retry would generate a new GUID, defeating the purpose. The key must be generated **before** `ExecuteAsync` and passed in.

**Do not reuse** a key across logically distinct operations. Two different charges must have two different keys.

### 11.3 Reading the propagated key

Inside the operation lambda, read the ambient key:

    using Portfolio.Resilience.Correlation;

    operation: async token =>
    {
        var key = IdempotencyContext.CurrentKey;
        // send the key to the downstream provider, e.g. as a header
        request.Headers.Add("Idempotency-Key", key);
        return await _http.SendAsync(request, token);
    }

### 11.4 The HTTP header

The `ResilientHttpMessageHandler` (Section 16) emits the `Idempotency-Key` HTTP header automatically on every attempt when an idempotency key is present in the ambient context.

If you use a custom `HttpClient` directly (not through the handler), set the header yourself in the operation lambda.

### 11.5 The IdempotencyContext API

    public static class IdempotencyContext
    {
        public static string? CurrentKey { get; }
        public static IDisposable Push(string key);
        public static string GenerateFromCorrelation();
    }

The executor manages the scope. You read `CurrentKey`. You do not need to call `Push` or `GenerateFromCorrelation` directly unless you are building custom integration.

### 11.6 Canonical payment example

    public Task<ChargeResult> ChargeAsync(Order order, CancellationToken ct) =>
        _resilience.ExecuteAsync<ChargeResult>(
            policyName: "payment-safe-policy",
            operation: async token =>
            {
                var key = IdempotencyContext.CurrentKey;

                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/charges")
                {
                    Content = JsonContent.Create(new { amount = order.Total, currency = order.Currency })
                };
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Add("Idempotency-Key", key);
                }

                using var response = await _http.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken: token)
                    ?? throw new InvalidOperationException("empty charge response");
            },
            idempotencyKey: $"charge-{order.Id}",
            ct: ct);

Every retry of `ChargeAsync` sends `Idempotency-Key: charge-{orderId}`. Stripe, Adyen, Braintree, and most modern payment providers deduplicate on this header.

### 11.7 Providers that honour idempotency keys

- Stripe - yes, via `Idempotency-Key` header
- Adyen - yes, via `Idempotency-Key` header
- Braintree - yes, via `Idempotency-Key` header or transaction-level dedup
- Square - yes
- PayPal - yes for v2 APIs
- Most modern payment APIs

**Check your provider's documentation.** The library cannot enforce downstream dedup - that is a provider capability.

### 11.8 Idempotency keys and the payment-safe preset

The two features are designed to be used together:

    var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();
    // ...
    idempotencyKey: $"charge-{orderId}"

The preset ensures the circuit sits outside retry and hedging, so once the provider is unhealthy no new attempts spawn. The idempotency key ensures that if retries or hedges did fire before the circuit opened, the provider sees one logical charge.

### 11.9 What idempotency keys do not do

- **Do not guarantee deduplication.** The downstream must honour the key.
- **Do not propagate across services automatically.** Only the HTTP handler emits the header. If your operation calls a non-HTTP downstream, propagate the key yourself.
- **Do not persist.** The library does not store keys; it passes them through.

---

## 12. PCI-safe event scrubbing (v0.8.0)

The library can mask sensitive payment data - PAN (primary account number), CVV / CVC, US Social Security Numbers - before any log sink sees an event. This is a first line of defense against accidentally logging cardholder data.

### 12.1 The three pattern families

**PAN** - 13 to 19 consecutive digits, optionally separated by spaces or dashes. Matches:

    4242424242424242
    4242 4242 4242 4242
    4242-4242-4242-4242

**CVV / CVC** - 3 or 4 digits adjacent to the tokens "cvv" or "cvc". Matches:

    cvv=123
    cvv: 1234
    cvc 123

**SSN** - US Social Security Number in the canonical `ddd-dd-dddd` format. Matches:

    123-45-6789

### 12.2 Enabling scrubbing

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("payment-provider", p =>
        {
            p.Retry.MaxAttempts              = 3;
            p.Logging.ScrubSensitiveData     = true;
        }));

### 12.3 The global-scope behavior (important)

**Once any policy sets `ScrubSensitiveData = true`, every event of every policy in the process is scrubbed.** The flag is a signal that the process needs scrubbing, not a per-policy toggle.

Setting `ScrubSensitiveData = false` on a second policy does not disable scrubbing. It only fails to enable it.

This behavior is documented in `docs/pci-scrubbing.md` and is by design. The rationale: if a process ever handles cardholder data, every event in that process is potentially sensitive. Granular per-policy control would be a false sense of safety.

### 12.4 Example - masking a PAN in an error message

    try
    {
        await _resilience.ExecuteAsync<ChargeResult>(
            policyName: "payment-provider",
            operation: ChargeAsync,
            ct: ct);
    }
    catch (ResilienceException ex)
    {
        // ex.Message and any error fields have already been scrubbed by the time
        // any log sink saw them
        _logger.LogError(ex, "charge failed");
    }

If the downstream provider returned an error message like `"declined: card=4242 4242 4242 4242 cvv=123"`, the event's `ErrorMessage` carries `"declined: card=[REDACTED] [REDACTED]"` (or a similar masked form). The raw value never reaches the sink.

### 12.5 The DefaultPciScrubber

    public sealed class DefaultPciScrubber : IEventScrubber
    {
        public const string Mask = "[REDACTED]";    // the replacement token
        public ResilienceEvent Scrub(ResilienceEvent evt);
    }

The scrubber returns a **new** `ResilienceEvent` with masked fields. It never mutates the input.

### 12.6 Custom scrubbers

Implement `IEventScrubber` to replace the default:

    public sealed class CustomScrubber : IEventScrubber
    {
        public ResilienceEvent Scrub(ResilienceEvent evt)
        {
            // your masking logic
            return evt with { ErrorMessage = Mask(evt.ErrorMessage ?? "") };
        }
    }

Then register:

    builder.Services.AddPortfolioResilience(r => r
        .AddScrubber(new CustomScrubber())
        .AddPolicy(...));

### 12.7 What the default scrubber catches - and what it misses

**Catches:**

- Standard PAN formats (13-19 digits, with common separators)
- CVV / CVC adjacent to `cvv` or `cvc` labels
- US SSNs in the canonical format

**Does not catch:**

- PANs in a format the regex does not recognize (very short, very long, unusual separators)
- Non-US identity numbers
- Provider-specific sensitive fields with different labels
- Sensitive data embedded in a metadata value that is not a string (e.g. inside a structured object)
- Any sensitive data that arrives after the event is emitted

**The scrubber is a first line of defense, not a compliance guarantee.** Use it as one layer of a broader PCI strategy:

1. Do not put cardholder data in log payloads in the first place
2. Use the scrubber as a safety net
3. Audit what your services log regularly

### 12.8 Performance

The default scrubber uses compiled regexes. It runs once per event, at the top of the composite sink, before any child sink sees the event. The cost is negligible unless you emit thousands of events per second with large metadata.

### 12.9 What scrubbing does not do

- **Does not scrub your application logs.** Only `ResilienceEvent` instances pass through the scrubber. Your own `_logger.LogInformation("card={Pan}", pan)` calls are unaffected.
- **Does not scrub HTTP bodies.** Only events.
- **Does not guarantee PCI DSS compliance.** Compliance requires a broader program.
---

## 13. Time budget (v0.8.0)

A time budget places a **total wall-clock ceiling** across the entire pipeline - every retry, every hedge, every attempt shares one deadline. When the budget runs out, layers stop spawning new work.

Per-attempt timeouts cap a single attempt. The budget caps the whole call.

### 13.1 The parameter

    int? timeBudgetMs = null

Passed to `ExecuteAsync`. `null` disables the budget (unchanged behavior). A positive integer activates it.

### 13.2 Canonical example

    await _resilience.ExecuteAsync<ChargeResult>(
        policyName: "payment-safe-policy",
        operation: ChargeAsync,
        idempotencyKey: $"charge-{orderId}",
        timeBudgetMs: 3000,               // total ceiling: 3 seconds
        ct: ct);

If the pipeline has not returned a result or thrown within 3 seconds, the budget is exhausted. The next attempt's delay cannot fit, the pipeline stops, and the last failure surfaces.

### 13.3 What the budget affects

The budget is read by:

- **Retry** - stops retrying when the next attempt's delay cannot fit in the remaining time
- **Timeout** - the effective per-attempt ceiling becomes `min(TimeoutMs, remaining budget)`
- **Hedging** - stops spawning hedges when remaining time cannot fit the next stagger delay

The budget does not:

- **Abort a running operation** - if the operation ignores its cancellation token, it runs to completion
- **Override caller cancellation** - if the caller's token fires, the pipeline rethrows immediately
- **Reduce `MaxAttempts`** - a policy with `MaxAttempts = 5` still allows up to 5 attempts if the budget permits

### 13.4 Worked example

Policy configuration:

    p.Retry.MaxAttempts   = 5;
    p.Retry.BaseDelayMs   = 100;
    p.Retry.MaxDelayMs    = 5000;
    p.Timeout.TimeoutMs   = 5000;

Call with `timeBudgetMs: 1500`:

- Attempt 1 fires immediately. Fails after ~50ms.
- Retry check: remaining ≈ 1450ms. Delay for retry 1 = 100ms. `1450 < 100 + 100`? No. Retry.
- Attempt 2 fires after 100ms. Fails after ~50ms. Total elapsed: ~200ms.
- Retry check: remaining ≈ 1300ms. Delay for retry 2 = 200ms. `1300 < 200 + 100`? No. Retry.
- Attempt 3 fires after 200ms. Total elapsed: ~450ms.
- Retry check: remaining ≈ 1050ms. Delay for retry 3 = 400ms. `1050 < 400 + 100`? No. Retry.
- Attempt 4 fires after 400ms. Total elapsed: ~900ms.
- Retry check: remaining ≈ 600ms. Delay for retry 4 = 800ms. `600 < 800 + 100`? **Yes**. Stop retrying.
- Pipeline throws the last failure. Total elapsed: ~900ms, 4 attempts.

The budget stopped the sequence at 4 attempts instead of the 5 the policy allowed. See scenario 09 in the sample.

### 13.5 The TimeBudgetContext API

    public static class TimeBudgetContext
    {
        public static double? RemainingMs { get; }
        public static bool IsExhausted { get; }
        public static double RemainingOrMax();
        public static IDisposable Push(int budgetMs);
    }

Custom layers read `RemainingMs` to check the budget. `RemainingMs` is `null` when no budget is active.

### 13.6 When to use a time budget

- Payment operations with strict SLAs (e.g. "must respond in 3 seconds or fail fast")
- Any call path where the caller has a total latency budget rather than a per-attempt budget
- Multi-service request flows where one slow dependency would blow the overall budget

### 13.7 When not to use a time budget

- Batch jobs with generous timing (per-attempt timeouts are enough)
- Calls where the operation is expected to take a long time (the budget would fire immediately)
- Any call where retries would legitimately need more time than the budget allows

### 13.8 Tuning guidance

| Operation | Suggested budget |
|-----------|------------------|
| User-facing API request | 2-5 seconds |
| Interactive UI with progress | 10-30 seconds |
| Background job | no budget, or generous |
| Payment authorization | 3-5 seconds |

**Rule of thumb:** the budget should be the maximum time the caller is willing to wait, not the time the operation needs.

### 13.9 Interaction with cancellation

If the caller's token fires, the pipeline rethrows immediately - the budget is irrelevant. If the caller's token is still live, the budget is the effective ceiling.

### 13.10 What the budget does not do

- **Does not extend timeouts.** A policy with `TimeoutMs = 5000` and `timeBudgetMs = 1000` runs each attempt with an effective ceiling of 1000ms.
- **Does not adjust the retry schedule.** The retry delays are the same. The budget only stops retries that cannot fit.
- **Does not persist.** The budget is per-call.

---

## 14. Payment-safe pipeline preset (v0.8.0)

The payment-safe preset returns a pipeline with the layer order that is safe for non-idempotent writes. This is the fintech chapter.

### 14.1 What "payment-safe" means

A payment operation - a charge, a refund, a payout, an order submission - is a **non-idempotent write**. If the same operation reaches the downstream provider twice, the customer can be charged twice.

The default pipeline order is:

    RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

Notice that **Circuit sits inside Retry and Hedging**. That means when Retry fires, it goes through Circuit again. When Hedging fires a parallel attempt, it goes through Circuit too. If the circuit is Closed, all attempts reach the operation.

Under a slow-but-eventually-successful provider, this can produce **two successful charges**: the primary attempt, and a hedge fired 50ms later, both eventually reaching the provider.

The payment-safe order is:

    RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation

The only change is the **position of Circuit**: it moves from inside Retry/Hedging to outside. Once the circuit opens, no new attempts spawn. The pipeline fails fast.

### 14.2 The preset

    var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();

Or with custom layers:

    var pipeline = ResiliencePipeline.WithPaymentSafeDefaults(
        rateLimiter: myRateLimiter,
        bulkhead: myBulkhead,
        circuit: myCircuit,
        hedging: myHedging,
        retry: myRetry,
        timeout: myTimeout);

Any null argument gets a fresh default instance.

### 14.3 Canonical payment write

    using Portfolio.Resilience.Policies;
    using Portfolio.Resilience.Correlation;

    var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();

    var definition = new PolicyDefinition { Name = "charge-pipeline" };
    definition.RateLimiter.Enabled       = true;
    definition.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;
    definition.RateLimiter.PermitLimit   = 100;
    definition.RateLimiter.WindowSeconds = 60;
    definition.Bulkhead.Enabled          = true;
    definition.Bulkhead.MaxConcurrency   = 20;
    definition.Bulkhead.MaxQueue         = 10;
    definition.Circuit.FailureThreshold  = 3;
    definition.Circuit.OpenDurationSeconds = 60;
    definition.Retry.MaxAttempts         = 3;
    definition.Retry.BaseDelayMs         = 100;
    definition.Hedging.Enabled           = false;   // do not hedge writes
    definition.Timeout.TimeoutMs         = 5000;

    var charge = await pipeline.ExecuteAsync<ChargeResult>(
        policyName: "charge-pipeline",
        operation: async token =>
        {
            var key = IdempotencyContext.CurrentKey;

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/charges")
            {
                Content = JsonContent.Create(new { amount = order.Total, currency = order.Currency })
            };
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.Add("Idempotency-Key", key);
            }

            using var response = await _http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken: token)
                ?? throw new InvalidOperationException("empty charge response");
        },
        definition: definition,
        ct: ct);

But note: the payment-safe preset does **not** propagate the idempotency key automatically when driving the pipeline directly. Only `IResilienceExecutor.ExecuteAsync` pushes the key onto the ambient context.

### 14.4 The recommended approach - executor with payment-safe preset

To get both the safe layer order AND automatic idempotency propagation:

**Step 1.** Register a policy with the payment-safe options:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("payment-safe-policy", p =>
        {
            p.Retry.MaxAttempts           = 3;
            p.Retry.BaseDelayMs           = 100;
            p.Circuit.FailureThreshold    = 3;
            p.Circuit.OpenDurationSeconds = 60;
            p.Circuit.OnlyCountTransient  = true;
            p.Timeout.TimeoutMs           = 5000;
            // Hedging disabled by default - safe for writes
            // Rate limiter and bulkhead optional
        }));

**Step 2.** Call it through the executor:

    public Task<ChargeResult> ChargeAsync(Order order, CancellationToken ct) =>
        _resilience.ExecuteAsync<ChargeResult>(
            policyName: "payment-safe-policy",
            operation: async token =>
            {
                var key = IdempotencyContext.CurrentKey;

                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/charges")
                {
                    Content = JsonContent.Create(new { amount = order.Total, currency = order.Currency })
                };
                if (!string.IsNullOrEmpty(key))
                {
                    request.Headers.Add("Idempotency-Key", key);
                }

                using var response = await _http.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<ChargeResult>(cancellationToken: token)
                    ?? throw new InvalidOperationException("empty charge response");
            },
            idempotencyKey: $"charge-{order.Id}",
            timeBudgetMs: 5000,
            ct: ct);

But **does the executor use the payment-safe layer order?** In `0.8.0`, the executor uses the `CompositePolicyBuilder` pipeline, which uses the default order. To use the payment-safe order, you need to construct a pipeline with `WithPaymentSafeDefaults()` and drive it directly, OR use the executor with a `CompositePolicyBuilder` configured to the payment-safe order.

**In `0.8.0` the recommended path is:** use `ResiliencePipeline.WithPaymentSafeDefaults()` and push the idempotency key yourself:

    using Portfolio.Resilience.Correlation;

    using var scope = IdempotencyContext.Push($"charge-{order.Id}");

    var charge = await _paymentPipeline.ExecuteAsync<ChargeResult>(
        policyName: "charge-pipeline",
        operation: ChargeAsync,
        definition: _chargeDefinition,
        ct: ct);

The explicit `Push` sets the ambient key, so the operation sees `IdempotencyContext.CurrentKey`. This is a small amount of ceremony for the full payment-safe guarantee.

### 14.5 When to use the payment-safe preset

- Charges (credit card, bank, wallet)
- Refunds
- Payouts
- Order submissions
- Event publishes to a non-idempotent bus
- Ledger writes
- Inventory reservations

### 14.6 When not to use it

- Read operations - the default order is fine and retries more aggressively
- Writes the downstream already deduplicates deterministically (rare; check the provider)
- Operations where a duplicate is harmless (metrics increments, idempotent state writes)

### 14.7 The formal pattern name

This is a **fail-fast, deduplication-first** write pattern. It is what payment gateways expect from well-behaved clients. Stripe, Adyen, Braintree, and other major payment providers explicitly document the pattern:

1. Use idempotency keys on every write
2. Back off aggressively on failures
3. **Stop retrying** once the provider signals unhealthy

`ResiliencePipeline.WithPaymentSafeDefaults()` + `IdempotencyContext.Push(key)` gives you all three. That is why the library ships the preset - composing it correctly by hand is easy to get subtly wrong.

### 14.8 What the preset does not do

- **Does not enable hedging safety.** Hedging is off by default. If you enable it, you must supply an idempotency key.
- **Does not guarantee provider-side deduplication.** That is a provider capability.
- **Does not persist circuit state across replicas.** Same as the default circuit.
- **Does not enforce single delivery.** It minimizes the chance of duplicates; it does not eliminate them. The idempotency key is the mechanism that makes retries safe. The preset just ensures retries do not multiply once a dependency is unhealthy.

### 14.9 Testing the preset in the sample

Scenario 10 in the sample (`10_CombinedScenario.cs`) demonstrates the preset. It:

1. Builds the pipeline with `WithPaymentSafeDefaults()`
2. Asserts 6 layers in the exact order `RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout`
3. Runs a charge-shaped operation that fails once and succeeds on retry
4. Verifies the operation ran exactly twice

Run it:

    dotnet run --project dotnet\samples\Samples.App\Samples.App.csproj

Look for `[10/10] COMBINED` in the output.
---

## 15. Pipeline composition

The pipeline is an ordered list of layers. Every layer implements `IResiliencePolicy`. You can build a custom pipeline from any subset of layers in any order.

### 15.1 The IResiliencePolicy contract

    public interface IResiliencePolicy
    {
        Task<T> ExecuteAsync<T>(
            string policyName,
            Func<CancellationToken, Task<T>> operation,
            PolicyDefinition definition,
            CancellationToken ct);
    }

Every builder in the library implements this. So can your custom layers.

### 15.2 Two composition styles

**Style A - direct.** One line for a static shape:

    using Portfolio.Resilience.Policies;

    var pipeline = ResiliencePipeline.Wrap(
        rateLimiterBuilder,
        bulkheadBuilder,
        retryBuilder,
        circuitBuilder,
        timeoutBuilder);

**Style B - fluent.** For configuration-driven shapes with conditional layers:

    var pipeline = new ResiliencePipelineBuilder()
        .WithName("external-api-pipeline")
        .Add(rateLimiterBuilder)
        .AddIf(isProduction, retryBuilder)
        .Add(circuitBuilder)
        .Add(timeoutBuilder)
        .Build();

Both produce an immutable `ResiliencePipeline`.

### 15.3 Layer order significance

Layers execute outermost-first. The first layer in the list is the outermost.

`Wrap(rateLimiter, bulkhead, retry)` executes:

    rateLimiter -> bulkhead -> retry -> operation

If the rate limiter rejects the call, the bulkhead and retry are never invoked. If the bulkhead rejects, retry is never invoked.

**Order matters.** Choose the order based on what protects what:

- Rate limiter outermost - protects downstream services from this caller's burst
- Bulkhead next - protects this caller's thread pool from a slow dependency
- Circuit before retry - fails fast once the dependency is unhealthy (payment-safe)
- Retry after circuit - only retry when the dependency is considered healthy
- Timeout innermost - bounds every attempt

### 15.4 The default pipeline order

    RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

Built by `CompositePolicyBuilder` internally. This is what runs when you use `IResilienceExecutor.ExecuteAsync`.

### 15.5 The payment-safe pipeline order

    RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation

Built by `ResiliencePipeline.WithPaymentSafeDefaults()`. See Section 14.

### 15.6 Invoking a custom pipeline

    using Portfolio.Resilience.Configuration;

    var definition = new PolicyDefinition { Name = "my-pipeline" };
    definition.Retry.MaxAttempts = 3;
    // configure other layers

    var result = await pipeline.ExecuteAsync<int>(
        policyName: "my-pipeline",
        operation: DoWorkAsync,
        definition: definition,
        ct: ct);

The `definition` argument tells each layer what options to read. Every layer reads its own options from the definition.

### 15.7 Custom layers

Implement `IResiliencePolicy` for custom behavior:

    public sealed class MetricsDecorator : IResiliencePolicy
    {
        private readonly IMetricSink _sink;

        public MetricsDecorator(IMetricSink sink) => _sink = sink;

        public async Task<T> ExecuteAsync<T>(
            string policyName,
            Func<CancellationToken, Task<T>> operation,
            PolicyDefinition definition,
            CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            finally
            {
                sw.Stop();
                _sink.RecordCall(policyName, sw.Elapsed, success: true, attempts: 1);
            }
        }
    }

Add to a pipeline with `.Add(customLayer)` or `Wrap(customLayer, ...)`.

### 15.8 The Layers property

`ResiliencePipeline.Layers` exposes the ordered layers as `IReadOnlyList<IResiliencePolicy>`. Useful for assertions in tests and for health endpoints that want to report which layers are active.

### 15.9 What composition does not do

- **Does not validate layer order.** If you compose `Wrap(retry, circuit)`, retry runs first and can fire multiple attempts before the circuit even sees the call. That is allowed; it is just usually wrong for writes.
- **Does not share state between pipelines.** Two `ResiliencePipeline` instances have independent layer state. The circuit for a policy in one pipeline is not the circuit in another.

---

## 16. HTTP integration

The library provides a `DelegatingHandler` that routes every `HttpClient` request through a policy. Two flavors: named policy or safe default.

### 16.1 Named policy handler

Register a policy, then attach it to a named `HttpClient`:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts = 3;
            p.Retry.BaseDelayMs = 100;
            p.Circuit.FailureThreshold = 5;
            p.Timeout.TimeoutMs = 5000;
        }));

    builder.Services
        .AddHttpClient("auth-service", c =>
            c.BaseAddress = new Uri("https://auth.internal"))
        .AddResilientHandler("auth-service");

Every request sent through that client runs through the policy.

### 16.2 Standard handler - one-liner safe defaults

If you do not want to define a policy by hand:

    builder.Services
        .AddHttpClient("auth-service", c =>
            c.BaseAddress = new Uri("https://auth.internal"))
        .AddStandardResilienceHandler();

That registers a policy named `"standard"` and routes every request through it. Override inline:

    .AddStandardResilienceHandler(p =>
    {
        p.Retry.MaxAttempts = 5;
        p.Timeout.TimeoutMs = 10_000;
    });

The standard policy enables retry, circuit, and timeout. Rate limiter, bulkhead, and hedging are off by default - they multiply load or duplicate requests, and must be opted into explicitly.

If you register your own policy named `"standard"`, yours wins.

### 16.3 What the handler emits

The handler:

- Reads the ambient correlation ID and emits `X-Correlation-Id` header
- Reads the ambient idempotency key and emits `Idempotency-Key` header
- Records every request in the metric sink
- Emits events at each pipeline decision

### 16.4 Reading correlation IDs from inbound requests

For a server-side app, read the incoming header and push it onto the ambient context:

    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? CorrelationContext.NewId();

        using var scope = CorrelationContext.Push(correlationId);
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        await next();
    });

Every event, exception, and metric sample emitted during the request carries that correlation ID.

### 16.5 Using the handler without DI

For a console app or non-DI environment, construct the handler manually:

    var handler = new ResilientHttpMessageHandler(
        policyName: "auth-service",
        executor: resilienceExecutor);

    var client = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://auth.internal")
    };

The exact constructor signature depends on your version. In the sample, we use the DI path.

### 16.6 What the handler does not do

- **Does not add retry at the socket level.** Only at the HTTP request level.
- **Does not buffer request bodies.** If you need to retry a `POST` with a streamed body, buffer it first.
- **Does not automatically propagate idempotency keys to non-HTTP transports.**

---

## 17. Observability

Every pipeline decision emits a `ResilienceEvent` and records a metric sample. Both side channels run parallel to execution - no configuration is required to get events or metrics; they work out of the box with sensible defaults.

### 17.1 Events

The `ResilienceEvent` record carries:

    public sealed record ResilienceEvent
    {
        public ResilienceEventType EventType { get; init; }
        public string PolicyName { get; init; }
        public string? CorrelationId { get; init; }
        public DateTime TimestampUtc { get; init; }
        public int? Attempt { get; init; }
        public double? DurationMs { get; init; }
        public ResilienceErrorCategory? ErrorCategory { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ErrorType { get; init; }
        public IReadOnlyDictionary<string, object?> Metadata { get; init; }
    }

Every property is `init`-only. The record is immutable.

### 17.2 The event types

    CallStarted = 0
    RetryAttempted = 1
    CallSucceeded = 2
    CallFailed = 3
    CircuitOpened = 4
    CircuitClosed = 5
    CircuitHalfOpened = 6
    FallbackUsed = 7
    TimeoutBreached = 8
    RateLimited = 9
    BulkheadRejected = 10
    HedgeWon = 11
    HedgeLost = 12
    HedgeCancelled = 13

**Note:** In `0.8.0`, not every event type reaches registered `ILogSink` implementations. `CallStarted`, `CallSucceeded`, and `CallFailed` are reliably delivered. Others are emitted by the internal emitter but not forwarded to sinks. See the sample README's findings for details.

### 17.3 Sinks

    public interface ILogSink
    {
        void Emit(ResilienceEvent evt);
    }

One method. Synchronous. Built-in sinks:

- `ConsoleLogSink` - JSON lines to stdout
- `FileLogSink` - daily rotating JSON Lines to a folder
- `NullLogSink` - discards everything (the default)
- `CompositeLogSink` - forwards to multiple child sinks

Register one or more:

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddLogSink(new FileLogSink("/var/log/resilience"))
        .AddPolicy(...));

Multiple `AddLogSink` calls automatically compose into a `CompositeLogSink`. A broken sink does not break the others - exceptions are caught per-sink.

### 17.4 Custom sinks

Implement `ILogSink`:

    public sealed class DatadogSink : ILogSink
    {
        private readonly HttpClient _http;

        public void Emit(ResilienceEvent evt)
        {
            // fire-and-forget; do not block the pipeline
            _ = _http.PostAsJsonAsync("/logs", evt);
        }
    }

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new DatadogSink())
        .AddPolicy(...));

A custom sink is ~30 lines. The library does not ship per-vendor sinks - the OTel package is the universal adapter (Section 21).

### 17.5 Metrics

    public interface IMetricSink
    {
        void RecordCall(string policyName, TimeSpan duration, bool success, int attempts);
    }

The `InMemoryMetricSink` is always present. It keeps a rolling window per policy and computes:

- p50, p95, p99 latencies
- Error rate
- Total calls
- In-flight count

    var tracker = provider.GetRequiredService<ILatencyTracker>();

    var snapshots = tracker.Snapshot();
    foreach (var s in snapshots)
    {
        Console.WriteLine($"{s.PolicyName}: p50={s.P50Ms}ms p95={s.P95Ms}ms p99={s.P99Ms}ms errors={s.ErrorRate:P1} inFlight={s.InFlight}");
    }

### 17.6 The LatencySnapshot record

    public sealed record LatencySnapshot(
        string PolicyName,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double ErrorRate,
        long TotalCalls,
        int InFlight);

Fields shown in the sample output; exact property names may include additional fields. Read the snapshot directly from `ILatencyTracker.Snapshot()`.

### 17.7 Correlation

Every event, exception, and metric sample carries a correlation ID from `CorrelationContext.CurrentId`. The `X-Correlation-Id` HTTP header propagates the ID across services.

    using Portfolio.Resilience.Correlation;

    var id = CorrelationContext.NewId();           // generate one
    using var scope = CorrelationContext.Push(id); // set for this async flow

The `ResilientHttpMessageHandler` emits the header on every request automatically.

### 17.8 Health endpoint pattern

    using Portfolio.Resilience.Abstractions;

    app.MapGet("/health/resilience", (
        ICircuitBreakerMonitor monitor,
        ILatencyTracker latency) =>
    {
        var circuits = monitor.Snapshot()
            .Select(c => new { c.PolicyName, State = c.State.ToString(), c.ConsecutiveFailures });
        var policies = latency.Snapshot()
            .Select(p => new { p.PolicyName, p.P50Ms, p.P95Ms, p.P99Ms, p.ErrorRate, p.InFlight });

        return Results.Ok(new { circuits, policies });
    });

### 17.9 What observability does not do

- **Does not ship events to a remote backend by default.** You must register a sink.
- **Does not guarantee delivery.** A broken sink is swallowed (by design - never crash the caller).
- **Does not compute percentiles across process restarts.** In-memory per process.
---

## 18. Error classification

Every failure that passes through the pipeline is classified into one of six categories. The category drives retry and circuit decisions, and gives you a language-neutral way to write alerting rules.

### 18.1 The six categories

    Transient    - network blips, connection resets, HTTP 5xx, HTTP 429
    Permanent    - HTTP 4xx (except 429), ArgumentException, validation failures
    CircuitOpen  - the circuit was Open when the call arrived
    Timeout      - the per-attempt ceiling or the time budget fired
    FallbackUsed - the pipeline failed and the fallback produced a value
    Unknown      - the classifier could not categorize the exception

### 18.2 Default classification rules

The `ErrorClassifier` ships with defaults:

| Exception type or signal | Category |
|--------------------------|----------|
| `TimeoutException` | `Timeout` |
| `HttpRequestException` with 5xx status | `Transient` |
| `HttpRequestException` with 429 status | `Transient` |
| `HttpRequestException` with 4xx status (except 429) | `Permanent` |
| `OperationCanceledException` when the caller's token fired | (rethrows unchanged, not classified) |
| `OperationCanceledException` when our timer fired | `Timeout` |
| `ArgumentException`, `ArgumentNullException` | `Permanent` |
| `InvalidOperationException` | `Permanent` |
| SQLSTATE 08xxx (connection error), 40xxx (deadlock) | `Transient` |
| SQLSTATE 23xxx (integrity violation) | `Permanent` |
| Anything else | `Unknown` |

The exact rules are documented in `docs/error-classification.md`. The XML for `ErrorClassificationOptions` exposes the configurable pieces.

### 18.3 Custom classification

Configure on the `ResilienceBuilder`:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("custom-service", p => { ... })
        // global error classification rules apply to every policy
        .ConfigureErrorClassification(e =>
        {
            // mark a custom exception as transient
            e.TransientExceptionTypes.Add(typeof(MyRetryableException));

            // mark an HTTP status as transient
            e.TransientHttpStatusCodes.Add(425);   // Too Early

            // mark a SQLSTATE prefix as transient
            e.TransientSqlStatePrefixes.Add("08");
        }));

The exact property names on `ErrorClassificationOptions` are documented in the shipped XML. Callers who need a rule the defaults do not cover can add one here.

### 18.4 Why classification matters

Retry and circuit both gate on category:

- **Retry** fires only for `Transient` and `Timeout` (unless `RetryOnPermanent = true`)
- **Circuit** counts only `Transient` failures (unless `OnlyCountTransient = false`)

So the classification determines:

- Whether the operation is retried
- Whether the circuit opens after a failure streak
- Whether the caller sees a specific error category or a generic one

**A common bug:** your operation throws a custom exception, the classifier labels it `Unknown`, and retry never fires even though the failure is genuinely transient. Fix: register the exception type as transient.

### 18.5 The ResilienceException

When the pipeline fails without a fallback, it throws `ResilienceException`. Key members:

    public sealed class ResilienceException : Exception
    {
        public string PolicyName { get; }
        public ResilienceErrorCategory Category { get; }
        public int AttemptsMade { get; }
        public TimeSpan TotalDuration { get; }
        public string? CorrelationId { get; }
        public IReadOnlyDictionary<string, object?> Metadata { get; }
        public Exception? InnerException { get; }
    }

`AttemptsMade` is `0` for `CircuitOpen` rejections - the operation was never invoked.

### 18.6 Catching by category

    try
    {
        await _resilience.ExecuteAsync<int>(
            policyName: "some-service",
            operation: DoWorkAsync,
            ct: ct);
    }
    catch (ResilienceException ex)
    {
        switch (ex.Category)
        {
            case ResilienceErrorCategory.Transient:
                return Results.Json(new { error = "temporary issue" }, statusCode: 503);
            case ResilienceErrorCategory.Permanent:
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            case ResilienceErrorCategory.CircuitOpen:
                return Results.Json(new { error = "service unavailable" }, statusCode: 503);
            case ResilienceErrorCategory.Timeout:
                return Results.Json(new { error = "request timed out" }, statusCode: 504);
            default:
                return Results.Json(new { error = "unknown" }, statusCode: 500);
        }
    }

### 18.7 What classification does not do

- **Does not affect non-pipeline exceptions.** Your code throws whatever it throws. Only the pipeline wraps failures.
- **Does not replace your own error handling.** You still catch and map exceptions at the boundary.
- **Does not fix a bad exception type.** If your downstream returns 200 with `{ "error": "..." }` in the body, the classifier sees a success and will not retry.

---

## 19. Dependency injection and configuration

### 19.1 The DI registration

    public static IServiceCollection AddPortfolioResilience(
        this IServiceCollection services,
        Action<ResilienceBuilder> configure);

Returns `IServiceCollection` for chaining. Registers:

- `IResilienceExecutor` (singleton)
- `ICircuitBreakerMonitor` (singleton)
- `ILatencyTracker` (singleton)
- `IResiliencePolicyRegistry` (singleton)

All singletons. Safe to inject anywhere.

### 19.2 The ResilienceBuilder

The `Action<ResilienceBuilder>` callback receives a builder with these methods:

    public sealed class ResilienceBuilder
    {
        public ResilienceOptions Options { get; }
        public ErrorClassificationOptions ErrorClassification { get; }
        public IReadOnlyList<ILogSink> LogSinks { get; }
        public IReadOnlyList<IMetricSink> MetricSinks { get; }

        public ResilienceBuilder AddLogSink(ILogSink sink);
        public ResilienceBuilder AddMetricSink(IMetricSink sink);
        public ResilienceBuilder AddPolicy(string name, Action<PolicyDefinition> configure);
        public ResilienceBuilder UseDefaultPolicy(Action<PolicyDefinition> configure);
    }

Every method returns `this`. Chain freely.

### 19.3 The PolicyDefinition

Every `AddPolicy` callback receives a `PolicyDefinition` with these properties:

    public sealed class PolicyDefinition
    {
        public string Name { get; set; }
        public RetryOptions Retry { get; set; }
        public CircuitOptions Circuit { get; set; }
        public TimeoutOptions Timeout { get; set; }
        public FallbackOptions Fallback { get; set; }
        public LoggingOptions Logging { get; set; }
        public RateLimiterOptions RateLimiter { get; set; }
        public BulkheadOptions Bulkhead { get; set; }
        public HedgingOptions Hedging { get; set; }
    }

Every sub-option is settable inside the callback. Sections 4 through 14 cover every field.

### 19.4 Binding from configuration

    builder.Services.AddPortfolioResilience(r => r
        .LoadFromConfiguration(builder.Configuration, "Resilience"));

Loads from `appsettings.json` under the given section (default: `Resilience`). Policies registered in code before `LoadFromConfiguration` are preserved; entries from configuration override on collision.

Environment variables prefixed with `RESILIENCE_` are also supported via the standard .NET configuration system.

### 19.5 The default policy

    builder.Services.AddPortfolioResilience(r => r
        .UseDefaultPolicy(p =>
        {
            p.Retry.MaxAttempts = 3;
            p.Timeout.TimeoutMs = 5000;
        }));

The default policy is used when a call references a policy name that is not registered. Without a default, unknown policy names throw on first call.

**Use the default policy sparingly.** It makes typo-driven policy misses silent. If you register `"payment-policy"` but call `"payment-prodicy"`, the default policy runs and you may not notice.

---

## 20. Middleware and error envelopes

When you expose an HTTP API, every unhandled exception becomes a 500 with a generic body. That leaks nothing useful to the caller. `ResilienceExceptionMiddlewareBase` helps you render errors in your own service's response shape.

### 20.1 The pattern

    using Portfolio.Resilience.Middleware;
    using Portfolio.Resilience.Errors;

    public sealed class ResilienceExceptionMiddleware : ResilienceExceptionMiddlewareBase
    {
        protected override async Task HandleAsync(HttpContext context, ResilienceException ex)
        {
            context.Response.StatusCode = ex.Category switch
            {
                ResilienceErrorCategory.Transient     => StatusCodes.Status503ServiceUnavailable,
                ResilienceErrorCategory.CircuitOpen   => StatusCodes.Status503ServiceUnavailable,
                ResilienceErrorCategory.Timeout       => StatusCodes.Status504GatewayTimeout,
                ResilienceErrorCategory.Permanent     => StatusCodes.Status400BadRequest,
                _                                     => StatusCodes.Status500InternalServerError
            };

            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                error = ex.Category.ToString(),
                policyName = ex.PolicyName,
                correlationId = ex.CorrelationId,
                attempts = ex.AttemptsMade
            });
        }
    }

Then register in `Program.cs`:

    app.UseMiddleware<ResilienceExceptionMiddleware>();

### 20.2 Why a middleware

The library shares the mechanism (catching `ResilienceException`); you own the envelope (the JSON shape your clients expect). This is the "share the engine, own the interface" principle from the design docs.

### 20.3 What the base class does

`ResilienceExceptionMiddlewareBase`:

- Catches `ResilienceException` thrown by downstream middleware or endpoints
- Calls your `HandleAsync` for each one
- Re-throws non-`ResilienceException` exceptions unchanged

You implement only the response shape.

### 20.4 When to use it

- Any HTTP API with an error contract you control
- Any service where clients depend on specific status codes or error body shapes
- Any service where you want consistent `X-Correlation-Id` propagation in error responses

### 20.5 When not to use it

- Minimal APIs with endpoint-specific error handling (each endpoint decides its own status)
- gRPC services (different error model)
- Background workers with no HTTP interface
---

## 21. OpenTelemetry export (optional package)

The core library is zero-dependency. If you already use OpenTelemetry for observability, install the companion package and every `ResilienceEvent` becomes an OTel log record, and every call becomes an OTel histogram and counter.

### 21.1 Install and register

    dotnet add package Portfolio.Resilience.OpenTelemetry

    using Portfolio.Resilience.Extensions;

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("auth-service", p => { ... }));

    builder.Services.AddPortfolioResilienceOpenTelemetry();

The registration adds an OTel log sink and an OTel metric sink to the composite. Events flow to both the OTel pipeline and any sinks you registered separately.

### 21.2 What gets exported

**Logs.** Every `ResilienceEvent` becomes an OTel log record with structured attributes prefixed `resilience.*`:

    resilience.event_type        = retry_attempted
    resilience.policy_name       = auth-service
    resilience.correlation_id    = abc-123
    resilience.attempt           = 2
    resilience.duration_ms       = 123.4

**Metrics.** Every call produces:

    resilience.call.duration_ms          histogram, tagged policy_name, success
    resilience.call.succeeded_total      counter, tagged policy_name, attempts
    resilience.call.failed_total         counter, tagged policy_name, attempts

Point them at your existing OTel exporter (OTLP, Prometheus, Datadog, etc.).

### 21.3 Example - export to console (dev)

    builder.Services
        .AddOpenTelemetry()
        .WithLogging(b => b.AddConsoleExporter())
        .WithMetrics(b => b.AddConsoleExporter());

    builder.Services.AddPortfolioResilienceOpenTelemetry();

### 21.4 Example - export to OTLP

    builder.Services
        .AddOpenTelemetry()
        .WithLogging(b => b.AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317")))
        .WithMetrics(b => b.AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317")));

    builder.Services.AddPortfolioResilienceOpenTelemetry();

### 21.5 Custom sinks vs. OTel export

You can use both. The core library never changes; the OTel package is one sink among many. If you already log to console, file, or a vendor, keep those and add OTel.

### 21.6 What the OTel package does not do

- **Does not configure an exporter.** It emits into the OpenTelemetry SDK pipeline; you configure where that pipeline sends data.
- **Does not replace your existing logs.** It adds a sink. Your own `ILogger` usage is unaffected.
- **Does not require OpenTelemetry.** The core library works without it.

---

## 22. Roslyn analyzers (optional package)

Two compile-time warnings catch common resilience mistakes before they ship.

### 22.1 Install

    dotnet add package Portfolio.Resilience.Analyzers

The package ships analyzers. They run in your IDE and in `dotnet build`. They never affect runtime behavior.

### 22.2 PR0001 - HttpClient bypass

    // PR0001 warning
    var response = await _httpClient.GetAsync("/users/me");

An `HttpClient` obtained from `IHttpClientFactory` is called directly, without going through the resilience pipeline. Add `.AddResilientHandler(policyName)` or `.AddStandardResilienceHandler()` to the client registration to silence the warning.

The analyzer knows when you are using `IHttpClientFactory` and flags direct calls. If your `HttpClient` was not created via the factory, the rule does not apply.

### 22.3 PR0002 - policy misconfiguration

    p.RateLimiter.Enabled       = true;
    p.RateLimiter.PermitLimit   = 0;      // PR0002: PermitLimit must be > 0 when enabled

A policy enables a feature (rate limiter, bulkhead, hedging) but sets a companion value to `0` or negative. The warning tells you which value is suspicious.

### 22.4 Suppressing a warning

    #pragma warning disable PR0001
    var response = await _httpClient.GetAsync("/users/me");
    #pragma warning restore PR0001

Or via `.editorconfig`:

    [*.cs]
    dotnet_diagnostic.PR0001.severity = none

**Suppress with a reason.** Most warnings are correct and suppressing them hides real problems.

### 22.5 What the analyzers do not do

- **Do not catch every mistake.** They catch the two most common ones.
- **Do not affect runtime.** Suppress them freely if the warning is a false positive in your context.
- **Do not replace tests.** They are a first line of defense, not a compliance mechanism.

---

## 23. Comparison with Polly and Microsoft.Extensions.Http.Resilience

This section places the library honestly beside the two most common alternatives. Polly is the .NET standard for resilience and the base for Microsoft.Extensions.Resilience. Neither of those libraries is being replaced by Portfolio.Resilience; the three have different design centers.

### 23.1 The comparison table

| Feature | Portfolio.Resilience | Polly | MS.Extensions.Http.Resilience |
|---------|----------------------|-------|------------------------------|
| Retry + backoff + jitter | Yes | Yes | Yes |
| Circuit breaker | Yes | Yes | Yes |
| Timeout (per-attempt) | Yes | Yes | Yes |
| Fallback | Yes | Yes | Yes |
| Rate limiter (4 strategies) | Yes | Yes | Yes |
| Bulkhead | Yes | Yes | Yes |
| Hedging | Yes | Yes | Yes |
| Idempotency key propagation | Built in | Manual | Manual |
| PCI-safe event scrubbing | Built in | Not built in | Not built in |
| Timeout budget propagation | Built in | Per-attempt only | Per-attempt only |
| Payment-safe pipeline preset | Built in | Manual composition | Manual composition |
| Policy composition (Wrap) | Yes | Yes | Yes |
| OpenTelemetry integration | Yes (optional package) | Yes | Yes |
| Compile-time analyzers | Yes (optional package) | No | Yes |
| AddStandardResilienceHandler() | Yes | No | Yes |
| Chaos engineering | Deferred to v1.x | Yes (Simmy) | No |
| Ambient correlation IDs | Built in | Manual | Partial |
| Cross-language SPEC | Yes | No | No |
| Latency percentiles without OTel | Built in | OTel only | OTel only |
| Zero core dependencies | Yes | Yes | Depends on Polly |

### 23.2 What "Manual" vs "Not built in" means

The table uses three states:

- **Yes / Built in** - the feature exists as a first-class API. One call and it works.
- **Manual** - the feature exists as a pattern the caller must write. Polly users frequently implement idempotency keys themselves, but Polly does not provide the plumbing.
- **Not built in** - the feature does not exist in the library, and implementing it requires code the library does not help with.

This distinction matters. Polly can be used for idempotency - but you write the propagation yourself, and you must remember to write it in every call site. Portfolio.Resilience propagates the key through the pipeline automatically.

### 23.3 The four v0.8.0 differentiators, explained

**1. Idempotency key propagation.**
Portfolio.Resilience carries an idempotency key through every retry and hedged attempt of the same logical call. Polly leaves this to the caller: you must remember to construct the key and attach it to every attempt. In Portfolio.Resilience, the key is one parameter to `ExecuteAsync`, and the executor propagates it.

**2. PCI-safe event scrubbing.**
Portfolio.Resilience ships a scrubber that masks PAN, CVV, and SSN patterns before any log sink sees an event. Polly has no equivalent. If you use Polly and log events, you must write your own scrubber or ensure sensitive data never enters log payloads.

**3. Timeout budget propagation.**
Portfolio.Resilience accepts a `timeBudgetMs` parameter that bounds the entire pipeline - retries, hedges, timeouts. Polly composes per-attempt timeouts without a total ceiling; you must build the total ceiling yourself if you need one.

**4. Payment-safe pipeline preset.**
Portfolio.Resilience ships a named factory (`WithPaymentSafeDefaults()`) that returns a pipeline with the layer order safe for non-idempotent writes. Polly requires careful manual composition.

### 23.4 Where Polly still leads

Two areas where Polly is objectively stronger:

**Chaos engineering.** Polly's Simmy injects faults for resilience testing. Portfolio.Resilience has deferred this to v1.x. If you need chaos injection today, Polly is the only option among the three.

**Ecosystem maturity.** Polly has 200M+ downloads, .NET Foundation membership, and a broad plugin marketplace. Portfolio.Resilience is new, has a single maintainer, and has a smaller surface.

Neither of those is a knock on Portfolio.Resilience. It is a newer library with a narrower focus.

### 23.5 When to choose Polly or MS.Extensions.Resilience

- You want the industry standard with the widest ecosystem.
- You need chaos engineering today.
- You already have a substantial investment in Polly configurations.
- You want the broadest community support and the most documentation.

### 23.6 When to choose Portfolio.Resilience

- You need idempotency propagation, PCI scrubbing, or total time budgets as first-class features
- You have multiple services in different languages and want a consistent event schema (see SPEC.md)
- You want a small, dependency-free core library with a straightforward mental model
- You want compile-time analyzers that catch resilience mistakes before they ship
- You want correlation IDs, structured events, and latency percentiles without configuring OpenTelemetry

### 23.7 Can you use both?

Yes. The two libraries operate at different layers. Some teams use Polly for the fine-grained HTTP client pipeline and Portfolio.Resilience for the higher-level operation pipeline (database calls, Redis, cross-service business operations). Neither library knows or cares about the other.

If you use both, be explicit about which pipeline each call site uses. Two resilience layers stacked on one call can multiply retries, open two circuits, and produce confusing event streams.

### 23.8 Honest framing

Portfolio.Resilience is not a Polly replacement. It is a focused alternative for teams that need the four v0.8.0 features and prefer a small, spec-driven library over a mature but broad one. If your team already uses Polly and it works, you do not need to switch. If you need idempotency propagation as a first-class feature, or PCI-safe scrubbing, or total time budgets, or a cross-language event contract, this library is a fit.

The right question is not "which is better" but "which fits this problem". Both libraries answer that question honestly.
---

## 24. Troubleshooting and common mistakes

Every trap below was hit while building the sample in this repo. Each one cost real time. Read them before you debug something that looks broken.

### 24.1 "My circuit never opens"

**Cause:** `Circuit.OnlyCountTransient` defaults to `true`. Your operation throws a `Permanent`-classified exception (like `InvalidOperationException`), which does not count toward `FailureThreshold`.

**Fix:** Set `p.Circuit.OnlyCountTransient = false;` if the circuit should open on any failure, or throw a transient-classified exception (`TimeoutException`, `HttpRequestException`).

### 24.2 "My retry never fires"

**Cause:** The exception is classified `Permanent` (a `4xx` response, `ArgumentException`, `InvalidOperationException`).

**Fix:** Confirm the exception is classified `Transient`. If it is not, either change the exception or register the type as transient via `ConfigureErrorClassification`.

### 24.3 "My operation was invoked with the wrong number of attempts"

**Cause:** Two possibilities:

1. `MaxAttempts` is being read as **retries** rather than **total attempts**. `MaxAttempts = 3` means 3 total invocations, not 1 + 3.
2. The circuit opened after the first N failures and rejected the rest.

**Fix:** Read `CallSucceeded.Attempt` (or `CallFailed.AttemptsMade`) to see how many attempts actually ran. If it is less than `MaxAttempts`, the circuit is likely the cause.

### 24.4 "My time budget rejects before the first attempt"

**Cause:** The circuit is open. The budget works correctly - a failing operation counted by the circuit opens it, and the next call is rejected by the circuit, not the budget.

**Fix:** Neutralize the circuit for budget tests by setting `p.Circuit.FailureThreshold = 100;` or using a fresh policy name per sub-test.

### 24.5 "The ConcurrencyLimit rate limiter never rejects my sequential calls"

**Cause:** `ConcurrencyLimit` caps **simultaneous** calls, not cumulative. A sequential burst of 5 calls that all complete before the next starts never has more than 1 in flight.

**Fix:** If you want a cumulative limit, use `SlidingWindow` or `FixedWindow`. Use `ConcurrencyLimit` only when you actually need to cap overlap.

### 24.6 "RetryAttempted / CircuitOpened events do not reach my sink"

**Cause:** In `Portfolio.Resilience 0.8.0`, some event types are emitted by the internal emitter but not forwarded to registered `ILogSink` implementations. `CallStarted`, `CallSucceeded`, and `CallFailed` reach sinks reliably. Others may not.

**Fix:** Use `ICircuitBreakerMonitor` for circuit state, `CallSucceeded.Attempt` / `CallFailed.AttemptsMade` for retry evidence, and the `IMetricSink` for latency and error rate. Do not depend on `RetryAttempted` or circuit events reaching a custom sink in `0.8.0`. This is a documented finding.

### 24.7 "ScrubSensitiveData = false on one policy did not disable scrubbing"

**Cause:** Scrubbing is global. Once any policy sets `ScrubSensitiveData = true`, every event in the process is scrubbed. Setting it to `false` on another policy does not disable it.

**Fix:** This is documented behavior. See `docs/pci-scrubbing.md`. There is no per-policy opt-out in `0.8.0`.

### 24.8 "LoggingOptions toggles do not suppress emission"

**Cause:** The toggles define an intended contract whose wiring is a known limitation carried from `v0.6.0`. In `0.8.0`, every configured event fires regardless of its toggle.

**Fix:** Do not rely on the toggles for suppression in `0.8.0`. Filter events in your sink implementation if you need to.

### 24.9 "The time budget test is flaky"

**Cause:** The budget's interaction with the circuit and with retry timing is deterministic in the library, but the sample's assertion may be too strict about exact attempt counts. Scheduling variance in test runners can change the count.

**Fix:** Assert on the **shape** of the behavior, not on exact counts:
- `boundedAttempts < unboundedAttempts` (budget reduced attempts)
- `boundedElapsed < unboundedElapsed` (budget reduced time)
- `boundedAttempts >= 1` (budget allowed at least one attempt)

Not on exact values like `unboundedAttempts == 5`.

### 24.10 "My hedged write charged twice"

**Cause:** Hedging fires parallel attempts of the same operation. On a write, two attempts can reach the provider.

**Fix:** Either disable hedging on write paths (`p.Hedging.Enabled = false;`) or use an idempotency key so the provider deduplicates. See Section 9 and Section 14.

### 24.11 "The pipeline hangs after my operation times out"

**Cause:** The operation is ignoring the `CancellationToken`. The pipeline returns control to the caller but the operation keeps running in the background.

**Fix:** Always pass the token through to the underlying work. See Section 3.5.

### 24.12 "Two policies with different names share a circuit"

**Cause:** This should not happen - the circuit is per-policy. If you are seeing shared state, confirm the policy names are actually different strings. A typo like `"payment"` vs `"payment "` (trailing space) creates two policies with almost identical names.

**Fix:** Register policy names once as constants and reference the constants everywhere.

### 24.13 "My unit tests interfere with each other"

**Cause:** The circuit and metric state is per-policy per-process. If multiple tests use the same policy name, they share circuit and metric state.

**Fix:** Use a unique policy name per test, or reset the `ServiceProvider` between tests. The sample uses unique policy names per scenario for exactly this reason.

### 24.14 "The events arrive out of order"

**Cause:** Events are emitted from multiple threads (parallel hedges, concurrent retries). Emission order is not guaranteed.

**Fix:** Sort by `TimestampUtc` if order matters. Or filter by event type - most assertions only need "was a `CallSucceeded` seen".

### 24.15 "I cannot resolve ICircuitBreakerMonitor"

**Cause:** You are on a version before `0.7.0`, or the DI registration failed silently.

**Fix:** Confirm `AddPortfolioResilience` ran during startup. Resolve `ICircuitBreakerMonitor` with `GetService<>` (not `GetRequiredService<>`) first to see if it is registered.

---

## 25. Reference

### 25.1 All PolicyDefinition sub-options

    p.Retry.MaxAttempts              // int, default 3
    p.Retry.BaseDelayMs              // int, default 100
    p.Retry.MaxDelayMs               // int, default 5000
    p.Retry.JitterRatio              // double, default 0.0
    p.Retry.RetryOnPermanent         // bool, default false

    p.Circuit.FailureThreshold       // int, default 5
    p.Circuit.OpenDurationSeconds    // int, default 30
    p.Circuit.SuccessThreshold       // int, default 1
    p.Circuit.OnlyCountTransient     // bool, default true

    p.Timeout.TimeoutMs              // int, default 5000

    p.RateLimiter.Enabled            // bool, default false
    p.RateLimiter.Strategy           // RateLimitStrategy, default SlidingWindow
    p.RateLimiter.PermitLimit        // int
    p.RateLimiter.WindowSeconds      // int
    p.RateLimiter.QueueLimit         // int, default 0
    p.RateLimiter.QueueTimeoutMs     // int, default 0
    p.RateLimiter.RejectionCategory  // ResilienceErrorCategory, default Transient

    p.Bulkhead.Enabled               // bool, default false
    p.Bulkhead.MaxConcurrency        // int
    p.Bulkhead.MaxQueue              // int
    p.Bulkhead.QueueTimeoutMs        // int
    p.Bulkhead.RejectionCategory     // ResilienceErrorCategory, default Transient

    p.Hedging.Enabled                // bool, default false
    p.Hedging.MaxAttempts            // int
    p.Hedging.DelayMs                // int
    p.Hedging.ExponentialBackoff     // bool, default false
    p.Hedging.AttemptTimeoutMs       // int, default 0
    p.Hedging.CancelOnSuccess        // bool, default true
    p.Hedging.EmitAttemptEvents      // bool, default true
    p.Hedging.RejectionCategory      // ResilienceErrorCategory, default Transient

    p.Logging.EmitCallStarted        // bool, default false
    p.Logging.EmitRetryAttempted     // bool, default true
    p.Logging.EmitCallSucceeded      // bool, default true
    p.Logging.EmitCallFailed         // bool, default true
    p.Logging.EmitCircuitEvents      // bool, default true
    p.Logging.EmitFallbackUsed       // bool, default true
    p.Logging.EmitTimeoutBreached    // bool, default true
    p.Logging.EmitRateLimited        // bool, default true
    p.Logging.EmitBulkheadRejected   // bool, default true
    p.Logging.EmitHedgeEvents        // bool, default true
    p.Logging.ScrubSensitiveData     // bool, default false

### 25.2 All event types

    CallStarted            0
    RetryAttempted         1
    CallSucceeded          2
    CallFailed             3
    CircuitOpened          4
    CircuitClosed          5
    CircuitHalfOpened      6
    FallbackUsed           7
    TimeoutBreached        8
    RateLimited            9
    BulkheadRejected      10
    HedgeWon              11
    HedgeLost             12
    HedgeCancelled        13

### 25.3 All error categories

    Transient       - network blips, 5xx, 429
    Permanent       - 4xx, ArgumentException, validation
    CircuitOpen     - the circuit was Open
    Timeout         - a timeout ceiling fired
    FallbackUsed    - a fallback produced a value
    Unknown         - the classifier could not categorize

### 25.4 The public API surface

    Abstractions/
      IResilienceExecutor
      IResiliencePolicy
      IResiliencePolicyRegistry
      ILogSink
      IMetricSink
      ILatencyTracker
      IEventScrubber
      ICorrelationAccessor
      ICircuitBreakerMonitor
      CircuitState (enum)
      CircuitSnapshot (record)
      LatencySnapshot (record)

    Configuration/
      PolicyDefinition
      ResilienceBuilder
      ResilienceOptions
      ResilienceOptionsExtensions
      RetryOptions
      CircuitOptions
      TimeoutOptions
      RateLimiterOptions
      RateLimitStrategy (enum)
      BulkheadOptions
      HedgingOptions
      LoggingOptions
      FallbackOptions
      ErrorClassificationOptions
      HttpClientOptions
      StandardPolicy

    Correlation/
      CorrelationContext (static)
      IdempotencyContext (static)
      TimeBudgetContext (static)
      AsyncLocalCorrelationAccessor

    Errors/
      ErrorClassifier
      ResilienceErrorCategory (enum)
      ResilienceException

    Events/
      ResilienceEvent (record)
      ResilienceEventType (enum)

    Extensions/
      ServiceCollectionExtensions
      ConfigurationExtensions

    HttpClient/
      ResilientHttpMessageHandler
      HttpClientBuilderExtensions
      HttpRequestSnapshot

    Implementation/
      ResilienceExecutor
      ResiliencePolicyRegistry
      ResilienceEventEmitter
      CircuitBreakerMonitor
      LatencyTracker

    Middleware/
      ResilienceExceptionMiddlewareBase

    Policies/
      ResiliencePipeline
      ResiliencePipelineBuilder
      CompositePolicyBuilder
      RetryPolicyBuilder
      CircuitPolicyBuilder
      TimeoutPolicyBuilder
      RateLimiterPolicyBuilder
      BulkheadPolicyBuilder
      HedgingPolicyBuilder

    Sinks/
      ConsoleLogSink
      FileLogSink
      NullLogSink
      CompositeLogSink
      CompositeMetricSink
      InMemoryMetricSink
      DefaultPciScrubber

### 25.5 The final shape

    Every call site:
        await _resilience.ExecuteAsync<T>(
            policyName: "...",
            operation: async token => { ... },
            fallback: null,
            idempotencyKey: null,
            timeBudgetMs: null,
            ct: ct);

    Every registration:
        builder.Services.AddPortfolioResilience(r => r
            .AddLogSink(new ConsoleLogSink())
            .AddPolicy("...", p => { ... }));

That is the entire library. Everything else is optional.

---

## Closing

This document covers every feature the library exposes in `0.8.0`. Where behavior differs from documentation, the sample scenarios are the source of truth - each one is executable and prints a `[PASS]` line.

If something in this document is wrong, the code is right. If something in the code is surprising, the tests are right. If a test is surprising, open an issue.

Run the sample:

    dotnet run --project dotnet\samples\Samples.App\Samples.App.csproj

Run the tests:

    dotnet test dotnet\samples\Samples.App.Tests\Samples.App.Tests.csproj

Full solution:

    dotnet build dotnet\Portfolio.Resilience.slnx --configuration Release
    dotnet test dotnet\Portfolio.Resilience.slnx --configuration Release

The library is on nuget.org:
    https://www.nuget.org/packages/Portfolio.Resilience
    https://www.nuget.org/packages/Portfolio.Resilience#versions-tab

The sample is on GitHub:
    https://github.com/sancy1/portfolio-resilience/tree/main/dotnet/samples/Samples.App