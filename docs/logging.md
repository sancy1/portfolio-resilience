<!--
filepath: docs/logging.md
package:  Portfolio.Resilience | since: v0.8.0
purpose:  Explains structured resilience events, sinks, and how to migrate to cloud logging.
-->

# Logging

## What it is

Structured, event-based logging for every resilience decision the library makes. When a retry
happens, when a circuit opens, when a fallback is used - each of those moments produces a
`ResilienceEvent` object. **Sinks** receive those events and decide where they go.

This is not `Console.WriteLine`. Every event is a first-class object with typed fields
(`event_type`, `policy_name`, `correlation_id`, `duration_ms`, and so on).

## Why it exists

Two reasons.

**One: consistency across services.** When landing-page-service, auth-service, and the future
Python AI service all emit the same event shape, you can query across them in one dashboard.
A circuit-opened event in .NET and a circuit-opened event in Python look identical to your
log aggregator.

**Two: destination independence.** The library does not know whether your logs go to console,
a file, Datadog, Application Insights, OpenTelemetry, or all of them at once. That decision
belongs to the consuming service. Swapping destinations is a config change, not a code change.

## When you need it

**Always.** Even in development, the console sink gives immediate visibility into retries
and circuit behavior. In production, every event matters - it is how you debug "why did the
checkout fail at 3:15pm".

If you are writing a new service and you have not configured any sink, the library falls back
to `NullLogSink` (silent). That is deliberate: it is safer to emit nothing than to crash
because logging was misconfigured.

## How it works

Two abstractions and their implementations.

### The event

Location: `src/Portfolio.Resilience/Events/ResilienceEvent.cs`

A `record` with these fields:

- `EventType` - one of `CallStarted`, `RetryAttempted`, `CallSucceeded`,
  `CallFailed`, `CircuitOpened`, `CircuitClosed`, `CircuitHalfOpened`,
  `FallbackUsed`, `TimeoutBreached`, `RateLimited`, `BulkheadRejected`,
  `HedgeWon`, `HedgeLost`, `HedgeCancelled`
- `PolicyName` - which policy produced the event
- `CorrelationId` - ties the event to the originating request
- `TimestampUtc` - when it happened (UTC)
- `Attempt` - retry attempt number, when applicable
- `DurationMs` - how long the call took
- `ErrorCategory` - Transient / Permanent / CircuitOpen / Timeout / FallbackUsed / Unknown
- `ErrorMessage`, `ErrorType` - populated on failures
- `Metadata` - an open dictionary for policy-specific context

### The sink abstraction

Location: `src/Portfolio.Resilience/Abstractions/ILogSink.cs`

    public interface ILogSink
    {
        void Emit(ResilienceEvent evt);
    }

One method. Any destination is one class away.

### Shipped sinks

**ConsoleLogSink** - writes compact JSON to `Console.Out`, one line per event.
Best for development and for container environments where stdout is collected.

**FileLogSink** - writes the same JSON to a daily rotating file
(`resilience-YYYY-MM-DD.jsonl`). Useful when stdout is ephemeral or when you want a local
trail independent of cloud infrastructure. Implements `IDisposable` to release the file handle.

**NullLogSink** - discards everything. The safe default.

**CompositeLogSink** - fans out to multiple sinks. If one sink throws, the others still
receive the event, so a misbehaving cloud sink cannot take down your console logging.
Also applies an optional `IEventScrubber` before fan-out - see the scrubbing section below.

## Swapping sinks - from console to cloud in one line

This is the feature that makes the library worth its weight. Consider a service that
currently logs to console:

    builder.Services.AddSingleton<ILogSink, ConsoleLogSink>();

Now you want Datadog. Add a `DatadogSink` class (a new file, ~40 lines) that implements
`ILogSink` and calls the Datadog client. Then compose:

    builder.Services.AddSingleton<ILogSink>(sp => new CompositeLogSink(new ILogSink[]
    {
        new ConsoleLogSink(),
        new DatadogSink(apiKey: config["DATADOG_API_KEY"])
    }));

**No call sites changed.** No `ResilienceExecutor` code changed. The library kept its side
of the contract - you swapped the destination.

## Where events are emitted from

Every `IResilienceExecutor.ExecuteAsync(...)` call produces events automatically.
The pipeline's layers emit at their decision points: `CallStarted` at the top,
`RetryAttempted` before each retry delay, `CircuitOpened` / `CircuitClosed` /
`CircuitHalfOpened` on state transitions, `RateLimited` and `BulkheadRejected`
on rejections, `HedgeWon` / `HedgeLost` / `HedgeCancelled` on the race,
`TimeoutBreached` when the ceiling fires, `FallbackUsed` when a fallback
produces a value, and `CallSucceeded` / `CallFailed` at the end.

You do not call `ILogSink.Emit` yourself. Logging is **part of the pipeline**,
not a side task.

## Configuration

`LoggingOptions` (`Configuration/LoggingOptions.cs`) exposes one boolean toggle
per event family. All default to `true` except `EmitCallStarted` (verbose) and
`ScrubSensitiveData` (opt-in).

- `EmitCallStarted` - off by default. Each successful call would otherwise
  produce a started + succeeded pair, doubling log volume. Turn on only when
  debugging.
- `EmitRetryAttempted`, `EmitCallSucceeded`, `EmitCallFailed` - default on.
- `EmitCircuitEvents` - default on. Covers `CircuitOpened`, `CircuitClosed`,
  `CircuitHalfOpened`.
- `EmitFallbackUsed`, `EmitTimeoutBreached` - default on.
- `EmitRateLimited`, `EmitBulkheadRejected` - default on.
- `EmitHedgeEvents` - default on. Covers `HedgeWon`, `HedgeLost`,
  `HedgeCancelled`.
- `ScrubSensitiveData` - default off. See [pci-scrubbing.md](pci-scrubbing.md).

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Abstractions/ILogSink.cs` | Sink contract |
| `src/Portfolio.Resilience/Abstractions/IEventScrubber.cs` | Scrubber contract |
| `src/Portfolio.Resilience/Events/ResilienceEvent.cs` | Event data model |
| `src/Portfolio.Resilience/Events/ResilienceEventType.cs` | Event type enum |
| `src/Portfolio.Resilience/Errors/ResilienceErrorCategory.cs` | Error category enum |
| `src/Portfolio.Resilience/Sinks/ConsoleLogSink.cs` | JSON to stdout |
| `src/Portfolio.Resilience/Sinks/FileLogSink.cs` | JSON Lines to daily file |
| `src/Portfolio.Resilience/Sinks/NullLogSink.cs` | No-op |
| `src/Portfolio.Resilience/Sinks/CompositeLogSink.cs` | Fan-out + scrubber |
| `src/Portfolio.Resilience/Sinks/DefaultPciScrubber.cs` | Default scrubber |
| `src/Portfolio.Resilience/Configuration/LoggingOptions.cs` | Per-event toggles |

## JSON field naming

Events serialize with **snake_case** field names - `event_type`, `policy_name`,
`correlation_id` - regardless of the C# property name (`EventType`, `PolicyName`,
`CorrelationId`). This is the cross-language contract in `SPEC.md`: the Python implementation
will produce the same field names, so log aggregators and dashboards work uniformly.

## Sensitive-data scrubbing (v0.8.0)

Every event can optionally run through an `IEventScrubber` before any sink
sees it. This masks PAN, CVV, and SSN patterns in `ErrorMessage`, `ErrorType`,
and string values in `Metadata`. Off by default.

Enable per policy:

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("stripe-charge", p =>
        {
            p.Logging.ScrubSensitiveData = true;
        }));

Once enabled on any policy, the scrubber runs globally - every event is
scrubbed, regardless of which policy emitted it. See
[pci-scrubbing.md](pci-scrubbing.md) for the full contract, the default
patterns, and how to plug in a custom scrubber.

## Common mistakes

**Mistake 1 - writing directly to `Console` instead of using a sink.**
Any output that is not a `ResilienceEvent` breaks the schema. If you want custom data, put
it in `ResilienceEvent.Metadata`.

**Mistake 2 - throwing inside a custom sink.**
A sink must never throw. If a sink fails, catch the exception internally and log to a
fallback channel, or silently drop. `CompositeLogSink` guards against this, but a single
sink registered directly will propagate its exception to the caller.

**Mistake 3 - logging PII in `Metadata`.**
Event metadata flows to every sink. If a sink forwards to a cloud service, that metadata
goes with it. Do not put emails, tokens, or payload bodies in metadata. If you must
log data that might contain sensitive patterns, enable
`ScrubSensitiveData` on the policy - see
[pci-scrubbing.md](pci-scrubbing.md).

**Mistake 4 - keeping a `FileLogSink` alive past process shutdown.**
It is `IDisposable`. If you register it as a singleton, the DI container disposes it on
shutdown. If you construct one manually, put it in a `using` block.

**Mistake 5 - expecting logs to be ordered across sinks.**
`CompositeLogSink` forwards synchronously in array order, but a real Datadog or OTel sink
will batch and may reorder. Order is guaranteed per sink, not across sinks.

## Testing sinks

`NullLogSink` is the sink of choice for tests that do not care about logging.

For tests that DO care, capture events with a small in-memory sink:

    public sealed class CapturingSink : ILogSink
    {
        public List<ResilienceEvent> Events { get; } = new();
        public void Emit(ResilienceEvent evt) => Events.Add(evt);
    }

Then assert on `sink.Events`. No mocking framework required.

## See also

- [correlation.md](correlation.md) - how `CorrelationId` is set and propagated
- [pci-scrubbing.md](pci-scrubbing.md) - masking sensitive data before it reaches a sink
- [metrics.md](metrics.md) - the sibling concern to logging
- [../SPEC.md](../SPEC.md) - the normative event schema (section 7)
- [../README.md](../README.md) - package install and quick-start

---

## Test coverage

Five test files cover the logging subsystem:

- `ConsoleLogSinkTests.cs` - valid JSON, snake_case field names,
  special-character escaping, null handling, thread safety under 200 parallel writes
- `FileLogSinkTests.cs` - directory creation, daily-rotated filename,
  appending across sink instances, thread safety, file-handle release on `Dispose`
- `CompositeLogSinkTests.cs` - fan-out to all children, exception isolation
  (one broken sink cannot stop the others), null filtering, empty-sink tolerance
- `CompositeLogSinkScrubberTests.cs` - scrubber runs once before fan-out,
  single-sink wrapping when `ScrubSensitiveData = true`, DI wiring
- `NullLogSinkTests.cs` - singleton identity, silent null acceptance,
  10,000-call no-side-effect loop