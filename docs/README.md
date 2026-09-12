<!--
filepath: docs/README.md
package:  Portfolio.Resilience | since: v0.2.0
purpose:  Index of all documentation for the resilience package.
-->

# Portfolio.Resilience — Documentation

This folder holds the documentation for the resilience library. Each file covers one concern
(retry, circuit breaker, logging, etc.), explains **what it does**, **why it exists**,
**when to use it**, and **which files implement it**.

The docs are language-neutral. When the Python implementation arrives (for the AI service),
it will reference the same documents. Only the filepaths in the "Associated files" sections
will differ.

---

## Reading order for a new developer

1. **[correlation.md](correlation.md)** — start here. Everything else depends on the ability
   to trace a single request across services.
2. **[logging.md](logging.md)** — how we observe behavior.
3. **[metrics.md](metrics.md)** — how we measure latency and error rate.
4. **[error-classification.md](error-classification.md)** — how failures are categorized.
5. **[retry.md](retry.md)** — how transient failures are recovered.
6. **[timeout.md](timeout.md)** — how slow operations are bounded.
7. **[circuit-breaker.md](circuit-breaker.md)** — how we stop hammering a failing dependency.
8. **[fallback.md](fallback.md)** — how we degrade gracefully.
9. **[executor.md](executor.md)** — the main entry point that wires all of the above.
10. **[http-integration.md](http-integration.md)** — how HttpClient gets resilience for free.
11. **[recipes.md](recipes.md)** — how to add a new policy, wire a new service, migrate to cloud logging.

---

## Documentation index

| Doc | Covers | Status |
|-----|--------|--------|
| [correlation.md](correlation.md) | Ambient correlation IDs, AsyncLocal, HTTP header propagation | ✅ Written |
| [logging.md](logging.md) | Structured events, sinks, migration from console → cloud | ✅ Written |
| [metrics.md](metrics.md) | Latency percentiles, error rates, the `/health/resilience` endpoint | ✅ Written |
| [error-classification.md](error-classification.md) | Transient vs permanent vs circuit-open vs timeout categories | ⏳ Stage D |
| [retry.md](retry.md) | Exponential backoff formula, jitter, per-policy tuning | ⏳ Stage D |
| [timeout.md](timeout.md) | Per-operation ceilings, cancellation semantics | ⏳ Stage D |
| [circuit-breaker.md](circuit-breaker.md) | State machine (Closed/Open/HalfOpen), thresholds, probes | ⏳ Stage E |
| [fallback.md](fallback.md) | Static vs dynamic fallbacks, resolution order | ⏳ Stage E |
| [executor.md](executor.md) | The `IResilienceExecutor` entry point, call-site patterns | ⏳ Stage F |
| [http-integration.md](http-integration.md) | DelegatingHandler, named clients, per-service policies | ⏳ Stage G |
| [recipes.md](recipes.md) | Copy-paste examples for common tasks | ⏳ Stage H |

Docs marked **⏳ Stage X** will be written when that stage lands. The code and the doc ship together — never ahead, never behind.

---

## Relationship to `SPEC.md`

`SPEC.md` (at `../SPEC.md`) is the **normative contract** — the canonical
definition of event types, metric names, retry formulas, and the circuit state machine.
These docs are the **explanatory companion**: they say the same things in prose, with
examples and design rationale.

If a doc and the spec disagree, **the spec wins**. File an issue or open a PR against `SPEC.md`.

---

## How to use these docs

- **Building a new service that consumes the library?**
  Read `correlation.md`, `logging.md`, `metrics.md` first. Those three tell you how your
  service will be observed once it starts using the library.

- **Wiring a call to a new external dependency?**
  Read `executor.md`, then `recipes.md`. There's a checklist for adding a new policy.

- **Debugging a "why did this call fail?" question?**
  Read `error-classification.md`, then `retry.md`. The failure category tells you whether
  a retry helped, and the retry doc tells you how many attempts were made.

- **Migrating logs from console to Datadog / OTel / Application Insights?**
  Read `logging.md` §"Swapping sinks". It's a one-line change in `Program.cs`.

- **Wondering why a circuit opened?**
  Read `circuit-breaker.md`. The state machine and thresholds are explained, along with
  which events are emitted when.

---

## How to contribute

- **New feature?** Write the doc first, then the code. The doc forces clarity.
- **Bug fix?** Update the doc if the behavior changed. Docs and code move together.
- **Unclear doc?** Open an issue. Confusing documentation is a bug.
- **New language implementation?** Copy the doc structure, keep the content. Only the
  "Associated files" section changes.

---

## See also

- `SPEC.md` — the normative contract (language-agnostic)
- `README.md` (package root) — install and quick-start
- `CHANGELOG.md` (package root) — version history
- `VERSION` (package root) — current version

---

## Current test suite

**67 tests, 0 failures.** All green as of Stage C completion.

| Test file | Tests | Covers |
|-----------|-------|--------|
| `CorrelationContextTests.cs` | 9 | Ambient correlation primitive |
| `AsyncLocalCorrelationAccessorTests.cs` | 5 | DI adapter for correlation |
| `NullLogSinkTests.cs` | 4 | No-op logging |
| `ConsoleLogSinkTests.cs` | 7 | JSON Lines to stdout |
| `CompositeLogSinkTests.cs` | 7 | Log sink fan-out |
| `FileLogSinkTests.cs` | 9 | JSON Lines to daily file |
| `InMemoryMetricSinkTests.cs` | 19 | Latency, percentiles, error rate |
| `CompositeMetricSinkTests.cs` | 7 | Metric sink fan-out |

Run them with:

    cd dotnet
    dotnet test Portfolio.Resilience.slnx
