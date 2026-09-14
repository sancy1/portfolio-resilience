<!--
filepath: docs/idempotency.md
package:  Portfolio.Resilience | since: v0.8.0
purpose:  Explains idempotency-key propagation - what it is, why it matters, and how every retry attempt carries the same key.
-->

# Idempotency Key Propagation

## What it is

Idempotency-key propagation attaches a caller-supplied (or automatically
derived) key to **every attempt** of an operation - the primary call, every
retry, and every hedged attempt. The key travels through an ambient context
(`IdempotencyContext`) and is emitted as an HTTP header (default
`Idempotency-Key`) by `ResilientHttpMessageHandler`.

The result: a retried `POST /charge` reaches the payment provider with the
same key on every attempt. The provider deduplicates. One logical charge.

## Why it exists

**For any non-idempotent write - charges, order creation, event publishing,
notification sends - not just payments.**

Retry and hedging deliberately run operations more than once. For reads, that
is safe. For writes, it is dangerous:

- A retried `POST /charge` can create two charges if the server does not
  deduplicate.
- A hedged `POST /orders` can create two orders.
- A retried `POST /notifications` can send two emails.

The library has shipped hedging since v0.7.0 with a loud warning: *"Not safe for
non-idempotent operations."* v0.8.0 removes that caveat by making the key
propagation automatic.

**Payment-grade means every non-idempotent write carries a key on every
attempt.** The provider's deduplication window (Stripe: 24 hours, Adyen: 7
days, Square: forever) guarantees at-most-once semantics even when the network
forces at-least-once delivery.

## When you need it

**Always, for any non-idempotent write that goes through the executor:**

- HTTP POST that creates a resource (charge, order, subscription, refund)
- Event publishing to a broker without built-in deduplication
- Sending emails or push notifications
- Any call that would cause harm if run twice

**Not needed for:**

- HTTP GET, HEAD, OPTIONS, or any read
- HTTP PUT, PATCH, or DELETE when the target is naturally idempotent
- In-memory operations with no external side effect

If you are unsure, supply a key. The cost is one dictionary lookup and one
header.

## How it works

### The executor

`IResilienceExecutor.ExecuteAsync` accepts an optional `idempotencyKey`
parameter. The resolution order is:

1. **Explicit parameter wins.** `idempotencyKey: "order-12345"` is used as-is.
2. **Ambient key is honored.** If the caller already pushed a key via
   `IdempotencyContext.Push(...)`, that value is preserved.
3. **Fallback: derived from correlation ID.** If neither is present, the
   executor generates `idem-{correlation-id}` so retries still carry a stable
   key. If there is no correlation ID either, a fresh GUID is used.

The chosen key is pushed onto `IdempotencyContext` for the duration of the
call and popped when the call returns - success or failure.

### The HTTP handler

`ResilientHttpMessageHandler` reads `IdempotencyContext.CurrentKey` inside its
per-attempt closure and adds the configured header to **every** request it
sends - the first attempt and every retry. The header name defaults to
`Idempotency-Key` (Stripe, Adyen, Square all use this name). It is configurable
via `HttpClientOptions.IdempotencyHeaderName`.

### Payment example

    var charge = await _resilience.ExecuteAsync(
        "stripe-charge",
        ct => _stripe.ChargeAsync(request, ct),
        idempotencyKey: $"order-{orderId}",
        ct: ct);

If the network drops the response after Stripe accepted the charge, retry
re-sends with the same key. Stripe returns the original charge instead of
creating a second one.

### Generic microservice example

    var response = await _resilience.ExecuteAsync(
        "order-service",
        ct => _http.PostAsJsonAsync("/api/v1/orders", order, ct),
        idempotencyKey: $"order-{orderId}",
        ct: ct);

Order creation, event publishing, notification sends - all follow the same
pattern. Any operation that would be harmful to run twice accepts a key.

## Configuration

### The header name

Default: `Idempotency-Key`. Override per HTTP client:

    builder.Services
        .AddHttpClient("stripe-charge", c => c.BaseAddress = new Uri(url))
        .AddResilientHandler("stripe-charge", options =>
        {
            options.IdempotencyHeaderName = "Idempotency-Key";  // default, kept for clarity
        });

For a provider that uses a different convention:

    .AddResilientHandler("custom-psp", options =>
    {
        options.IdempotencyHeaderName = "X-Request-Id";
    });

### The key source

Three sources, in priority order:

1. **Per-call parameter** - the explicit case, recommended for most call sites.
2. **Ambient context** - useful for a scoped block that wraps several calls:
   `using (IdempotencyContext.Push("batch-42")) { /* multiple calls */ }`
3. **Auto-derived from correlation ID** - the safety net; ensures retries
   always carry a stable key even if the caller forgets.

## Quick start

### 1. Supply a key per call site

    await _resilience.ExecuteAsync(
        "stripe-charge",
        ct => _stripe.ChargeAsync(request, ct),
        idempotencyKey: $"order-{orderId}",
        ct: ct);

### 2. Or use an ambient scope

    using (IdempotencyContext.Push($"batch-{batchId}"))
    {
        await _resilience.ExecuteAsync("stripe-charge", ct => ChargeAsync(ct), ct: ct);
        await _resilience.ExecuteAsync("stripe-charge", ct => RefundAsync(ct), ct: ct);
    }

Both calls carry the same `batch-{batchId}` key. This is useful when several
writes belong to one logical unit of work.

### 3. Or rely on correlation ID (do not do this for production payments)

    using (CorrelationContext.Push("req-abc-123"))
    {
        await _resilience.ExecuteAsync(
            "stripe-charge",
            ct => _stripe.ChargeAsync(request, ct),
            ct: ct);
        // key becomes "idem-req-abc-123"
    }

The auto-derived key is stable across retries of one call, but two calls with
different correlation IDs get different keys - which is exactly right for
"different logical writes" and exactly wrong for "same logical write retried
from a fresh request." **For production payments, always supply an explicit
key derived from your domain (order ID, charge ID, etc.).**

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Correlation/IdempotencyContext.cs` | Ambient AsyncLocal store |
| `src/Portfolio.Resilience/Configuration/HttpClientOptions.cs` | Configurable header name |
| `src/Portfolio.Resilience/Implementation/ResilienceExecutor.cs` | Accepts the key, pushes ambient |
| `src/Portfolio.Resilience/HttpClient/ResilientHttpMessageHandler.cs` | Emits the header per attempt |
| `src/Portfolio.Resilience/HttpClient/HttpClientBuilderExtensions.cs` | `AddResilientHandler` overload |

## Common mistakes

**Mistake 1 - reusing a key across different logical writes.**
`order-12345` should identify one charge, not "any charge from order 12345".
If your flow can charge twice (retry from the user, scheduled retry, etc.),
use a key derived from the specific attempt's identity - a charge-attempt ID,
not just the order ID.

**Mistake 2 - not supplying a key at all for payments.**
The auto-derived fallback works, but it is derived from the correlation ID.
Two retried calls of the same logical write with different correlation IDs
will get different keys. **Always supply an explicit key for money paths.**

**Mistake 3 - expecting the library to deduplicate.**
The library propagates the key. Deduplication happens at the downstream
provider (Stripe, Adyen, Square, your own service). **The library's job is to
ensure the key arrives; the provider's job is to act on it.**

**Mistake 4 - configuring a header name your provider does not accept.**
If you override `IdempotencyHeaderName`, verify the provider's expected name.
Stripe, Adyen, and Square all use `Idempotency-Key`. Stripe's API also accepts
`Idempotency-Key` on every write endpoint. Do not invent a custom name unless
the provider documents it.

**Mistake 5 - using a correlation ID as the idempotency key.**
They are different concepts. The correlation ID ties a request to a trace; the
idempotency key ties a write to a logical operation. Derive the key from your
domain (`order-{id}`, `charge-{id}`), not from infrastructure.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/IdempotencyContextTests.cs` (11 tests),
`HttpClientOptionsTests.cs` (7 tests), and the extended `ResilienceExecutorTests.cs`
and `ResilientHttpMessageHandlerTests.cs` (4 + 4 tests), plus
`ResilienceIntegrationTestsV08_Idempotency.cs` (4 end-to-end tests).

## See also

- [correlation.md](correlation.md) - the other ambient context
- [http-integration.md](http-integration.md) - where the header is emitted
- [executor.md](executor.md) - the API surface
- [hedging.md](hedging.md) - why this feature exists (hedged writes without a key)
- [../SPEC.md](../SPEC.md) section 18 - the normative contract
