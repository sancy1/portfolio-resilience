<!--
filepath: docs/analyzers.md
package:  Portfolio.Resilience.Analyzers | since: v0.7.0
purpose:  Explains the Roslyn analyzers - compile-time checks for common resilience mistakes.
-->

# Analyzers

## What it is

An **optional companion package** that ships Roslyn analyzers for
`Portfolio.Resilience`. The analyzers run during compilation and warn about
code patterns that are almost certainly wrong.

Two rules ship in v0.7.0:

| Rule ID | Severity | Detects |
|---------|----------|---------|
| `PR0001` | Warning | An `HttpClient` obtained from `IHttpClientFactory` is called directly, bypassing the resilience pipeline |
| `PR0002` | Warning | A policy enables a feature but sets a companion value to `0` or negative |

**The core library and the OpenTelemetry package do not depend on this
package.** Install it only if you want compile-time checks.

## Why it exists

**Bugs are cheaper to catch at compile time than at runtime.**

Two classes of mistake in resilience code are common and costly:

1. **Forgetting to use the pipeline.** An `HttpClient` is registered with
   `AddResilientHandler`, but a new method calls `GetAsync` directly on it.
   No retry, no circuit, no timeout. The bug only surfaces under load.

2. **Enabling a feature with an invalid value.** A policy sets
   `RateLimiter.Enabled = true` and `PermitLimit = 0`. At startup, the library
   warns. In production, the rate limiter rejects every call. Caught at
   compile time instead, the developer sees the mistake while typing.

Both analyzers turn "later" into "now."

## When you need it

**Yes:**

- You have more than one developer touching resilience code.
- You have a large codebase where new call sites may bypass the pipeline.
- You prefer compile-time warnings to runtime exceptions.

**No:**

- You have a tiny codebase where you can see every call site.
- You deliberately use `HttpClient` for non-resilient purposes (health probes,
  metrics scrapes) at scale, and the false positives outweigh the value.
- Your team has a policy against analyzer packages.

## Install

    dotnet add package Portfolio.Resilience
    dotnet add package Portfolio.Resilience.Analyzers

The analyzer package has no runtime dependency on your application - it is
loaded by the compiler only. It has no impact on the compiled output, on
binary size, or on runtime performance.

## Walkthrough - PR0001 (HttpClient bypass)

### The problem

You register an `HttpClient` with the resilience pipeline:

    builder.Services
        .AddHttpClient("auth-service")
        .AddResilientHandler("auth-service");

Later, a colleague adds a method to a service:

    public sealed class UserService
    {
        private readonly HttpClient _http;

        public UserService(IHttpClientFactory factory)
        {
            _http = factory.CreateClient("auth-service");
        }

        public Task<string> GetUserAsync(string id) =>
            _http.GetStringAsync($"/api/v1/users/{id}");   // bypasses the pipeline
    }

This code compiles, runs, and looks right. But **every call bypasses retry,
circuit breaking, and timeout**. The bug only shows up when the dependency
has a bad day.

### What the analyzer says

On the line `_http.GetStringAsync(...)`, the compiler emits:

    warning PR0001: HttpClient '_http' is called directly. Register it with
    AddResilientHandler() or wrap calls with IResilienceExecutor to apply the
    resilience pipeline.

You see this in your IDE while typing, and in `dotnet build` output.

### The fix

Wrap the call in `IResilienceExecutor`:

    public sealed class UserService
    {
        private readonly HttpClient _http;
        private readonly IResilienceExecutor _resilience;

        public UserService(
            IHttpClientFactory factory,
            IResilienceExecutor resilience)
        {
            _http = factory.CreateClient("auth-service");
            _resilience = resilience;
        }

        public Task<string> GetUserAsync(string id, CancellationToken ct) =>
            _resilience.ExecuteAsync(
                "auth-service",
                token => _http.GetStringAsync($"/api/v1/users/{id}", token),
                ct: ct);
    }

Or, if you are not using the executor pattern, register the client with
`AddStandardResilienceHandler()` - either way, the pipeline is applied.

### How the analyzer decides

The heuristic:

1. Find a field or property of type `System.Net.Http.HttpClient`.
2. Recognize that it is assigned from `IHttpClientFactory.CreateClient(...)`
   - either in a field initializer or inside a constructor body.
3. Detect calls to `SendAsync`, `GetAsync`, `PostAsync`, `PutAsync`,
   `DeleteAsync`, `PatchAsync`, `GetStringAsync`, `GetByteArrayAsync`,
   `GetStreamAsync`, `GetFromJsonAsync`, `PostAsJsonAsync`, `PutAsJsonAsync`,
   or `DeleteFromJsonAsync` on that field.
4. **If the class has no `IResilienceExecutor` field or constructor
   parameter**, emit the warning.

The "no `IResilienceExecutor`" guard prevents false positives: a class that
already depends on the executor knows about resilience.

### When PR0001 does NOT fire

- The `HttpClient` is created via `new HttpClient()` - no factory involved.
- The class has an `IResilienceExecutor` field or constructor parameter.
- The field exists but is never called.
- Only non-`HttpClient` methods are called on the field (`Dispose()`, etc.).

## Walkthrough - PR0002 (policy misconfiguration)

### The problem

You configure a policy that enables a feature with a value that cannot work:

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("api", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = 0;   // cannot be 0
        }));

`PermitLimit = 0` means the limiter allows **zero** calls. The service will
reject every request. This compiles and starts cleanly; the library logs a
startup warning but does not throw. In production, it is a silent outage.

### What the analyzer says

On the line `p.RateLimiter.PermitLimit = 0;`, the compiler emits:

    warning PR0002: Policy enables RateLimiter but sets PermitLimit to 0.
    RateLimiter requires PermitLimit > 0 when enabled.

### The fix

    builder.Services.AddPortfolioResilience(r => r
        .AddPolicy("api", p =>
        {
            p.RateLimiter.Enabled = true;
            p.RateLimiter.PermitLimit = 100;   // correct
        }));

### What PR0002 checks

The rule fires when a feature is **enabled** and a companion value is
**non-positive**, **in the same `AddPolicy` lambda**:

| Feature enabled | Companion checked | Rule |
|-----------------|-------------------|------|
| `RateLimiter.Enabled = true` | `RateLimiter.PermitLimit` | Must be `> 0` |
| `Bulkhead.Enabled = true` | `Bulkhead.MaxConcurrency` | Must be `> 0` |
| `Hedging.Enabled = true` | `Hedging.MaxAttempts` | Must be `> 0` |
| - (always) | `Timeout.TimeoutMs` | Must be `>= 0` |
| - (always) | `Retry.MaxAttempts` | Must be `>= 0` |

### When PR0002 does NOT fire

- The feature is disabled (`Enabled = false` or unset) and a value is `0`.
  A placeholder is not a mistake until the feature is enabled.
- The value comes from configuration, not a literal. Runtime validation in
  `RateLimiterOptions.Validate` and `BulkheadOptions.Validate` handles those
  cases at startup.

## Configuration

### Disabling a rule globally

Add to your `.editorconfig`:

    [*.cs]
    dotnet_diagnostic.PR0001.severity = none
    dotnet_diagnostic.PR0002.severity = none

### Changing severity

Promote to error - a good choice for greenfield codebases where the rule
must be enforced:

    [*.cs]
    dotnet_diagnostic.PR0001.severity = error
    dotnet_diagnostic.PR0002.severity = error

### Suppressing a single occurrence

Local suppression is idiomatic for the rare legitimate case:

    #pragma warning disable PR0001
    var response = await _http.GetStringAsync("/healthz");
    #pragma warning restore PR0001

Always add a comment explaining why. If the file has several suppressions,
reconsider whether the client needs resilience.

### Reducing noise for a specific project

If a project deliberately uses `HttpClient` outside the pipeline for many
calls, disable PR0001 in that project's `.editorconfig`:

    [*.cs]
    dotnet_diagnostic.PR0001.severity = none

## When NOT to use it

- **Health probes and metrics scrapes.** A periodic GET to `/healthz` does
  not need retry - a failure is expected. Suppress the rule locally.
- **Interop with legacy code.** A class that must use `HttpClient` directly
  for a specific reason should either suppress locally or globally per file.
- **Small scripts and tools.** The value of the analyzer is realized in
  production code. A throwaway script does not need it.

## Troubleshooting

**"The analyzer is not running."**
- Confirm the package is installed: `dotnet list package` should show
  `Portfolio.Resilience.Analyzers`.
- Confirm your project targets .NET 10 or a runtime the analyzer supports.
- Check for `dotnet_diagnostic.PR0001.severity = none` in `.editorconfig`.

**"PR0001 is firing on code that is already wrapped."**
- The class must have an `IResilienceExecutor` field, property, or
  constructor parameter. If it accesses resilience some other way (a helper
  that internally wraps), the analyzer cannot see that and will warn.
- Fix: add a local suppression with a comment, or add an `IResilienceExecutor`
  parameter even if unused.

**"PR0002 is firing but the value is set correctly."**
- The rule reads literal values only. If the value is set twice - once
  correct, once `0` - the analyzer sees both and warns at the second. Remove
  the redundant assignment.

**"Warnings appear during publish, not build."**
- This is expected if the build is configured with `<TreatWarningsAsErrors>`
  off in Debug and on in Release. The analyzer emits in both configurations.

## Associated files

| File | Role |
|------|------|
| `src/Portfolio.Resilience.Analyzers/HttpClientBypassAnalyzer.cs` | PR0001 |
| `src/Portfolio.Resilience.Analyzers/MisconfigurationAnalyzer.cs` | PR0002 |
| `src/Portfolio.Resilience.Analyzers/AnalyzerReleases.Unshipped.md` | Release-tracking file required by the Roslyn SDK |
| `tests/Portfolio.Resilience.Analyzers.Tests/HttpClientBypassAnalyzerTests.cs` | 8 tests |
| `tests/Portfolio.Resilience.Analyzers.Tests/MisconfigurationAnalyzerTests.cs` | 9 tests |

## Testing

Verified by two test files (17 tests total):

- `HttpClientBypassAnalyzerTests.cs` (8 tests) - covers the analyzer
  descriptor, the fires-on-bypass case, and the four cases where PR0001
  does not fire (executor present, `new HttpClient`, no calls, non-HttpClient
  method).
- `MisconfigurationAnalyzerTests.cs` (9 tests) - covers the descriptor, each
  enabled-with-non-positive pair, both negative-only rules, the
  multiple-misconfiguration case, and the two "no warning" cases (valid
  configuration, disabled with placeholder).

The tests use **hand-rolled Roslyn compilations** with the SDK's reference
packs, not the `Microsoft.CodeAnalysis.Testing` framework. See the test file
headers for the reasoning.

## See also

- [http-integration.md](http-integration.md) - the `AddResilientHandler` API
  the PR0001 rule steers users toward
- [executor.md](executor.md) - the `IResilienceExecutor` API
- [rate-limiter.md](rate-limiter.md) - the options PR0002 validates
- [bulkhead.md](bulkhead.md) - the options PR0002 validates
- [hedging.md](hedging.md) - the options PR0002 validates
- [../SPEC.md](../SPEC.md) section 17 - the normative contract
