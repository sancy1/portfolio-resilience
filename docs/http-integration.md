<!--
filepath: docs/http-integration.md
package:  Portfolio.Resilience | since: v0.4.0
purpose:  Explains how every HttpClient request flows through the resilience pipeline.
-->

# HTTP Integration

## What it is

Every outbound HTTP call made through an `HttpClient` configured with `AddResilientHandler`
runs through the resilience pipeline: retry, circuit breaker, timeout, structured logging,
and latency metrics. The consumer writes no retry logic, no circuit logic, no logging code
at the call site.

## Why it exists

Services call each other over HTTP constantly. Auth checks, user lookups, notification
sends, content fetches. Without a resilient HTTP layer:

- A single slow dependency causes cascading failures
- Transient 503s turn into user-visible errors
- There is no visibility into which dependency is degrading
- Every caller site has to implement its own retry/timeout strategy

With `AddResilientHandler`, the answer to all four problems is one line.

## When you need it

**Every outbound HTTP call to another internal service.** This includes:
- Calling auth-service to validate a token
- Calling notification-service to send an email
- Calling any future service

**Not for:** outbound calls to third-party APIs that already handle their own retries
(e.g., Stripe's SDK, Cloudinary's SDK). Those SDKs ship their own retry logic and adding
a second layer would double-retry.

## How it works

Three components.

### `ResilientHttpMessageHandler` — the handler

Location: `src/Portfolio.Resilience/HttpClient/ResilientHttpMessageHandler.cs`

A `DelegatingHandler` that:
1. Captures the request (method, URI, headers, body)
2. Runs the send through `IResilienceExecutor.ExecuteAsync(policyName, ...)`
3. Classifies responses: 5xx, 408, 425, 429 throw (trigger retry); 2xx/3xx/4xx return to caller
4. On final failure, throws `ResilienceException` with full context

### `HttpRequestSnapshot` — the request cloner

Location: `src/Portfolio.Resilience/HttpClient/HttpRequestSnapshot.cs`

`HttpRequestMessage` is single-use — after the first send, its body stream is consumed.
On retry, `InvalidOperationException: The request message was already sent`.

The snapshot captures method, headers, URI, and body bytes. Each retry calls
`BuildRequest()` which constructs a fresh `HttpRequestMessage`. This is what makes retry
work for POST/PUT requests with bodies.

### `AddResilientHandler` — the fluent API

Location: `src/Portfolio.Resilience/HttpClient/HttpClientBuilderExtensions.cs`

    public static IHttpClientBuilder AddResilientHandler(
        this IHttpClientBuilder builder,
        string policyName)

Attaches the handler to the HttpClient's pipeline via `AddHttpMessageHandler`.

## Usage

### Named client

    builder.Services
        .AddHttpClient("auth-service", c =>
        {
            c.BaseAddress = new Uri(config["AUTH_SERVICE_URL"]!);
            c.Timeout = TimeSpan.FromSeconds(30);  // HttpClient's own ceiling
        })
        .AddResilientHandler("auth-service");

    // Register a policy in the same startup:
    builder.Services.AddPortfolioResilience(r =>
        r.AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts = 3;
            p.Timeout.TimeoutMs = 5000;
            p.Circuit.FailureThreshold = 5;
        }));

Then at the call site:

    public sealed class UserService
    {
        private readonly HttpClient _http;

        public UserService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient("auth-service");
        }

        public Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct)
            => _http.GetFromJsonAsync<UserDto>($"/api/v1/users/{id}", ct);
    }

**That is the entire integration.** No retry code. No circuit code. No timeout code.
No logging. The handler does all of it.

### Typed client

    builder.Services
        .AddHttpClient<AuthClient>(c => c.BaseAddress = new Uri(url))
        .AddResilientHandler("auth-service");

Same behavior, injected as `AuthClient` instead of through the factory.

### The one-liner — `AddStandardResilienceHandler`

For most HTTP clients you want resilience with **sensible defaults** and no
policy definition ceremony. One call does it:

    builder.Services
        .AddHttpClient("auth-service", c => c.BaseAddress = new Uri(url))
        .AddStandardResilienceHandler();

Behind the scenes, the extension creates a policy named `standard` (if one is
not already registered) and routes every request through it.

**The standard policy contains:**

| Feature | Enabled | Configuration |
|---------|---------|---------------|
| Retry | ? | `MaxAttempts = 3`, `BaseDelayMs = 100`, `MaxDelayMs = 5000`, `JitterRatio = 0.3` |
| Circuit breaker | ? | `FailureThreshold = 5`, `OpenDurationSeconds = 30` |
| Timeout | ? | `TimeoutMs = 30_000` (30 seconds) |
| Rate limiter | ? | Off — not every HTTP call needs rate limiting |
| Bulkhead | ? | Off — not every HTTP call needs concurrency capping |
| Hedging | ? | Off — **hedging is never enabled by default**, because it duplicates requests |

**Customize the standard policy inline:**

    builder.Services
        .AddHttpClient("auth-service", c => c.BaseAddress = new Uri(url))
        .AddStandardResilienceHandler(p =>
        {
            p.Retry.MaxAttempts = 5;
            p.Timeout.TimeoutMs = 10_000;
        });

The callback runs once, on the fresh policy, before it is registered.

**Override it entirely.** If you register your own policy named `standard`
via `AddPolicy` **before** calling `AddStandardResilienceHandler()`, your
policy wins. The extension detects the existing registration and does nothing.

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("standard", p =>
        {
            p.Retry.MaxAttempts = 10;
            p.Timeout.TimeoutMs = 60_000;
        }));

    builder.Services
        .AddHttpClient("auth-service")
        .AddStandardResilienceHandler();   // uses YOUR standard policy

**Call order does not matter** between `AddStandardResilienceHandler()` and
`AddPortfolioResilience()`. The policy is injected lazily at resolve time,
and every registration made before the first resolve is seen.

**When not to use the one-liner:**

- The client needs a **specific per-dependency policy** (rate limit, bulkhead,
  hedging, or non-standard retry counts). Register that policy and use
  `AddResilientHandler("your-policy-name")` instead.
- The client makes **non-idempotent writes** and you might be tempted to enable
  hedging later. The `standard` policy deliberately does not enable hedging —
  keep it that way for writes.
## Idempotency-Key header

Every request the handler sends carries the ambient idempotency key in a header
(default `Idempotency-Key`). The header is added **on every attempt** - the
primary request and every retried request - with the same value, so a
downstream service that deduplicates on the key sees one logical write.

Set the key per call site:

    await _resilience.ExecuteAsync(
        "stripe-charge",
        ct => http.SendAsync(request, ct),
        idempotencyKey: $"order-{orderId}",
        ct: ct);

Or scope a block of writes under a shared key:

    using (IdempotencyContext.Push($"batch-{batchId}"))
    {
        await _resilience.ExecuteAsync("stripe-charge", ct => ChargeAsync(ct), ct: ct);
        await _resilience.ExecuteAsync("stripe-charge", ct => RefundAsync(ct), ct: ct);
    }

For a provider with a different header convention, configure the handler:

    builder.Services
        .AddHttpClient("custom-psp")
        .AddResilientHandler("custom-psp", options =>
        {
            options.IdempotencyHeaderName = "X-Idempotency";
        });

If no key is supplied and no ambient key is active, the executor derives one
from the correlation ID (`idem-{correlation_id}`), guaranteeing retries of the
same call carry the same value.

**See [idempotency.md](idempotency.md) for the full story.**
## What the caller sees on failure

When the pipeline exhausts retries or the circuit is open, the caller receives a
`ResilienceException`:

    catch (ResilienceException ex)
    {
        ex.PolicyName       // "auth-service"
        ex.Category         // Transient | Permanent | CircuitOpen | Timeout
        ex.AttemptsMade     // 3
        ex.TotalDuration    // 00:00:01.234
        ex.CorrelationId    // "abc-123"
        ex.Metadata         // { "timeout_ms": 5000, ... }
        ex.InnerException   // the original HttpRequestException
    }

**Services handle this in one place:** the `ResilienceExceptionMiddlewareBase`. See
docs/executor.md §"Handling failures at the service boundary."

## HTTP status classification

By default:

| Status | Classified as | Behavior |
|--------|---------------|----------|
| 2xx | success | Returned to caller |
| 3xx | success | Returned (shouldn't happen if auto-redirect is on) |
| 400, 401, 403, 404, 422 | permanent | Returned to caller (no retry) |
| 408 | transient | Retried, then thrown |
| 425, 429 | transient | Retried, then thrown |
| 5xx | transient | Retried, then thrown |

**Overridable** via `ErrorClassificationOptions.TransientHttpStatusCodes` and
`PermanentHttpStatusCodes`.

**Note:** 4xx responses are NOT converted to exceptions by the handler. They are
returned to the caller unchanged. Only transient statuses are converted to exceptions
so retry can trigger. This is deliberate: a 404 is a legitimate answer, not a failure.

## Timeouts: two layers

You have two timeout ceilings, and understanding the interaction matters.

| Layer | Where it lives | What it does |
|-------|----------------|--------------|
| **HttpClient.Timeout** | `HttpClient` property | Ceiling on the **whole send + retries** |
| **Policy TimeoutMs** | `PolicyDefinition.Timeout` | Ceiling on **each attempt** |

**Recommended configuration:** make `HttpClient.Timeout` significantly larger than
`Policy.TimeoutMs * (MaxAttempts + 1)`:

    c.Timeout = TimeSpan.FromSeconds(60);     // HttpClient outer ceiling
    p.Timeout.TimeoutMs = 5000;                // per attempt
    p.Retry.MaxAttempts = 3;                   // 4 total attempts

    // Max total: 4 * 5000 + backoff ~= 20-25 seconds.
    // HttpClient's 60s gives comfortable headroom.

If `HttpClient.Timeout` is smaller than the total retry budget, the outer timeout fires
first and the caller sees `TaskCanceledException` instead of the structured
`ResilienceException`. **Always leave headroom.**

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/HttpClient/ResilientHttpMessageHandler.cs` | The DelegatingHandler |
| `src/Portfolio.Resilience/HttpClient/HttpRequestSnapshot.cs` | Request rebuilding across retries |
| `src/Portfolio.Resilience/HttpClient/HttpClientBuilderExtensions.cs` | `AddResilientHandler` fluent API |
| `src/Portfolio.Resilience/Implementation/ResilienceExecutor.cs` | The pipeline entry point |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | The pipeline itself |

## Common mistakes

**Mistake 1 — setting HttpClient.Timeout to the same value as Policy.TimeoutMs.**
The HttpClient timeout would fire on the first attempt, before any retry runs. The
caller sees `TaskCanceledException`, not `ResilienceException`.

**Mistake 2 — expecting a 404 to throw.**
It won't. 404 is a legitimate response and is returned to the caller. Only transient
statuses throw. Check `response.IsSuccessStatusCode` at the call site if you want to
branch on non-success.

**Mistake 3 — reusing an HttpRequestMessage across SendAsync calls.**
Even with our handler, this fails. The handler clones for its internal retries, but if
you manually call `SendAsync(sameMessage)` twice, the second fails. Always create a new
`HttpRequestMessage` per logical call.

**Mistake 4 — using the resilient handler for streaming responses.**
A long-lived SSE or gRPC stream will hit the per-attempt timeout. Use a separate
HttpClient without `AddResilientHandler` for streaming, or configure a very high
per-attempt timeout for that policy.

**Mistake 5 — forgetting to register the policy.**
If you attach `.AddResilientHandler("auth-service")` but never call
`AddPolicy("auth-service", ...)`, the handler uses the **default policy**. It works,
but with default thresholds. Register the policy explicitly to tune for the dependency.

## Testing

The `ResilientHttpMessageHandlerTests` verify:
- Happy path (200, no retry)
- Retry on 503 (succeeds on 2nd attempt)
- Exhausted retries (throws `ResilienceException` with `Transient` category)
- 404 doesn't retry (returns to caller)
- Request rebuilt per retry (different HttpRequestMessage instances)
- POST body preserved across retries

See `tests/Portfolio.Resilience.Tests/ResilientHttpMessageHandlerTests.cs` (9 tests).

## See also

- [executor.md](executor.md) - the underlying pipeline
- [circuit-breaker.md](circuit-breaker.md) - how circuit state affects HTTP calls
- [timeout.md](timeout.md) - timeout semantics in detail
- [error-classification.md](error-classification.md) - HTTP status classification rules
- [../SPEC.md](../SPEC.md) §HttpIntegration
