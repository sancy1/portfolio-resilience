<!--
filepath: docs/composition.md
package:  Portfolio.Resilience | since: v0.7.0
purpose:  Explains how to compose a custom resilience pipeline in any order.
-->

# Policy Composition

## What it is

Policy composition lets you build a resilience pipeline in **any order you
want**, from **any subset** of the available layers. Instead of the library's
fixed default:

    RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout -> Operation

you decide the shape:

    var pipeline = ResiliencePipeline.Wrap(retry, circuit, timeout);

Each layer implements `IResiliencePolicy`. The pipeline executes them
outermost-first — the first layer you add is the outermost.

## Why it exists

The library's default pipeline order is a sensible starting point for most
services. But real systems have specific needs:

- **Fallback outermost.** If the caller provided a fallback, it should run
  after every other layer has given up — not inside retry.
- **Hedging inside retry.** Hedged attempts should be retried as a group,
  not retried individually.
- **Skip the circuit.** A dependency that never fails systematically does not
  need circuit protection — and the circuit's state machine adds small overhead.
- **Per-call composition.** The same operation may need different pipeline
  shapes depending on context (production vs. background batch).

Hard-coded order blocks all of these. Composition removes that limit.

## When you need it

**Yes:**

- The default order is wrong for your dependency (e.g. fallback-first)
- You want to omit a layer entirely for a specific call site
- You are building a reusable library on top of `Portfolio.Resilience` and
  need to expose a custom pipeline shape
- You have a scenario where the strict default order is measurably slower or
  less correct

**No:**

- The default order works for you — use `AddPortfolioResilience` and the
  built-in pipeline. Composition is a power tool, not a requirement.

## How it works

### The two entry points

There are two ways to build a custom pipeline. **They are equally valid.**
Pick the one that reads better for your use case.

**1. `ResiliencePipeline.Wrap(...)` — direct, minimal**

    var pipeline = ResiliencePipeline.Wrap(
        rateLimiter,
        bulkhead,
        retry,
        circuit,
        timeout);

    var result = await pipeline.ExecuteAsync(
        "external-api",
        ct => _http.GetAsync("/data", ct),
        definition,
        ct);

Five layers, one line, in the order you want. **Use this when the shape is
static and simple.**

**2. `ResiliencePipelineBuilder` — fluent, expressive**

    var pipeline = new ResiliencePipelineBuilder()
        .WithName("external-api-pipeline")
        .Add(rateLimiter)
        .Add(bulkhead)
        .AddIf(env.IsProduction(), retry)
        .Add(circuit)
        .Add(timeout)
        .Build();

    var result = await pipeline.ExecuteAsync(
        "external-api",
        ct => _http.GetAsync("/data", ct),
        definition,
        ct);

The builder lets you add layers conditionally, name the pipeline for
diagnostics, and discover methods in your IDE. **Use this when the shape
depends on configuration.**

### Execution order

Layers are applied **outermost-first**, in the order they were added.

    Wrap(A, B, C)

produces:

    A -> B -> C -> operation

The first layer in the call is the outermost. It runs first, and it wraps
everything that follows.

### Interaction with `PolicyDefinition`

Each layer reads its own options from the shared `PolicyDefinition`. You
still enable/configure features the same way:

    var definition = new PolicyDefinition
    {
        Name = "external-api",
        RateLimiter = new RateLimiterOptions
        {
            Enabled = true,
            PermitLimit = 100,
            WindowSeconds = 60
        },
        Retry = new RetryOptions { MaxAttempts = 3 },
        Timeout = new TimeoutOptions { TimeoutMs = 5000 }
    };

The pipeline's `ExecuteAsync` takes the `PolicyDefinition` alongside the
operation. Each layer reads what it needs.

**A layer that is not in the pipeline is not executed — even if its options
are enabled.** This is deliberate: `ResiliencePipeline` does what it is told
and no more. To enforce "enabled implies present," use `CompositePolicyBuilder`
(the default pipeline), which throws `InvalidOperationException` when a policy
enables a feature whose builder was not wired.

## Quick start — side by side

Both examples below produce the same pipeline. Pick whichever you prefer.

### `Wrap(...)` style

    builder.Services.AddSingleton<ResiliencePipeline>(sp =>
        ResiliencePipeline.Wrap(
            sp.GetRequiredService<RateLimiterPolicyBuilder>(),
            sp.GetRequiredService<BulkheadPolicyBuilder>(),
            sp.GetRequiredService<RetryPolicyBuilder>(),
            sp.GetRequiredService<CircuitPolicyBuilder>(),
            sp.GetRequiredService<TimeoutPolicyBuilder>()));

### `Builder` style

    builder.Services.AddSingleton<ResiliencePipeline>(sp =>
        new ResiliencePipelineBuilder()
            .WithName("default-pipeline")
            .Add(sp.GetRequiredService<RateLimiterPolicyBuilder>())
            .Add(sp.GetRequiredService<BulkheadPolicyBuilder>())
            .Add(sp.GetRequiredService<RetryPolicyBuilder>())
            .Add(sp.GetRequiredService<CircuitPolicyBuilder>())
            .Add(sp.GetRequiredService<TimeoutPolicyBuilder>())
            .Build());

Both produce the same behavior. The builder adds a `Name` and lets you use
`AddIf(...)` for configuration-driven composition.

## Real-world patterns

### Pattern 1 — Fallback outermost

Fallback is not a layer in this library — it is a per-call argument to
`IResilienceExecutor.ExecuteAsync`. But with composition, you can build a
pipeline that stops retrying earlier and lets the caller's fallback run sooner:

    var pipeline = ResiliencePipeline.Wrap(retry, circuit);

    try
    {
        return await pipeline.ExecuteAsync(policyName, operation, definition, ct);
    }
    catch (ResilienceException)
    {
        return fallback(ct);
    }

More granular fallback handling becomes possible when you own the outer
control flow.

### Pattern 2 — No circuit for a stable dependency

The circuit adds small overhead (state lookup, lock, timestamp comparison).
For a dependency that never fails systematically — say, a local cache — you
can skip it:

    var pipeline = ResiliencePipeline.Wrap(retry, timeout);

Two layers. Cleaner. Slightly faster. Same resilience for the transient
failures the cache actually experiences.

### Pattern 3 — Circuit outermost for fail-fast

For a dependency that fails hard when it fails, put the circuit outermost so
a rejection is fast:

    var pipeline = ResiliencePipeline.Wrap(circuit, retry, timeout);

A call while the circuit is Open returns in microseconds — no retry, no
timeout, no resource use.

### Pattern 4 — Configuration-driven shape

Different environments need different pipelines. `AddIf(...)` handles this
without duplicating the chain:

    var pipeline = new ResiliencePipelineBuilder()
        .WithName("env-aware")
        .AddIf(env.IsProduction(), rateLimiter)
        .AddIf(env.IsProduction(), bulkhead)
        .Add(retry)
        .Add(circuit)
        .Add(timeout)
        .Build();

Development and test environments skip rate limiting and bulkheading; a
production deployment gets the full pipeline. One builder, no `if` around the
whole chain.

## Writing your own layer

Any type that implements `IResiliencePolicy` can be a layer:

    public sealed class MetricsLayer : IResiliencePolicy
    {
        private readonly IMetricSink _sink;

        public MetricsLayer(IMetricSink sink) => _sink = sink;

        public async Task<T> ExecuteAsync<T>(
            string policyName,
            Func<CancellationToken, Task<T>> operation,
            PolicyDefinition definition,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try { return await operation(ct); }
            finally { _sink.RecordCall(policyName, sw.Elapsed, true, 1); }
        }
    }

Compose it with anything else:

    var pipeline = ResiliencePipeline.Wrap(
        new MetricsLayer(sink),
        retry,
        circuit);

**Rules for a custom layer:**

- Must be thread-safe. The same instance may be invoked concurrently.
- Must honor the `ct` cancellation token.
- Must call `operation` (or not) exactly once. Calling it multiple times is
  what retry and hedging layers do — a generic layer should not.
- When the layer's feature is disabled in `definition`, the layer should
  pass through to `operation` without adding behavior.

## Configuration

There is no dedicated configuration section for composition. The pipeline
shape is decided **in code**, at registration time. The per-policy options
(retry attempts, rate limits, etc.) still come from `PolicyDefinition`.

**Binding from `appsettings.json` is not supported for pipeline shape.**
Composition is structural — a code decision, not a configuration one. If you
need environment-dependent shape, use `AddIf(env.IsProduction(), ...)` as in
Pattern 4.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Abstractions/IResiliencePolicy.cs` | The layer interface |
| `src/Portfolio.Resilience/Policies/ResiliencePipeline.cs` | The composition container |
| `src/Portfolio.Resilience/Policies/ResiliencePipelineBuilder.cs` | The fluent builder |
| `src/Portfolio.Resilience/Policies/CompositePolicyBuilder.cs` | The default pipeline (uses composition internally) |
| `src/Portfolio.Resilience/Policies/RetryPolicyBuilder.cs` | Implements `IResiliencePolicy` |
| `src/Portfolio.Resilience/Policies/CircuitPolicyBuilder.cs` | Implements `IResiliencePolicy` |
| `src/Portfolio.Resilience/Policies/TimeoutPolicyBuilder.cs` | Implements `IResiliencePolicy` |
| `src/Portfolio.Resilience/Policies/RateLimiterPolicyBuilder.cs` | Implements `IResiliencePolicy` |
| `src/Portfolio.Resilience/Policies/BulkheadPolicyBuilder.cs` | Implements `IResiliencePolicy` |

## Common mistakes

**Mistake 1 — expecting `Wrap` to enforce enabled-but-missing.**

`ResiliencePipeline` executes the layers it is given. It does not know what
the `PolicyDefinition` *wants* to be enabled. If you enable the rate limiter
in the definition but do not include the rate limiter in the pipeline, no
rate limiting happens.

**Use `CompositePolicyBuilder` if you want fail-loud behavior.** It validates
that everything enabled is present, then composes the default pipeline.

**Mistake 2 — assuming the layers run in parallel.**

They run sequentially, outermost-first. Each layer awaits the next. This is
not hedging — that is a specific parallel-execution feature (v0.7.0, separate
builder).

**Mistake 3 — sharing a builder across threads.**

`ResiliencePipelineBuilder` is mutable and not thread-safe. Create one,
configure it, call `Build()`, and share the resulting **pipeline** (which is
immutable and thread-safe). Never share the builder itself.

**Mistake 4 — reordering layers without testing.**

Layer order is significant. `Retry -> RateLimiter` and `RateLimiter -> Retry`
produce different behavior under load. See the test
`Wrap_RateLimiterRejectsBeforeRetryRuns` in `CompositionTests.cs` for a
concrete demonstration.

**Mistake 5 — building an empty pipeline.**

`Build()` throws `InvalidOperationException` when no layers have been added.
An empty pipeline would just call the operation directly — meaningless.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/ResiliencePipelineTests.cs`
(15 tests) and `tests/Portfolio.Resilience.Tests/CompositionTests.cs`
(9 tests):

- `Wrap` guards: null array, empty array, null element
- `Layers` property preserves order
- Execution order is outermost-first
- Pass-through: a single-layer pipeline calls the operation
- `Builder.Add`, `AddIf(true, ...)`, `AddIf(false, ...)`
- `Builder.WithName` stores the name
- `Builder.Build` throws on empty
- **`Wrap_RateLimiterRejectsBeforeRetryRuns`** — a rejected call is not retried
- **`Wrap_RetryOuterThanRateLimiter_RetryConsumesMultiplePermits`** — retry
  attempts each consume a permit when rate limiter is innermost
- Full 5-layer pipeline executes correctly end-to-end
- `CompositePolicyBuilder` still throws when a feature is enabled but missing

## See also

- [executor.md](executor.md) — the default pipeline entry point
- [retry.md](retry.md) — the retry layer
- [circuit-breaker.md](circuit-breaker.md) — the circuit layer
- [timeout.md](timeout.md) — the timeout layer
- [rate-limiter.md](rate-limiter.md) — the rate limiter layer
- [bulkhead.md](bulkhead.md) — the bulkhead layer
- [../SPEC.md](../SPEC.md) section 14 — the normative contract
