<!--
filepath: dotnet/samples/Samples.App/QUICKSTART.md
package:  Samples.App | since: n/a
purpose:  Copy-paste guide for adopting Portfolio.Resilience in a real project, with exact call shapes and known traps
-->

# Quickstart - using Portfolio.Resilience in a real project

This guide is derived from the sample in this folder. Every code shape in it has been run end-to-end against the published `Portfolio.Resilience` package. Copy from here when you adopt the library.

If you want to see the shapes in action, run the sample first:

    dotnet run --project dotnet\samples\Samples.App\Samples.App.csproj

Ten scenarios print. Every API call you will copy is in one of the ten files under `Scenarios\`.

## 1. Install

Install the core package (required):

    dotnet add package Portfolio.Resilience

Pin a specific version if needed:

    dotnet add package Portfolio.Resilience --version 0.8.0

Optional packages:

    # OpenTelemetry export - every event becomes an OTel log, every call becomes a histogram
    dotnet add package Portfolio.Resilience.OpenTelemetry

    # Roslyn analyzers - PR0001 and PR0002 compile-time warnings
    dotnet add package Portfolio.Resilience.Analyzers

Published packages and version history:

- Portfolio.Resilience: https://www.nuget.org/packages/Portfolio.Resilience
- Version history: https://www.nuget.org/packages/Portfolio.Resilience#versions-tab
- OpenTelemetry export: https://www.nuget.org/packages/Portfolio.Resilience.OpenTelemetry#versions-tab
- Analyzers: https://www.nuget.org/packages/Portfolio.Resilience.Analyzers#versions-tab

The core package has zero third-party dependencies. It requires .NET 10 or later.

## 2. Register the library - the only required step

In `Program.cs`, on the `IServiceCollection`:

    using Portfolio.Resilience.Extensions;
    using Portfolio.Resilience.Sinks;

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())
        .AddPolicy("auth-service", p =>
        {
            p.Retry.MaxAttempts = 3;
            p.Retry.BaseDelayMs = 100;
            p.Circuit.FailureThreshold = 5;
            p.Circuit.OpenDurationSeconds = 30;
            p.Timeout.TimeoutMs = 5000;
        }));

That is the entire registration. Every registered policy is resolvable by name from the injected executor.

## 3. Resolve and use the executor

    using Portfolio.Resilience.Abstractions;

    public sealed class UserService
    {
        private readonly IResilienceExecutor _resilience;

        public UserService(IResilienceExecutor resilience) => _resilience = resilience;

        public Task<UserDto?> GetUserAsync(Guid id, CancellationToken ct) =>
            _resilience.ExecuteAsync<UserDto?>(
                policyName: "auth-service",
                operation: async token =>
                {
                    // your real operation - HTTP call, DB query, cache read
                    return await _http.GetFromJsonAsync<UserDto>($"/users/{id}", token);
                },
                fallback: token => Task.FromResult<UserDto?>(UserDto.Anonymous),
                idempotencyKey: null,
                timeBudgetMs: null,
                ct: ct);
    }

That is the complete call shape. Everything else is optional.

## 4. The exact ExecuteAsync signature - memorize this

    Task<T> ExecuteAsync<T>(
        string policyName,
        Func<CancellationToken, Task<T>> operation,
        Func<CancellationToken, Task<T>>? fallback = null,
        string? idempotencyKey = null,
        int? timeBudgetMs = null,
        CancellationToken ct = default);

Six parameters. Every one after `operation` is optional. **Always use named arguments.** Positional arguments are the single most common source of bugs - a `null` in the wrong slot silently becomes a fallback, an idempotency key, or a budget. Write:

    await _resilience.ExecuteAsync<int>(
        policyName: "auth-service",
        operation: async ct => await _client.FetchAsync(ct),
        fallback: null,
        idempotencyKey: null,
        timeBudgetMs: null,
        ct: ct);

Not:

    await _resilience.ExecuteAsync<int>("auth-service", async ct => await _client.FetchAsync(ct));

Both compile. Only the first is safe to edit later.

## 5. The non-generic overload

When the operation returns nothing:

    Task ExecuteAsync(
        string policyName,
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task>? fallback = null,
        string? idempotencyKey = null,
        int? timeBudgetMs = null,
        CancellationToken ct = default);
## 6. Layer order - the two valid orders

The library composes six layers. Two orders ship: the default and the payment-safe preset. The only difference is where the circuit sits.

Default pipeline:

    RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout -> Operation

Payment-safe pipeline (ResiliencePipeline.WithPaymentSafeDefaults()):

    RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation

The circuit moves outside Retry and Hedging so a fail-fast rejection never spawns another attempt. Use the payment-safe order on any write path where a duplicate attempt would be harmful (charges, orders, refunds).

## 7. Per-layer options - the exact property names

Set these inside AddPolicy("name", p => { ... }):

    // Retry
    p.Retry.MaxAttempts  = 3;       // total attempts including the first
    p.Retry.BaseDelayMs  = 100;     // first retry delay; subsequent delays multiply
    p.Retry.MaxDelayMs   = 5000;    // cap on the exponential backoff
    p.Retry.JitterRatio  = 0.2;     // 0.0 to 1.0 - adds randomness to delay

    // Circuit
    p.Circuit.FailureThreshold      = 5;      // consecutive failures to open
    p.Circuit.OpenDurationSeconds   = 30;     // time Open before HalfOpen probe
    p.Circuit.SuccessThreshold      = 1;      // successes in HalfOpen to close
    p.Circuit.OnlyCountTransient    = true;   // default true - see Trap 4

    // Timeout
    p.Timeout.TimeoutMs = 5000;     // per-attempt ceiling. <= 0 disables.

    // Rate limiter
    p.RateLimiter.Enabled       = true;
    p.RateLimiter.Strategy      = RateLimitStrategy.SlidingWindow;  // or TokenBucket, FixedWindow, ConcurrencyLimit
    p.RateLimiter.PermitLimit   = 100;
    p.RateLimiter.WindowSeconds = 60;
    p.RateLimiter.QueueLimit    = 0;         // 0 = reject immediately
    p.RateLimiter.QueueTimeoutMs = 0;

    // Bulkhead
    p.Bulkhead.Enabled        = true;
    p.Bulkhead.MaxConcurrency = 20;
    p.Bulkhead.MaxQueue       = 10;
    p.Bulkhead.QueueTimeoutMs = 2000;

    // Hedging - NOT safe on writes without an idempotency key
    p.Hedging.Enabled           = false;
    p.Hedging.MaxAttempts       = 2;
    p.Hedging.DelayMs           = 50;
    p.Hedging.CancelOnSuccess   = true;

    // Logging
    p.Logging.ScrubSensitiveData = true;    // see Trap 5

Every property name above is verified against the shipped XML.

## 8. Five traps you will hit if you do not know them

Trap 1 - positional arguments.
ExecuteAsync has six optional parameters. Passing them positionally invites silent misassignment. Always use named arguments.

Trap 2 - the circuit shares state across the whole app lifetime.
If you register a policy and then reuse the same policy name for multiple unrelated operations, the circuit counts failures across all of them. A single flaky caller can open the circuit for every other caller of that policy. Use one policy name per logical dependency, not per method.

Trap 3 - the time budget works, but the circuit sits outside it.
If you pass timeBudgetMs and also have a circuit configured, a failing operation can trip the circuit before the budget has any effect. Neutralize the circuit for tests that only want to observe budget behavior:

    p.Circuit.FailureThreshold = 100;   // effectively never opens during the test

Trap 4 - Circuit.OnlyCountTransient defaults to true.
A Permanent failure (a 400 response, an ArgumentException) does not count toward FailureThreshold. A circuit that should open on any failure must set p.Circuit.OnlyCountTransient = false.

Trap 5 - Logging.ScrubSensitiveData runs globally in 0.8.0.
Setting it to true on any one policy enables scrubbing for every event of every policy in the process. Setting it to false does not disable scrubbing once it is on. This is documented in docs/pci-scrubbing.md. Verify against the currently installed version before relying on per-policy isolation.

## 9. Idempotency keys - the two valid modes

Mode A - you supply the key:

    await _resilience.ExecuteAsync<int>(
        policyName: "payment-safe",
        operation: ChargeAsync,
        idempotencyKey: $"charge-order-{orderId}",
        ct: ct);

Every retry and hedged attempt of this call carries the same key. Downstream services can deduplicate.

Mode B - the library generates one:

    await _resilience.ExecuteAsync<int>(
        policyName: "payment-safe",
        operation: ChargeAsync,
        idempotencyKey: null,
        ct: ct);

The library derives a stable key from the ambient correlation ID. All attempts of this call share the derived key. Two separate calls to ExecuteAsync get different keys.

Inside the operation, read the propagated key with:

    using Portfolio.Resilience.Correlation;

    var key = IdempotencyContext.CurrentKey;

## 10. Time budget - what it does and does not do

When you pass timeBudgetMs, the executor pushes an ambient deadline for the duration of the call. The Retry and Timeout layers consult it:

- Retry stops retrying when the next attempt's delay cannot fit in the remaining budget.
- Timeout caps its effective per-attempt ceiling to min(configured, remaining).

The budget does not abort an in-flight operation early. If the operation ignores its cancellation token, it will run to completion. Always honour the token.

Example: a 1500ms budget with 100ms base delay and 5 attempts.

    await _resilience.ExecuteAsync<int>(
        policyName: "budgeted-policy",
        operation: FailingOperation,
        timeBudgetMs: 1500,
        ct: ct);

Result on the sample: 4 attempts, ~750ms. The same policy unbounded: 6 attempts, ~3200ms. The budget stopped the retry sequence when the next delay no longer fit.
## 11. HTTP integration - two patterns

Pattern A - one-line safe defaults. Use when you want retry + circuit + timeout and nothing else:

    builder.Services
        .AddHttpClient("auth-service", c => c.BaseAddress = new Uri("https://auth.internal"))
        .AddStandardResilienceHandler();

Every request through that client flows through the standard policy.

Pattern B - a named policy you control. Use when you need rate limiting, bulkhead, hedging, or custom budgets:

    builder.Services
        .AddHttpClient("auth-service", c => c.BaseAddress = new Uri("https://auth.internal"))
        .AddResilientHandler("auth-service");

The named policy must already be registered via AddPortfolioResilience.

## 12. Policy composition - when the standard pipeline is not enough

For a custom layer order, build a pipeline directly:

    using Portfolio.Resilience.Policies;

    var pipeline = ResiliencePipeline.Wrap(
        rateLimiterBuilder,
        bulkheadBuilder,
        retryBuilder,
        circuitBuilder,
        timeoutBuilder);

    var definition = new PolicyDefinition { Name = "custom" };
    // configure definition.* here

    var result = await pipeline.ExecuteAsync<int>(
        policyName: "custom",
        operation: DoWorkAsync,
        definition: definition,
        ct: ct);

Or use the fluent builder for conditional layers:

    var pipeline = new ResiliencePipelineBuilder()
        .WithName("custom")
        .Add(retryBuilder)
        .AddIf(isProduction, circuitBuilder)
        .Add(timeoutBuilder)
        .Build();

Every builder implements IResiliencePolicy. Custom layers can implement it too.

## 13. Observability - where events land

Register sinks at AddPortfolioResilience time:

    builder.Services.AddPortfolioResilience(r => r
        .AddLogSink(new ConsoleLogSink())              // stdout, JSON lines
        .AddLogSink(new FileLogSink("/var/log/resil")) // daily rotated files
        .AddPolicy("auth-service", /* ... */));

Multiple AddLogSink calls compose into a CompositeLogSink automatically. A broken sink does not break the others.

For OpenTelemetry, add the optional package and register it:

    dotnet add package Portfolio.Resilience.OpenTelemetry

    builder.Services.AddPortfolioResilienceOpenTelemetry();

Events become OTel logs and metrics. See docs/opentelemetry.md.

## 14. Reading circuit state at runtime

The ICircuitBreakerMonitor is resolvable from DI:

    public sealed class HealthController
    {
        private readonly ICircuitBreakerMonitor _monitor;

        public HealthController(ICircuitBreakerMonitor monitor) => _monitor = monitor;

        public CircuitSnapshot Get(string policyName) => _monitor.Get(policyName);
    }

CircuitSnapshot carries PolicyName, State (Closed / Open / HalfOpen), ConsecutiveFailures, OpenedAtUtc, NextProbeAtUtc. This is the recommended way to observe circuit state - it does not depend on events reaching a sink.

## 15. Failure semantics - what throws and when

When the pipeline exhausts all layers and no fallback is provided, ExecuteAsync throws a ResilienceException. The exception carries:

- Category - one of Transient, Permanent, CircuitOpen, Timeout, FallbackUsed, Unknown
- PolicyName - the policy that ran
- AttemptsMade - how many times the operation was invoked
- TotalDuration - wall-clock time across all layers
- CorrelationId - the ambient correlation ID at the time of the call

Catch it precisely:

    try
    {
        await _resilience.ExecuteAsync<int>(/* ... */);
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.Transient)
    {
        // downstream unavailable - return a degraded response, queue for later
    }
    catch (ResilienceException ex) when (ex.Category == ResilienceErrorCategory.CircuitOpen)
    {
        // fail fast - the dependency is known to be down
    }

## 16. The five-line sanity check for a new project

After wiring the library into a new project, verify the pipeline end-to-end with this probe:

    var executor = provider.GetRequiredService<IResilienceExecutor>();
    var calls = 0;
    try
    {
        await executor.ExecuteAsync<int>(
            policyName: "your-policy",
            operation: _ => { calls++; throw new TimeoutException("probe"); },
            ct: CancellationToken.None);
    }
    catch { }
    Console.WriteLine($"attempts observed: {calls}");

With `MaxAttempts = 3` on your policy, you should see `3`. If you see `1`, the retry layer is disabled or the failure is classified as Permanent. If you see `0`, the call never reached the pipeline - check the policy name against what you registered.

## 17. Reference

The sample's ten scenario files under `Scenarios\` are the canonical example of every shape above. Read them in order:

    01_RetryScenario.cs          - transient retry, CallSucceeded.Attempt
    02_CircuitScenario.cs        - open / reject / HalfOpen / close
    03_TimeoutScenario.cs        - per-attempt ceiling, caller cancellation
    04_RateLimiterScenario.cs    - all four strategies
    05_BulkheadScenario.cs       - concurrency cap, queue, rejection
    06_HedgingScenario.cs        - staggered parallel attempts
    07_IdempotencyScenario.cs    - explicit and derived keys
    08_ScrubbingScenario.cs      - PAN / CVV / SSN masking
    09_TimeBudgetScenario.cs     - total wall-clock budget
    10_CombinedScenario.cs       - WithPaymentSafeDefaults()

Each file is about 100 lines and self-contained.

## 18. Related documentation

- docs/retry.md - backoff formula and tuning
- docs/circuit-breaker.md - state machine
- docs/timeout.md - ceiling semantics
- docs/rate-limiter.md - four strategies, queue behavior
- docs/bulkhead.md - concurrency cap, waiter queue
- docs/hedging.md - parallel attempts, safety limits
- docs/composition.md - custom pipeline order
- docs/idempotency.md - key propagation rules
- docs/pci-scrubbing.md - masking and the global-scrubber behavior
- docs/time-budget.md - budget semantics
- docs/logging.md - event schema and sinks
- docs/metrics.md - p50 / p95 / p99 and error rate
- docs/correlation.md - ambient correlation IDs
- docs/http-integration.md - delegating handler setup
- docs/opentelemetry.md - optional OTel export
- docs/analyzers.md - optional Roslyn analyzers
- SPEC.md - cross-language event contract
