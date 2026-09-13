<!--
filepath: docs/README.md
package:  Portfolio.Resilience | since: v0.6.0
purpose:  Index of all documentation for the resilience package.
-->

# Portfolio.Resilience — Documentation

This folder holds the documentation for the resilience library. Each file covers
one concern (retry, circuit breaker, logging, etc.), explains **what it does**,
**why it exists**, **when to use it**, and **which files implement it**.

The docs are language-neutral. When a Python or Go implementation arrives, it
references the same documents. Only the filepaths in the "Associated files"
sections will differ.

---

## Reading order for a new developer

1. **[correlation.md](correlation.md)** — start here. Everything else depends on
   the ability to trace a single request across services.
2. **[logging.md](logging.md)** — how we observe behavior.
3. **[metrics.md](metrics.md)** — how we measure latency and error rate.
4. **[error-classification.md](error-classification.md)** — how failures are
   categorized.
5. **[retry.md](retry.md)** — how transient failures are recovered.
6. **[timeout.md](timeout.md)** — how slow operations are bounded.
7. **[circuit-breaker.md](circuit-breaker.md)** — how we stop hammering a
   failing dependency.
8. **[rate-limiter.md](rate-limiter.md)** — how we cap call rate.
9. **[bulkhead.md](bulkhead.md)** — how we cap concurrency.
10. **[executor.md](executor.md)** — the main entry point that wires all of the
    above.
11. **[http-integration.md](http-integration.md)** — how HttpClient gets
    resilience for free.
12. **[api-stability.md](api-stability.md)** — the frozen public API surface.

---

## Documentation index

| Doc | Covers |
|-----|--------|
| [correlation.md](correlation.md) | Ambient correlation IDs, AsyncLocal, HTTP header propagation |
| [logging.md](logging.md) | Structured events, sinks, migration from console → cloud |
| [metrics.md](metrics.md) | Latency percentiles, error rates, the `/health/resilience` endpoint |
| [error-classification.md](error-classification.md) | Transient / permanent / circuit-open / timeout categories |
| [retry.md](retry.md) | Exponential backoff formula, jitter, per-policy tuning |
| [timeout.md](timeout.md) | Per-operation ceilings, cancellation semantics |
| [circuit-breaker.md](circuit-breaker.md) | State machine (Closed / Open / HalfOpen), thresholds, probes |
| [rate-limiter.md](rate-limiter.md) | Four strategies, queue behavior, rejection metadata |
| [bulkhead.md](bulkhead.md) | Concurrency cap, waiter queue, slot release semantics |
| [executor.md](executor.md) | The `IResilienceExecutor` entry point, call-site patterns |
| [http-integration.md](http-integration.md) | DelegatingHandler, named clients, per-service policies |
| [api-stability.md](api-stability.md) | The frozen public API surface and versioning policy |

Every doc listed above is **written and current as of v0.6.0**. Code and doc
ship together — a doc never describes a feature the library does not have, and
the library never has a feature without a doc.

---

## Relationship to `SPEC.md`

`SPEC.md` (at `../SPEC.md`) is the **normative contract** — the canonical
definition of event types, metric names, retry formulas, the circuit state
machine, the rate limiter algorithms, and the bulkhead semantics. These docs
are the **explanatory companion**: they say the same things in prose, with
examples and design rationale.

If a doc and the spec disagree, **the spec wins**. File an issue or open a PR
against `SPEC.md`.

---

## How to use these docs

- **Building a new service that consumes the library?**
  Read `correlation.md`, `logging.md`, `metrics.md` first. Those three tell you
  how your service will be observed once it starts using the library.

- **Wiring a call to a new external dependency?**
  Read `executor.md`, then `retry.md` and `circuit-breaker.md`. Add
  `rate-limiter.md` and `bulkhead.md` if the dependency has capacity limits.

- **Debugging a "why did this call fail?" question?**
  Read `error-classification.md`, then `retry.md`. The failure category tells
  you whether a retry helped, and the retry doc tells you how many attempts
  were made.

- **Getting 429s from an external API?**
  Read `rate-limiter.md`. The limiter enforces your declared ceiling locally,
  before requests leave the process.

- **Seeing thread pool exhaustion on a slow dependency?**
  Read `bulkhead.md`. The bulkhead caps concurrent calls and bounds the queue.

- **Migrating logs from console to Datadog / OTel / Application Insights?**
  Read `logging.md` § "Logging scenarios". It is a one-line change in
  `Program.cs`.

- **Wondering why a circuit opened?**
  Read `circuit-breaker.md`. The state machine and thresholds are explained,
  along with which events are emitted when.

---

## How to contribute

- **New feature?** Write the doc first, then the code. The doc forces clarity.
- **Bug fix?** Update the doc if the behavior changed. Docs and code move
  together.
- **Unclear doc?** Open an issue. Confusing documentation is a bug.
- **New language implementation?** Copy the doc structure, keep the content.
  Only the "Associated files" section changes.

---

## See also

- `SPEC.md` — the normative contract (language-agnostic)
- `README.md` (package root) — install and quick-start
- `CHANGELOG.md` (package root) — version history
- `VERSION` (package root) — current version

---

## Current test suite

**273 tests, 0 failures, 0 warnings.**

Run them with:

    cd dotnet
    dotnet test Portfolio.Resilience.slnx

Coverage spans every sink, the correlation primitive, the error classifier,
each policy builder (retry, timeout, circuit, rate limiter, bulkhead), the
composite pipeline, the registry, the executor, and the HTTP handler.
