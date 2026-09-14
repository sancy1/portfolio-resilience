<!--
filepath: docs/time-budget.md
package:  Portfolio.Resilience | since: v0.8.0
purpose:  Explains total wall-clock budget enforcement across every pipeline layer.
-->

# Time Budget

## What it is

A **total wall-clock budget** for the entire pipeline. Unlike a per-attempt
timeout, the budget spans every retry, every hedge, and the initial call. When
the budget runs out, the pipeline stops adding new work and surfaces the
failure it has.

    await _resilience.ExecuteAsync(
        "stripe-charge",
        ct => _stripe.ChargeAsync(request, ct),
        timeBudgetMs: 3000,
        ct: ct);

The pipeline must return within ~3000ms, or the last attempt's exception is
thrown with whatever partial result the pipeline reached.

## Why it exists

**For any caller with an SLA - not just payments.**

A retry pipeline can multiply total wall-clock time dramatically. With a
5-second per-attempt timeout and 3 retries, worst case is
`4 * 5000ms + backoff ˜ 20 seconds`. If the caller's HTTP handler has a
3-second SLA, that pipeline fails the SLA even though every individual layer
is configured correctly.

Time budget gives the caller one knob to express "this whole operation must
fit in N milliseconds." Each layer self-caps:

- **Retry** stops early if the next delay + attempt cannot fit in the budget.
- **Timeout** uses `min(configured TimeoutMs, remaining budget)`.
- **Hedging** does not fire a new hedge if the budget cannot cover the stagger
  delay plus the attempt itself.

Payments are the strictest case: a 3-second card authorization SLA is
non-negotiable. But the same mechanism serves a search endpoint with a 500ms
budget, a feed endpoint with a 1s budget, or any operation where "eventually"
is not good enough.

## When you need it

**Yes:**

- Every caller with a hard SLA (payments, search, user-facing reads)
- Non-interactive workloads with a scheduler-driven deadline
- Any pipeline where the combined retry + hedge total time would exceed the
  caller's acceptable latency

**No:**

- Background jobs with no deadline (large exports, batch processing)
- Operations that legitimately take minutes (LLM inference, video encoding)

When `timeBudgetMs` is null, every layer behaves exactly as in v0.7.0. There
is no cost to the feature when it is not used.

## How it works

### The ambient deadline

`TimeBudgetContext.Push(budgetMs)` converts the budget into an **absolute UTC
deadline** at the moment the executor begins the call. Every layer reads
`TimeBudgetContext.RemainingMs` - the difference between the deadline and
`DateTime.UtcNow`.

The deadline is stored in an `AsyncLocal<DateTime?>`, so it flows through
`await` boundaries and is isolated between concurrent calls. When the executor
returns (success or failure), the scope is restored.

### Layer behavior

**Retry.** Before each retry, `RetryPolicyBuilder` checks whether the next
delay plus a floor for the next attempt fits in the remaining budget. If not,
it rethrows the last exception without scheduling another attempt. The
caller's exception type is preserved - the retry layer never wraps.

**Timeout.** `TimeoutPolicyBuilder` computes
`effectiveTimeoutMs = min(configured TimeoutMs, remaining budget)`. When the
configured timeout is disabled (`TimeoutMs <= 0`) but a budget is active, the
budget becomes the effective ceiling. When the budget is already exhausted
at entry, the timeout throws immediately with `Category = Timeout`.

**Hedging.** Before firing a new hedge, `HedgingPolicyBuilder` checks whether
`remaining budget >= delay + attemptTimeout`. If not, the hedge is skipped.
Already-in-flight attempts continue and whichever completes first wins.

**Circuit.** No change. The circuit delegates to the inner timeout, which caps
itself by budget. The circuit's open-rejection is instantaneous and unaffected.

### Worked example

    _resilience.ExecuteAsync(
        "stripe-charge",
        ct => _stripe.ChargeAsync(request, ct),
        timeBudgetMs: 3000,
        ct: ct);

Policy: `Retry.MaxAttempts = 3`, `Retry.BaseDelayMs = 100`,
`Timeout.TimeoutMs = 5000`.

Without budget: worst case is 4 attempts × 5000ms + backoff ˜ 20 seconds.

With a 3000ms budget:

1. Executor pushes deadline = now + 3000ms.
2. Attempt 1 runs with `min(5000, 3000) = 3000ms` timeout.
3. If attempt 1 fails at 800ms, ~2200ms remain. Retry checks: next delay
   `~100ms` + floor `~100ms` fits. Retry proceeds.
4. Attempt 2 runs with `min(5000, 2100) = 2100ms` timeout.
5. If attempt 2 fails at 1900ms, ~100ms remain. Retry checks: next delay
   `~200ms` + floor does NOT fit. Retry stops.
6. Caller sees the exception from attempt 2, at ~2800ms total, within the
   3000ms SLA.

## Configuration

### Enabling

Per call site:

    await _resilience.ExecuteAsync(
        "stripe-charge",
        ct => _stripe.ChargeAsync(request, ct),
        timeBudgetMs: 3000,
        ct: ct);

The budget is a **per-call** parameter, not a policy setting. The same policy
can be called with different budgets from different call sites.

### Relationship to per-attempt timeout

The two compose: the budget caps the **total**, the timeout caps the
**attempt**. `effectiveTimeout = min(configured, remaining budget)`.

Set `Timeout.TimeoutMs` to the per-attempt ceiling you consider healthy. Set
`timeBudgetMs` to the caller's total SLA. Neither replaces the other.

### Relationship to HttpClient.Timeout

`HttpClient.Timeout` is a third ceiling, outside the pipeline. Make it
**larger** than the budget so it never fires first.

    c.Timeout = TimeSpan.FromSeconds(30);    // outer ceiling
    // policy: Timeout.TimeoutMs = 5000
    // call site: timeBudgetMs = 3000

The budget will always fire before `HttpClient.Timeout` does.

### Validation

`timeBudgetMs` must be a positive integer. Zero or negative throws
`ArgumentOutOfRangeException` from `TimeBudgetContext.Push`.

## Common mistakes

**Mistake 1 - setting the budget equal to the per-attempt timeout.**
`timeBudgetMs = 5000` with `Timeout.TimeoutMs = 5000` means only one attempt
fits. If the operation needs retries, give the budget enough room for
`MaxAttempts + 1` attempts plus backoff.

**Mistake 2 - expecting the budget to extend a shorter configured timeout.**
The budget is a **cap**, not an extension. `min(configured, remaining)` means
a shorter configured timeout always wins.

**Mistake 3 - relying on the budget as a hard kill.**
The budget stops **new** work. If an attempt is already in flight, it runs to
its own timeout or completion. The pipeline returns shortly after the budget
expires, but not at the microsecond level.

**Mistake 4 - ignoring the `AsyncLocal` scope when nesting calls.**
If call site A sets `timeBudgetMs = 1000` and, inside, call site B sets
`timeBudgetMs = 500`, B's budget replaces A's for the duration of B. When B
returns, A's budget is restored. Nested budgets compose correctly, but the
outer budget is not automatically reduced by the inner call's elapsed time.
Use nested budgets deliberately.

**Mistake 5 - setting a budget smaller than the operation's p50.**
A 50ms budget on an operation with a 200ms p50 means every call hits the
budget. Set the budget at or above the operation's normal p99.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/TimeBudgetContextTests.cs`
(12 tests), plus extensions to `ResilienceExecutorTests` (+4),
`RetryPolicyBuilderTests` (+4), `TimeoutPolicyBuilderTests` (+4), and
`HedgingPolicyBuilderTests` (+3), plus
`ResilienceIntegrationTestsV08_TimeBudget.cs` (4 end-to-end tests).

## See also

- [retry.md](retry.md) - how retry interacts with the budget
- [timeout.md](timeout.md) - how timeout caps to the remaining budget
- [hedging.md](hedging.md) - how hedging stops firing new attempts
- [executor.md](executor.md) - the API surface
- [../SPEC.md](../SPEC.md) section 20 - the normative contract
