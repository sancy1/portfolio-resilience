<!--
filepath: docs/correlation.md
package:  Portfolio.Resilience | since: v0.2.0
purpose:  Explains correlation IDs, AsyncLocal propagation, and HTTP header handoff.
-->

# Correlation

## What it is

A **correlation ID** is a short unique string (usually a 32-character hex GUID) that is
attached to a single logical unit of work — typically one inbound HTTP request. As that
request travels through multiple services, every log line, every metric sample, and every
outbound HTTP call carries the same ID.

Result: you can query all your logs with `correlation_id = "abc123"` and see the **entire
story** of one user action — even if it touched five services.

## Why it exists

Without correlation IDs:

- You see `ERROR: upstream timeout` in a log line and have no idea which user request it
  belonged to, which downstream call caused it, or which other errors in the same second
  were part of the same incident.
- When a user reports "the contact form failed at 3:15pm", you grep by timestamp and get
  thousands of candidate log lines across services.
- Distributed traces (OpenTelemetry, Datadog APM) have nothing to stitch together.

With correlation IDs:

- One ID = one full trace. All logs and metrics for that request share the ID.
- Every `ResilienceEvent` includes `correlation_id`, so retries, circuit events, and
  fallbacks are automatically linked to their originating request.
- A user report of "failed at 3:15pm" turns into `GET /api/v1/contact failed correlation_id=abc123`,
  and every service that touched that request is instantly visible.

## When you need it

**Always.** Every request that enters landing-page-service (or any future service) must
have a correlation ID. The middleware assigns one at the edge if the incoming request
does not already provide one.

If a caller sends `X-Correlation-Id: abc123`, we adopt it. If they do not, we generate one.
Either way, every downstream call carries the same value.

## How it works

Two files, one concern:

### CorrelationContext — the primitive

Location: `src/Portfolio.Resilience/Correlation/CorrelationContext.cs`

A **static class** wrapping an `AsyncLocal<string?>`. This is the storage mechanism.

    public static class CorrelationContext
    {
        public static string? CurrentId { get; }      // read
        public static IDisposable Push(string id);    // set (returns restore-handle)
        public static string NewId();                 // generate
    }

**Why static?** Because correlation is ambient — like `DateTime.UtcNow` or `Activity.Current`.
Every layer of the code needs to read it without DI plumbing. A static `AsyncLocal` gives us
ambient access without threading it through every method signature.

**Why AsyncLocal and not ThreadLocal?** Because async code migrates between threads.
When you await, the continuation may run on a different thread than the one that started
the await. `AsyncLocal<T>` correctly flows the value across those thread hops; `ThreadLocal<T>`
does not.

**Why return IDisposable?** So you can use using blocks and get automatic restoration:

    using (CorrelationContext.Push("abc123"))
    {
        // Any code here — including awaited code — sees abc123
    }
    // Previous value (possibly null) restored automatically

Nested pushes compose correctly. The outermost push wins for the duration of its scope.

### AsyncLocalCorrelationAccessor — the DI adapter

Location: `src/Portfolio.Resilience/Correlation/AsyncLocalCorrelationAccessor.cs`

A thin wrapper implementing `ICorrelationAccessor`. It delegates to `CorrelationContext`.

**Why both?** Because some code wants to **inject** the accessor (for testability), and some
code wants to **read** the ambient value (for convenience). The primitive handles the second
case; the adapter handles the first.

- **Ambient access:** `var id = CorrelationContext.CurrentId;`
- **Injected access:** constructor takes `ICorrelationAccessor correlation`

Both observe the same underlying value.

## How to use it

### At the edge — assign or adopt

In an ASP.NET Core middleware:

    var incomingId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
    var correlationId = string.IsNullOrWhiteSpace(incomingId)
        ? CorrelationContext.NewId()
        : incomingId;

    using (CorrelationContext.Push(correlationId))
    {
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        await _next(context);
    }

The correlation ID is now active for the entire request. Every awaited call sees it.

### In library code — read the current value

    var correlationId = CorrelationContext.CurrentId;
    logger.LogInformation("Processing order {OrderId} correlation={Correlation}",
        orderId, correlationId);

### On outbound HTTP — propagate the header

    httpClient.DefaultRequestHeaders.Add("X-Correlation-Id",
        CorrelationContext.CurrentId ?? CorrelationContext.NewId());

The next service receives the same ID, adopts it, and passes it onward. The full chain is
traceable end to end.

## Configuration

None. The correlation subsystem has no tuning knobs. That is deliberate — a correlation ID
is either present or it is not; there is no sensible "half configured" state.

The **header name** is fixed at `X-Correlation-Id`. This is a contract with the other services
in the portfolio (auth-service, notification-service, future services) so traces stitch
together across the whole platform.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Correlation/CorrelationContext.cs` | Ambient `AsyncLocal` primitive |
| `src/Portfolio.Resilience/Correlation/AsyncLocalCorrelationAccessor.cs` | DI adapter |
| `src/Portfolio.Resilience/Abstractions/ICorrelationAccessor.cs` | Interface |
| `src/Portfolio.Resilience/Events/ResilienceEvent.cs` | Every event carries `CorrelationId` |
| `src/Portfolio.Resilience/Errors/ResilienceException.cs` | Every exception carries `CorrelationId` |

## Common mistakes

**Mistake 1 — passing correlation via parameter everywhere.**
That defeats the purpose. Correlation is ambient; let it be ambient. Read it via
`CorrelationContext.CurrentId`. Inject the accessor only in code you actually need to test
in isolation.

**Mistake 2 — reading CurrentId inside a fire-and-forget task.**
`AsyncLocal` does **not** flow into `Task.Run` if the task was started before the ambient
value was set. If you spawn background work, capture the ID first:

    var capturedId = CorrelationContext.CurrentId;
    _ = Task.Run(() =>
    {
        using (CorrelationContext.Push(capturedId ?? CorrelationContext.NewId()))
        {
            // Background work sees the correlation ID
        }
    });

**Mistake 3 — storing correlation ID in a scoped service field.**
Do not. Just call `CorrelationContext.CurrentId` where you need it. It is cheap (a
dictionary-free lookup on an `AsyncLocal`). Caching it invites bugs when the async flow
migrates.

**Mistake 4 — using the correlation ID as a security token.**
It is not. It is a trace identifier. Never authorize on it. Never trust the incoming value
as proof of identity. It is metadata, not authentication.

**Mistake 5 — generating a new ID mid-request.**
Only generate at the edge (middleware). Downstream code adopts the existing ID. If you
call `CorrelationContext.NewId()` in the middle of a request, you break the trace.

## Testing

The correlation subsystem is easy to test because `Push` returns a disposable:

    [Fact]
    public void Push_SetsAndRestoresValue()
    {
        Assert.Null(CorrelationContext.CurrentId);

        using (CorrelationContext.Push("abc"))
        {
            Assert.Equal("abc", CorrelationContext.CurrentId);
        }

        Assert.Null(CorrelationContext.CurrentId);
    }

Nested pushes restore in reverse order — see `CorrelationContext.cs` for the internal
`PopScope`.

## See also

- [logging.md](logging.md) — how correlation IDs land in log events
- [metrics.md](metrics.md) — how correlation IDs tag metric samples (future)
- [SPEC.md](../SPEC.md) §Correlation — the normative contract
- [../README.md](../README.md) — package install and quick-start

---

## Test coverage

Verified by `tests/Portfolio.Resilience.Tests/CorrelationContextTests.cs` (9 tests)
and `tests/Portfolio.Resilience.Tests/AsyncLocalCorrelationAccessorTests.cs` (5 tests):

- Push/pop semantics, including 3-level nesting
- Value flows across `await` and `Task.Run` boundaries
- Concurrent flows are fully isolated from each other
- `NewId()` returns 32-char lowercase hex, unique across 1000 calls
- Null/whitespace IDs rejected with `ArgumentException`
- DI adapter and primitive share the same underlying value (bidirectional)
