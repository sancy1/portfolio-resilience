<!--
filepath: docs/opentelemetry.md
package:  Portfolio.Resilience.OpenTelemetry | since: v0.7.0
purpose:  Explains the optional OpenTelemetry integration - event and metric export.
-->

# OpenTelemetry Integration

## What it is

An **optional companion package** that exports everything `Portfolio.Resilience`
emits as native OpenTelemetry telemetry:

- **Events** become OTel **logs** via `OpenTelemetryLogSink`.
- **Metrics** become OTel **histograms and counters** via `OpenTelemetryMetricSink`.

The core `Portfolio.Resilience` package stays **zero-dependency**. The
OpenTelemetry integration lives in a separate NuGet package,
`Portfolio.Resilience.OpenTelemetry`, which you install only if you want it.

## Why it exists

`Portfolio.Resilience` already ships with rich local observability: structured
JSON events via `ILogSink`, and latency metrics via `IMetricSink`. For services
running in production today, that is often enough.

But most teams also want their resilience activity to flow into the same
dashboards as everything else. If your stack is OpenTelemetry-based — Grafana,
Datadog, Jaeger, Honeycomb, Application Insights via OTLP — you want
`rate_limited` and `circuit_opened` events to appear next to your HTTP spans.

Without this package, you would write your own `ILogSink` and `IMetricSink`
(about 40 lines of code, twice). With this package, it is one line in
`Program.cs`.

## When you need it

**Yes:**

- You already run an OpenTelemetry collector (OTLP endpoint) or use a vendor
  that ingests OTLP.
- You want resilience events and metrics to appear in the same backend as your
  traces.
- You want per-policy latency histograms in Grafana without standing up a
  custom exporter.

**No:**

- You only need local logging (console/file) — use the built-in sinks.
- You use a logging backend that does not speak OTLP — write a custom sink or
  use the vendor's `ILogger` integration.

## Install

    dotnet add package Portfolio.Resilience
    dotnet add package Portfolio.Resilience.OpenTelemetry

The OTel package brings in only `OpenTelemetry.Api` (the minimal API) — not the
SDK, not exporters. You supply those.

## Quick start

### ASP.NET Core — the one-liner

    using Portfolio.Resilience.Extensions;
    using Portfolio.Resilience.OpenTelemetry;

    var builder = WebApplication.CreateBuilder(args);

    // Standard OTel setup
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddOtlpExporter())
        .WithMetrics(m => m.AddOtlpExporter())
        .WithLogging(l => l.AddOtlpExporter());

    // Resilience
    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("external-api", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = 100;
            p.RateLimiter.WindowSeconds = 60;
        }));

    builder.Services.AddPortfolioResilienceOpenTelemetry();

Call order **does not matter** — the composition is resolved lazily.

### Advanced — from inside the builder

If you already have a `Meter` and an `ILoggerFactory`, you can bypass the DI
composition:

    builder.Services.AddPortfolioResilience(r => r
        .AddOpenTelemetrySinks(
            loggerFactory: myLoggerFactory,
            meter: myMeter)
        .AddPolicy("...", p => { /* ... */ }));

## What gets exported

### Events → OTel Logs

Every `ResilienceEvent` becomes one OTel log record with these attributes:

| Attribute | Source | Notes |
|-----------|--------|-------|
| `resilience.event_type` | `EventType` | snake_case: `call_started`, `rate_limited`, etc. |
| `resilience.policy_name` | `PolicyName` | |
| `resilience.timestamp_utc` | `TimestampUtc` | Always present |
| `resilience.correlation_id` | `CorrelationId` | Present when set |
| `resilience.attempt` | `Attempt` | Present when set |
| `resilience.duration_ms` | `DurationMs` | Present when set |
| `resilience.error_category` | `ErrorCategory` | Present when set |
| `resilience.error_message` | `ErrorMessage` | Present when set |
| `resilience.error_type` | `ErrorType` | Present when set |
| `resilience.metadata.<key>` | `Metadata[k]` | One attribute per metadata key |

**Log level mapping:**

| Event type | Log level |
|------------|-----------|
| `call_started` | `Debug` |
| `call_succeeded`, `circuit_closed`, `circuit_half_opened`, `fallback_used` | `Information` |
| `retry_attempted`, `circuit_opened`, `timeout_breached`, `rate_limited`, `bulkhead_rejected` | `Warning` |
| `call_failed` | `Error` |

**A note on filtering.** The sink does not filter on `ILogger.IsEnabled` — it
emits everything it receives. Whether a record reaches your OTLP exporter is
decided by the logger pipeline's minimum level. If you set your logger to
`Information`, `call_started` (Debug) records are dropped **before** reaching
the sink's provider — this is standard `ILogger` filtering behavior.

### Metrics → OTel Instruments

Every `IMetricSink.RecordCall(...)` becomes three instrument recordings:

| Instrument | Type | Tags | Meaning |
|------------|------|------|---------|
| `resilience.call.duration_ms` | Histogram (double) | `policy_name`, `success` | One record per call |
| `resilience.call.succeeded_total` | Counter (long) | `policy_name`, `attempts` | Incremented on success |
| `resilience.call.failed_total` | Counter (long) | `policy_name`, `attempts` | Incremented on failure |

**In-flight tracking is not exported.** The `IMetricSink` interface only
exposes `RecordCall`. The in-flight gauge (`InMemoryMetricSink.BeginInFlight`
/ `EndInFlight`) is not part of the interface, so the OTel sink cannot see it.
If you need in-flight as an OTel gauge, register `InMemoryMetricSink` alongside
`OpenTelemetryMetricSink` — both run.

## Configuration

### Meter name

The metric sink uses a `Meter`. The default name is `Portfolio.Resilience`.
Override it:

    builder.Services.AddPortfolioResilienceOpenTelemetry(meterName: "MyService.Resilience");

### Logger category

The log sink uses the logger category `Portfolio.Resilience`. This is fixed —
change it in a future version if needed, but for now filter your logs by
category if you want to route them separately.

### Custom Meter

If you want to share a `Meter` across multiple sinks or manage its lifetime
explicitly, use the `ResilienceBuilder` extension:

    var meter = new Meter("MyService.Resilience");

    builder.Services.AddPortfolioResilience(r => r
        .AddOpenTelemetrySinks(
            loggerFactory: LoggerFactory.Create(b => b.AddConsole()),
            meter: meter)
        .AddPolicy("...", p => { /* ... */ }));

The `Meter` must be disposed by the caller on shutdown.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryLogSink.cs` | Event → OTel log |
| `src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryMetricSink.cs` | Metric → OTel instruments |
| `src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryBuilderExtensions.cs` | DI registration |
| `tests/Portfolio.Resilience.OpenTelemetry.Tests/OpenTelemetryLogSinkTests.cs` | 29 tests |
| `tests/Portfolio.Resilience.OpenTelemetry.Tests/OpenTelemetryMetricSinkTests.cs` | 8 tests |

## Common mistakes

**Mistake 1 — expecting the OTel sinks to replace the built-in ones.**

The OTel sinks **compose with** the built-in ones. The `ILogSink` registration
becomes a `CompositeLogSink` containing the original sink (console, file, null)
**plus** the OTel sink. Both run. If you want only OTel, do not add any other
`AddLogSink(...)` calls.

**Mistake 2 — forgetting to configure the OTel pipeline.**

`AddPortfolioResilienceOpenTelemetry()` registers the sinks but does not
configure the OTel exporter. You still need to call
`.WithTracing().WithMetrics().WithLogging()` and the exporter of your choice
via `AddOpenTelemetry()`.

**Mistake 3 — filtering out `Debug`.**

If your minimum log level is `Information` (common default), the
`call_started` event is filtered before reaching the sink. This is standard
`ILogger` behavior — not a bug. To see every event, set
`SetMinimumLevel(LogLevel.Debug)`.

**Mistake 4 — disposing the `Meter` too early.**

If you pass a `Meter` to the builder extension, you own its lifetime. Dispose
it on shutdown. A disposed `Meter` will throw on the next `RecordCall`.

**Mistake 5 — treating `resilience.metadata.*` attributes as a fixed schema.**

The metadata keys depend on the event type. `rate_limited` events carry
`strategy`, `permit_limit`, `queue_limit`, `queue_depth`, and `reason`.
`circuit_opened` events carry `consecutive_failures` and `opened_at_utc`. Query
per event type, not per key.

## Testing

Verified by:
- `tests/Portfolio.Resilience.OpenTelemetry.Tests/OpenTelemetryLogSinkTests.cs` — 29 tests
- `tests/Portfolio.Resilience.OpenTelemetry.Tests/OpenTelemetryMetricSinkTests.cs` — 8 tests

Coverage:
- Constructor guard (null logger factory / null meter)
- Event field mapping (common, error, metadata, absent optional)
- Log level mapping (all 11 event types)
- Event type to snake_case name (all 11 event types)
- Metric recording: duration, success counter, failure counter
- Multiple recordings
- Distinct policies tagged separately
- Parameterless metric sink constructor

## See also

- [logging.md](logging.md) — the base logging contract
- [metrics.md](metrics.md) — the base metrics contract
- [executor.md](executor.md) — where events and metrics originate
- [../SPEC.md](../SPEC.md) section 15 — the normative contract
