<!--
filepath: docs/error-classification.md
package:  Portfolio.Resilience | since: v0.5.0
purpose:  Explains how exceptions are mapped to resilience error categories.
-->

# Error Classification

## What it is

Every exception that escapes a resilience-wrapped operation is classified into one
of six **resilience error categories**. The category then drives decisions:
"should we retry this?", "does this count against the circuit?", "does the caller
need to see this as-is?"

Classification is performed by a single class: `ErrorClassifier`. It is stateless,
deterministic, and configurable.

## Why it exists

Not all failures are equal. Consider three exceptions:

1. **HTTP 503** from auth-service. Transient — retry may succeed.
2. **HTTP 401** because the user's token expired. Permanent — retrying is pointless.
3. **`ArgumentException`** because we passed a bad argument. Our bug — retrying hides it.

Without classification:
- Retry would hammer auth-service for a 401 (wasteful, and could lock out the account).
- Circuit breaker would open because of our own `ArgumentException` (wrong — the dependency is fine).
- Callers would see different exception types depending on which layer failed (confusing).

With classification:
- Retry sees 401 → permanent → skips retry → user sees the real error immediately.
- Circuit sees `ArgumentException` → doesn't count it toward opening the circuit.
- Caller sees a uniform `ResilienceException` with `Category = Permanent`.

## When it matters

**Whenever a wrapped operation fails.** The classification result decides:

| Decision | Driven by category |
|----------|-------------------|
| Should retry attempt again? | `Transient` → yes. `Permanent` → no. |
| Does this count against the circuit threshold? | `Transient` → yes (default). `Permanent` → no. |
| Should we surface `ResilienceErrorCategory.Timeout`? | Only when `TimeoutPolicyBuilder` fires. |
| Should the caller see the raw exception or a wrapper? | Wrapper (`ResilienceException`) carries the category. |

## The six categories

| Category | Meaning | Retryable | Example |
|----------|---------|-----------|---------|
| `Transient` | Temporary fault. Retry may succeed. | Yes | HTTP 503, network socket error, deadlock |
| `Permanent` | Unrecoverable without code or config change. | No | HTTP 401, `ArgumentException`, SQL integrity violation |
| `CircuitOpen` | Circuit is open. Operation was never attempted. | No | Any call while a circuit is Open |
| `Timeout` | Policy timeout ceiling fired. | Maybe | `TimeoutPolicyBuilder` exceeded `TimeoutMs` |
| `FallbackUsed` | Primary failed, fallback produced a result. | No (logged only) | Any call that hit a fallback |
| `Unknown` | Not yet classified. | Depends | Only appears before classification runs |

**Retryability by category:**
- `Transient` → retried up to `MaxAttempts`
- `Permanent` → not retried (unless `RetryOnPermanent = true`, opt-in)
- `CircuitOpen` → never retried (the circuit will stay open until `OpenDurationSeconds` passes)
- `Timeout` → retried (a slow dependency may be fast on the next attempt)
- `FallbackUsed` → logged but not retried (a fallback already produced a value)
- `Unknown` → not retried (conservative)
## Classification priority

The classifier examines an exception in a strict order. First match wins. This
ordering is documented in `ErrorClassifier.Classify`:

    1. Already-classified ResilienceException  -> passthrough category
    2. OperationCanceledException              -> Permanent (if caller's token) / Timeout (otherwise)
    3. HttpRequestException with StatusCode    -> mapped from HTTP status code
    4. Exception with SqlState property        -> mapped from PostgreSQL SQLSTATE
    5. Exception type name                     -> matched against transient/permanent lists
    6. Fallback                                -> Permanent

**Why this order matters:**
- Rule 1 must be first — otherwise a wrapped `ResilienceException` would be
  re-classified based on its type name (which is `Portfolio.Resilience.Errors.ResilienceException`,
  not on any list), losing its original category.
- Rule 2 must come before rule 5 — `OperationCanceledException` is a common .NET
  exception that would otherwise fall through to "unknown type -> Permanent", which
  is correct for caller cancellation but wrong for internal timeouts.
- Rule 4 uses **reflection-free duck typing**: `Npgsql.NpgsqlException` is not a
  compile-time dependency of this library. We inspect for a `SqlState` property.
  Any exception that exposes `SqlState` gets SQLSTATE-based classification.

## The HTTP status mapping

By default:

**Transient HTTP codes** (retryable):
- `408` Request Timeout
- `425` Too Early
- `429` Too Many Requests
- `500` Internal Server Error
- `502` Bad Gateway
- `503` Service Unavailable
- `504` Gateway Timeout
- `507` Insufficient Storage
- `509` Bandwidth Limit Exceeded

**Permanent HTTP codes** (not retryable):
- `400, 401, 403, 404, 405, 406, 407, 409, 410, 411, 412, 413, 414, 415, 416, 417, 418, 421, 422, 423, 424, 426, 428, 431, 451`

**Unlisted codes** fall through to class-based rules:
- Any `5xx` not listed -> `Transient` (transient by class)
- Any `4xx` not listed -> `Permanent` (permanent by class)
## PostgreSQL SQLSTATE classification

The classifier recognizes these SQLSTATEs as **transient** (retryable):

| SQLSTATE | Meaning |
|----------|---------|
| `08000` | Connection exception |
| `08003` | Connection does not exist |
| `08006` | Connection failure |
| `08001` | Client unable to establish connection |
| `08004` | Server rejected connection |
| `08007` | Transaction resolution unknown |
| `40001` | Serialization failure |
| `40P01` | Deadlock detected |
| `57P01` | Admin shutdown |
| `57P02` | Crash shutdown |
| `57P03` | Cannot connect now |
| `53300` | Too many connections |
| `55P03` | Lock not available |

Everything else with a `SqlState` is classified as **permanent**. This is the
conservative choice — retrying an integrity violation repeatedly will not help.

## Exception type names

**Transient by type:**
- `System.Net.Http.HttpRequestException`
- `System.Net.Sockets.SocketException`
- `System.IO.IOException`
- `System.TimeoutException`
- `Npgsql.NpgsqlException`
- `StackExchange.Redis.RedisConnectionException`
- `StackExchange.Redis.RedisTimeoutException`

**Permanent by type:**
- `System.ArgumentException`
- `System.ArgumentNullException`
- `System.ArgumentOutOfRangeException`
- `System.InvalidOperationException`
- `System.NotSupportedException`
- `System.FormatException`
- `System.ValidationException`

**Not listed -> `Permanent` by default.**

## Design note: why TimeoutException is Transient, not Timeout

`System.TimeoutException` is a general-purpose BCL exception. It is thrown for
network timeouts, socket timeouts, HTTP timeouts — all cases where a retry may
succeed.

`ResilienceErrorCategory.Timeout` is reserved for a **different** case: when
`TimeoutPolicyBuilder`'s own ceiling fires. That is an explicit "we set a ceiling
and blew it" signal, distinct from "a network operation took too long".

If you want a BCL timeout to be classified as `Timeout`, wrap it — or use
`ErrorClassificationOptions` to move it between lists.

## Configuration

All classification rules are configurable via `ErrorClassificationOptions`:

    var options = new ErrorClassificationOptions();

    // Treat 418 as transient (fun, but also: some APIs use unusual codes)
    options.TransientHttpStatusCodes.Add(418);

    // Treat a custom exception as permanent
    options.PermanentExceptionTypeNames.Add("MyApp.DataIntegrityException");

    // Don't treat caller cancellation as permanent (default: true)
    options.TreatCancellationAsPermanent = false;

    var classifier = new ErrorClassifier(options);

Via configuration (appsettings.json / env vars), the lists are set as arrays —
useful if a service needs different rules without code changes.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience/Errors/ErrorClassifier.cs` | The classifier |
| `src/Portfolio.Resilience/Configuration/ErrorClassificationOptions.cs` | Configurable rules |
| `src/Portfolio.Resilience/Errors/ResilienceErrorCategory.cs` | The enum |
| `src/Portfolio.Resilience/Errors/ResilienceException.cs` | Carries `Category` for rethrow |

## Common mistakes

**Mistake 1 — expecting a 404 to throw.**
It will not in HTTP integration. 404 is a legitimate response, returned to the caller
unchanged. Only transient statuses (5xx, 408, 425, 429) are converted to exceptions
by the resilient HTTP handler.

**Mistake 2 — assuming "not in the transient list" means "not retried".**
The classifier uses a priority order. An exception might be permanent by HTTP code
but transient by class (5xx class rule). Always check the code path, not just the
lists.

**Mistake 3 — overriding the classification per-call instead of per-policy.**
Classification lives in `ErrorClassificationOptions`, shared by all policies. If
you want per-policy rules, you would configure per-policy classifiers. The default
is fine for 99% of cases.

**Mistake 4 — catching the raw exception instead of ResilienceException.**
After a resilience call fails, callers see `ResilienceException` (with `Category`,
`PolicyName`, `AttemptsMade`, `CorrelationId`). The original exception is in
`.InnerException`. Catching `HttpRequestException` directly will miss the wrapper.

**Mistake 5 — treating Unknown as permanent.**
`Unknown` is only produced *before* classification. After classification, one of
the six real categories is set. If you see `Unknown` in production logs, it means
the classifier threw during classification — a bug, not a rule.

## Testing

Verified by `tests/Portfolio.Resilience.Tests/ErrorClassifierTests.cs` (20 tests):

- Passthrough of `ResilienceException`
- Cancellation with and without requested token
- HTTP status codes (transient + permanent)
- Unlisted 4xx / 5xx class fallback
- `HttpRequestException` without `StatusCode` (type-name path)
- PostgreSQL SQLSTATEs
- Type name lists (transient + permanent)
- Configurability: custom HTTP codes, cancellation toggle

## See also

- [retry.md](retry.md) — how classification gates retry attempts
- [circuit-breaker.md](circuit-breaker.md) — how classification gates circuit counting
- [timeout.md](timeout.md) — why `Timeout` is distinct from `Transient`
- [executor.md](executor.md) — where classification happens in the pipeline
- [../SPEC.md](../SPEC.md) §ErrorCategories — the normative contract
