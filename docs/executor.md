<!--
filepath: docs/executor.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Explains the executor — the single entry point for every resilient operation.
-->

# Executor

## What it is

The **executor** (`IResilienceExecutor`) is the public API of the library. Every
resilient call in every service goes through one of its two methods:

    Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task<T>>? fallback = null,
        CancellationToken ct = default);

    Task ExecuteAsync(
        string policyName,
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task>? fallback = null,
        CancellationToken ct = default);

The executor:
1. Resolves the policy by name via `IResiliencePolicyRegistry`.
2. Runs the operation through the composite pipeline (retry → circuit → timeout).
3. Emits structured events at each stage.
4. Records latency metrics and in-flight counts.
5. Handles optional per-call fallback.
6. Wraps failures in `ResilienceException` with full context.

Everything else in the library exists to make these two methods work correctly.

## Why it exists

Without a single entry point, each service would have to:

- Implement retry logic (again, differently)
- Implement circuit breaking (again, differently)
- Implement timeouts (again, differently)
- Wire up logging and metrics (again, differently)
- Handle correlation IDs (again, differently)
- Catch and translate errors (again, differently)

**Six places to get wrong, times N services.** That is how libraries die.

The executor gives every service one consistent, correct, tested path. Call sites
are two lines: acquire the executor (via DI), call `ExecuteAsync`.

**The alternative** — each service manually wrapping calls in Polly or
hand-rolled logic — was the industry norm ten years ago. It produces inconsistent
behavior, silent bugs, and observability gaps.

**The executor** centralizes the pipeline. The library owns the semantics; the
service owns the operation.

## When you need it

**Every time** an operation crosses a process boundary:

- HTTP call to another service
- Database query (via a repository interface)
- Redis get/set/delete
- Message queue publish/consume
- Third-party API call (Stripe, OpenAI, etc.)

**Never** for:

- Pure functions (parsing, math, formatting)
- In-memory dictionary access
- String concatenation
- Validation logic

The rule: **if it can fail transiently because of something outside your process,
it goes through the executor.**
## How it works

### The pipeline

The executor runs the operation through four layers, nested inside-out:

    Caller
      └─> Retry        (outermost — can re-run the inner layers)
            └─> Circuit  (middle — can reject without running the operation)
                  └─> Timeout  (innermost — bounds a single attempt)
                        └─> Operation
                              └─> returns or throws

**Order rationale (repeated from retry.md because it matters):**

- **Retry outermost:** each retry attempt gets a fresh circuit check and a fresh
  timeout budget. A slow attempt does not consume the budget of subsequent
  attempts.
- **Circuit middle:** when the circuit is Open, the operation is never attempted.
  No timeout runs, no resource is consumed. Fail-fast at its fastest.
- **Timeout innermost:** bounds a single attempt. Retries multiply the total time,
  but each attempt has the same ceiling.

### Walkthrough of a call

Consider `ExecuteAsync("auth-service", ct => _client.GetUserAsync(id, ct))` where
`auth-service` policy is:

    Retry.MaxAttempts = 3, Retry.BaseDelayMs = 100
    Circuit.FailureThreshold = 5, Circuit.OpenDurationSeconds = 30
    Timeout.TimeoutMs = 5000

**Step-by-step:**

1. **Resolve policy.** The registry looks up `"auth-service"`. If the policy does
   not exist, it uses `ResilienceOptions.DefaultPolicy`, but keeps the policy
   name `"auth-service"` in events and metrics.

2. **Emit `CallStarted`.** The event is written to the log sink.

3. **Mark in-flight.** `InMemoryMetricSink.BeginInFlight("auth-service")`.

4. **Attempt 1.** Retry calls the circuit gate.
   - Circuit is Closed. Allowed.
   - Circuit calls timeout with a fresh 5000ms window.
   - Timeout calls the operation.

   Suppose the operation throws a `TimeoutException` after 200ms.

5. **Circuit records the failure.** It is `Transient` by classification, so
   `ConsecutiveFailures` goes from 0 to 1.

6. **Retry sees the failure.** Not exhausted yet (`MaxAttempts = 3`). Delay =
   `100ms + jitter`. Emits `RetryAttempted`. Sleeps.

7. **Attempt 2.** Same as attempt 1. Suppose this time the operation succeeds
   after 150ms.

8. **Circuit records success.** `ConsecutiveFailures` resets to 0. State remains
   Closed.

9. **Timeout returns** the result. **Circuit returns** the result. **Retry returns**
   the result.

10. **Emit `CallSucceeded`.** Record latency. `EndInFlight`. Return to caller.

**Total time:** ~200ms (fail) + ~120ms (delay) + 150ms (success) = ~470ms.
**Caller sees:** the successful result. **Events emitted:** `CallStarted`,
`RetryAttempted`, `CallSucceeded`. **Metrics recorded:** one successful sample.

**What if the circuit had been Open?**
- Attempt 1: circuit rejects immediately with `ResilienceException(CircuitOpen)`.
- Retry sees `CircuitOpen`. Retry **does not retry** on this category (the circuit
  will reject until `OpenDurationSeconds` passes).
- Emit `CallFailed` with `error_category = CircuitOpen`.
- Record failed metric.
- Throw `ResilienceException(CircuitOpen)` to caller.
- **Total time:** microseconds. **Operation never invoked.**

### Fallback

The optional `fallback` parameter is invoked **after** the pipeline fails. It runs
**outside** the resilience pipeline — no retries, no circuit, no timeout.

    var user = await _executor.ExecuteAsync(
        "auth-service",
        ct => _authClient.GetUserAsync(id, ct),
        fallback: ct => Task.FromResult(UserDto.Anonymous),
        ct: ct);

If the pipeline exhausts retries, or the circuit is Open, or the timeout fires —
whichever fails last — the executor catches the failure and invokes the fallback.

**Fallback is per-call, not per-policy.** Whether a fallback makes sense depends on
the call site, not on the dependency. A user lookup can fall back to
`UserDto.Anonymous`; a payment call cannot meaningfully fall back to anything.

**If the fallback itself throws:**
- The executor wraps both errors in a single `ResilienceException`.
- `Metadata["fallback_error"]` contains the fallback's message.
- `InnerException` is the fallback's exception.
- The original pipeline error is logged (via `CallFailed`) before the fallback
  runs, so both failures are visible in logs.

See `docs/fallback.md` for the full fallback resolution rules.
### Handling failures at the service boundary

Every call ultimately either succeeds or throws `ResilienceException`. The
exception carries everything a boundary handler needs:

    catch (ResilienceException rex)
    {
        rex.PolicyName       // "auth-service"
        rex.Category         // Transient | Permanent | CircuitOpen | Timeout | FallbackUsed | Unknown
        rex.AttemptsMade     // 3
        rex.TotalDuration    // TimeSpan
        rex.CorrelationId    // "abc-123"
        rex.Metadata         // { "timeout_ms": 5000, "elapsed_ms": 5012, ... }
        rex.InnerException   // the original HttpRequestException / NpgsqlException / etc.
    }

**Do NOT catch this in every controller.** Instead, install
`ResilienceExceptionMiddlewareBase` (inherited) once, and each service decides how
to render the error.

    public sealed class LandingPageResilienceMiddleware : ResilienceExceptionMiddlewareBase
    {
        public LandingPageResilienceMiddleware(RequestDelegate next, ILogger<LandingPageResilienceMiddleware> logger)
            : base(next, logger) { }

        protected override async Task RenderErrorAsync(HttpContext ctx, ResilienceException rex)
        {
            ctx.Response.StatusCode = rex.Category switch
            {
                ResilienceErrorCategory.CircuitOpen => StatusCodes.Status503ServiceUnavailable,
                ResilienceErrorCategory.Timeout     => StatusCodes.Status504GatewayTimeout,
                ResilienceErrorCategory.Permanent   => StatusCodes.Status400BadRequest,
                _                                    => StatusCodes.Status502BadGateway
            };

            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "upstream_error",
                category = rex.Category.ToString(),
                correlationId = rex.CorrelationId
            });
        }
    }

**The base class handles:**
- Catching `ResilienceException` (other exceptions propagate)
- Setting the `X-Correlation-Id` response header
- Structured logging of the failure
- Guard against already-started responses

**The service handles:**
- HTTP status mapping
- Response body shape
- Content type

**This is the "share the engine, own the interface" pattern.** Different services
render errors differently. The library only shares the mechanism.

### Response envelope ownership

**Shared across services (from the library):**
- `ResilienceException` type and its fields
- `ResilienceErrorCategory` enum values
- The `X-Correlation-Id` response header name
- Structured log event names and field names
- Metric names

**Owned per service:**
- HTTP status code mapping
- Error response body schema
- Error code strings ("upstream_unavailable" vs "UPSTREAM_TIMEOUT")
- Consumer-facing messages
- Pagination, DTO shapes, API versioning

The SPEC explicitly documents this split. See `SPEC.md` §ResponseEnvelopeOwnership.

### Structured events

Every executor call emits events. The default sink is `NullLogSink` (silent). To
see events, register a real sink:

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink()));

Events are JSON Lines in the sink's output:

    {"event_type":"call_started","policy_name":"auth-service","correlation_id":"abc-123","timestamp_utc":"2026-09-12T13:01:06Z"}
    {"event_type":"retry_attempted","policy_name":"auth-service","correlation_id":"abc-123","attempt":1,"metadata":{"delay_ms":120}}
    {"event_type":"call_succeeded","policy_name":"auth-service","correlation_id":"abc-123","attempt":2,"duration_ms":470}

See `docs/logging.md` for the full event schema and sink options.

### Metrics

Every call records latency, success/failure, and attempt count in the metric sink.
The default is `InMemoryMetricSink`, which supports read-side queries via
`ILatencyTracker`:

    var tracker = sp.GetRequiredService<ILatencyTracker>();
    var snap = tracker.Get("auth-service");
    // snap.P50Ms, snap.P95Ms, snap.P99Ms, snap.ErrorRate, snap.TotalCalls

This is what powers the `/health/resilience` endpoint. See `docs/metrics.md`.
## Registration

Register the library once during startup:

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts         = 3;
            p.Retry.BaseDelayMs         = 100;
            p.Circuit.FailureThreshold  = 5;
            p.Circuit.OpenDurationSeconds = 30;
            p.Timeout.TimeoutMs         = 5000;
        })
        .AddPolicy("notification-service", p => { ... })
    );

**What `AddPortfolioResilience` registers:**

| Interface | Implementation | Lifetime |
|-----------|----------------|----------|
| `IResilienceExecutor` | `ResilienceExecutor` | Singleton |
| `IResiliencePolicyRegistry` | `ResiliencePolicyRegistry` | Singleton |
| `ILogSink` | Configured sink (or `NullLogSink`) | Singleton |
| `IMetricSink` | `InMemoryMetricSink` (or composite) | Singleton |
| `ICorrelationAccessor` | `AsyncLocalCorrelationAccessor` | Singleton |
| `ILatencyTracker` | `LatencyTracker` | Singleton |
| `ICircuitBreakerMonitor` | `CircuitBreakerMonitor` | Singleton |
| `RetryPolicyBuilder` | Concrete | Singleton |
| `TimeoutPolicyBuilder` | Concrete | Singleton |
| `CircuitPolicyBuilder` | Concrete | Singleton |
| `CompositePolicyBuilder` | Concrete | Singleton |
| `ResilienceEventEmitter` | Concrete | Singleton |

## Using the executor in a service

    public sealed class UserService
    {
        private readonly IResilienceExecutor _resilience;
        private readonly HttpClient _http;

        public UserService(
            IResilienceExecutor resilience,
            IHttpClientFactory httpFactory)
        {
            _resilience = resilience;
            _http = httpFactory.CreateClient("auth-service");
        }

        public Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct)
        {
            return _resilience.ExecuteAsync(
                "auth-service",
                async token => await _http.GetFromJsonAsync<UserDto>(
                    $"/api/v1/users/{id}", token),
                fallback: token => Task.FromResult<UserDto?>(UserDto.Anonymous),
                ct: ct);
        }
    }

**That's the whole call site.** No retry, no circuit, no timeout, no logging, no
metrics. All of that lives in the pipeline.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Implementation/ResilienceExecutor.cs` | The executor |
| `src/Portfolio.Resilience/Abstractions/IResilienceExecutor.cs` | Interface |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | Pipeline composition |
| `src/Portfolio.Resilience/Implementation/ResilienceEventEmitter.cs` | Event emission |
| `src/Portfolio.Resilience/Implementation/ResiliencePolicyRegistry.cs` | Policy resolution |
| `src/Portfolio.Resilience/Implementation/LatencyTracker.cs` | Read-side metrics |
| `src/Portfolio.Resilience/Implementation/CircuitBreakerMonitor.cs` | Read-side circuit state |
| `src/Portfolio.Resilience/Extensions/ServiceCollectionExtensions.cs` | DI registration |
| `src/Portfolio.Resilience/Middleware/ResilienceExceptionMiddlewareBase.cs` | Boundary handler base |

## Common mistakes

**Mistake 1 — catching ResilienceException everywhere.**
Install the middleware base once. Let the pipeline's exception propagate to it. Do
not wrap every controller action in try/catch.

**Mistake 2 — passing the caller's CancellationToken as the operation's only token.**
The pipeline uses the token internally. Pass `ct` at the top-level `ExecuteAsync`
call, and let the pipeline thread its own linked token into the operation.

**Mistake 3 — using a fallback for everything.**
A fallback that hides a real failure is worse than the failure. Use fallbacks only
when "a valid degraded response" is genuinely acceptable to the caller.

**Mistake 4 — expecting the fallback to run inside the pipeline.**
The fallback runs **outside** — no retries, no circuit, no timeout. If the fallback
itself depends on an external service, wrap that dependency in its own resilient
call.

**Mistake 5 — not registering a policy for a dependency.**
If you attach `AddResilientHandler("auth-service")` but never call
`AddPolicy("auth-service", ...)`, the default policy is used. It works — but the
thresholds may not match the dependency's characteristics. Always register explicit
policies for known dependencies.

**Mistake 6 — inspecting events from a non-log sink.**
The event sink contract (`ILogSink.Emit(ResilienceEvent)`) is single-method. If you
need to observe events in-process (e.g., for tests), implement a small capturing
sink:

    public sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent e) => Events.Add(e);
    }

## Testing

Verified by `tests/Portfolio.Resilience.Tests/ResilienceExecutorTests.cs` (14 tests):

- Null policy name / null operation rejected
- Success path returns result, emits `CallStarted` + `CallSucceeded`, records metric
- Transient failure retried, succeeds on later attempt
- Exhausted retries throw `ResilienceException` with policy name and category
- With fallback → fallback value returned, `FallbackUsed` event emitted
- Fallback throws → `ResilienceException` with `fallback_error` metadata
- Correlation ID from ambient context propagates to all events
- Non-generic `ExecuteAsync` returns a Task, fallback invoked on failure
- In-flight count is 1 during execution, 0 after

## See also

- [error-classification.md](error-classification.md) — what determines category
- [retry.md](retry.md) — the outermost pipeline layer
- [circuit-breaker.md](circuit-breaker.md) — the middle pipeline layer
- [timeout.md](timeout.md) — the innermost pipeline layer
- [fallback.md](fallback.md) — full fallback resolution rules
- [logging.md](logging.md) — event schema and sinks
- [metrics.md](metrics.md) — latency tracking
- [correlation.md](correlation.md) — correlation ID propagation
- [http-integration.md](http-integration.md) — the delegating handler
- [../SPEC.md](../SPEC.md) §Executor — the normative pipeline contract
