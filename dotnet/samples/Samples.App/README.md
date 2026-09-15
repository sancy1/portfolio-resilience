<!--
filepath: dotnet/samples/Samples.App/README.md
package:  Samples.App | since: n/a
purpose:  Runnable reference consumer for the published Portfolio.Resilience package
-->

# Samples.App

A runnable console application that consumes the published Portfolio.Resilience 0.8.0 package from nuget.org and exercises every capability end-to-end.

The sample is a consumer, not a producer. It never references the library source tree. It restores from nuget.org, builds against the shipped DLL, and demonstrates the public API exactly as any other .NET service would.

## Published packages

The sample consumes the published packages from nuget.org:

- **Core library (required)**: https://www.nuget.org/packages/Portfolio.Resilience
- **Core version history**: https://www.nuget.org/packages/Portfolio.Resilience#versions-tab
- **OpenTelemetry export (optional)**: https://www.nuget.org/packages/Portfolio.Resilience.OpenTelemetry#versions-tab
- **Roslyn analyzers (optional)**: https://www.nuget.org/packages/Portfolio.Resilience.Analyzers#versions-tab

Install the core package with:

    dotnet add package Portfolio.Resilience

The sample itself pins a version **range** (`[0.8.0,1.0.0)`) so it always restores the newest 0.x that nuget.org has published. See the section below on how the version is chosen.

## What it demonstrates

Ten scenarios, run in order, each one printing a [PASS] or [FAIL] line:

| # | Scenario | Proves |
|---|----------|--------|
| 01 | Retry | Transient failure retried; succeeds on attempt 2 |
| 02 | Circuit | Opens after 2 failures; rejects without invoking; recovers via HalfOpen probe |
| 03 | Timeout | Per-attempt ceiling cancels a slow operation; caller cancellation preserved |
| 04 | RateLimiter | All four strategies enforced: TokenBucket, SlidingWindow, FixedWindow, ConcurrencyLimit |
| 05 | Bulkhead | Concurrency cap + bounded queue + immediate rejection |
| 06 | Hedging | Slow primary beaten by a staggered hedge |
| 07 | Idempotency | Explicit and auto-derived keys preserved across retries (v0.8.0) |
| 08 | Scrubbing | PAN / CVV / SSN masked as [REDACTED] before any sink sees them (v0.8.0) |
| 09 | TimeBudget | Total wall-clock budget behavior (v0.8.0) - see Findings |
| 10 | Combined | WithPaymentSafeDefaults() composes 6 layers in the safe order (v0.8.0) |
## How to run

From the repository root:

    dotnet run --project dotnet\samples\Samples.App\Samples.App.csproj

From anywhere:

    dotnet run --project C:\Users\HP\Desktop\portfolio-resilience\dotnet\samples\Samples.App\Samples.App.csproj

Expected output: a banner, ten scenario blocks with [PASS] or [FAIL] on each, and a summary. Exit code 0 on success, non-zero on any failure. The sample is CI-verifiable.

Total wall-clock time is about 7 seconds: scenario 02 waits 1.2s past the circuit's open duration; scenario 09 runs the unbounded retry sequence (~3.2s) to contrast with the budgeted runs.

## How the package version is chosen

The project references Portfolio.Resilience with a version range:

    <PackageReference Include="Portfolio.Resilience" Version="[0.8.0,1.0.0)" />

NuGet resolves the range to the lowest published version at or above 0.8.0 and below 1.0.0. When a 0.8.1 or 0.9.0 ships, the sample restores it without editing the csproj. When a 1.0.0 ships, the range refuses to move - a human decides whether the sample needs adjusting.

The project name and namespace contain no version number. The banner prints the resolved version at runtime, read from the loaded assembly. There is nothing to keep in sync.

## How the code is organized

    Samples.App\
      Program.cs                          entry point - DI bootstrap + scenario loop
      Samples.App.csproj                  net10.0, PackageReference to the published package
      Infrastructure\
        ScenarioResult.cs                 immutable result record
        ScenarioRunner.cs                 runs one scenario, times it, catches failures
        ConsoleRenderer.cs                banner, per-scenario block, summary
        CapturingSink.cs                  in-memory ILogSink for assertions
        FakeHttpHandler.cs                in-process HTTP handler (no real network)
      Scenarios\
        01_RetryScenario.cs
        02_CircuitScenario.cs
        ...
        10_CombinedScenario.cs

Every .cs file carries a header that documents its filepath, layer, dependencies, and references. Every public type has XML documentation. The code is ASCII-only, BOM-less, and compiles with TreatWarningsAsErrors=true.
## Findings from the shipped artifact

The following were observed by running the sample against Portfolio.Resilience 0.8.0 from nuget.org. They are recorded here as a quality signal for the library. None of them are fixed in the sample, because the sample must not modify the library.

1. ResilienceEvent properties are undocumented in the shipped XML.
   The XML has a type-level summary for ResilienceEvent but no member entries for its ten properties (EventType, PolicyName, CorrelationId, TimestampUtc, Attempt, DurationMs, ErrorCategory, ErrorMessage, ErrorType, Metadata). Consumer IntelliSense shows the type summary only. The properties are visible at runtime through reflection. Recommended fix: add /// summaries to the record properties in the source, which will emit them into the XML on the next build.

2. CompositePolicyBuilder XML summary omits Hedging from the default pipeline order.
   The constructor accepts a HedgingPolicyBuilder and the WithPaymentSafeDefaults remarks list Hedging in the default order, but the CompositePolicyBuilder summary shows RateLimiter -> Bulkhead -> Retry -> Circuit -> Timeout. A consumer reading only the summary is misled. Recommended fix: update the summary to include Hedging between Bulkhead and Retry.

3. No documented non-DI path to construct an IResilienceExecutor.
   ResilienceBuilder produces a ResilienceOptions tree, but there is no public factory that turns options into an executor without going through Microsoft.Extensions.DependencyInjection. The sample uses DI (legitimate for a console app). Design choice, not a bug - but worth documenting explicitly for consumers who cannot use DI.

4. Circuit.OnlyCountTransient defaults to true.
   A circuit that should open on any failure must explicitly set Circuit.OnlyCountTransient = false. Permanent failures do not count toward the threshold under the default. Documented behavior; the sample configures this explicitly where it matters.

5. ConcurrencyLimit rate limiter caps simultaneous calls, not cumulative.
   A sequential burst of N calls never triggers the limit even when N exceeds PermitLimit. Only concurrent overlap does. Documented behavior; the sample proves it with truly overlapping operations.

## What works well

- Retry, circuit, timeout, rate limiter, bulkhead, and hedging all behave as documented.
- Idempotency key propagation (v0.8.0) works for both explicit and auto-derived keys, with the same key preserved across retry attempts.
- PCI-safe event scrubbing (v0.8.0) correctly masks PAN, CVV, and SSN patterns before any sink sees the event.
- Time budget propagation (v0.8.0) caps the total wall-clock duration of the retry sequence: a 1500ms budget stops the retry layer early, while the same policy unbounded runs to completion.
- ResiliencePipeline.WithPaymentSafeDefaults() (v0.8.0) composes six layers in the documented safe order: RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout.
- Structured correlation IDs, ambient contexts, and the ICircuitBreakerMonitor API all behave correctly.
- The public API surface is stable, consistent, and readable.
- The package has zero third-party dependencies.

## What a consumer should know

- The default pipeline order is RateLimiter -> Bulkhead -> Hedging -> Retry -> Circuit -> Timeout.
- The payment-safe pipeline order (ResiliencePipeline.WithPaymentSafeDefaults()) is RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout.
- The Circuit.OnlyCountTransient default is true - set it to false if the circuit should open on Permanent failures as well.
- The ConcurrencyLimit rate limiter caps simultaneous calls, not cumulative calls.
- Logging.ScrubSensitiveData is documented as opt-in but runs globally in 0.8.0: once any policy sets it to true, every policy's events are scrubbed. Existing consumers upgrading from 0.7.0 should read docs/pci-scrubbing.md before enabling it.
- The LoggingOptions per-event-type toggles are declared but not wired to suppress emission in 0.8.0. This is a known limitation carried forward from 0.6.0 and documented in the CHANGELOG.

## How to use this sample

- Read it as a reference for how to wire the library into a console or service host.
- Run it to verify the library still behaves as documented after a version bump or an SDK update.
- Copy from it - each scenario file is self-contained and about 100 lines.
- Do not modify it to work around a library issue. Record the finding instead. The sample is a canary; its job is to report changes, not to hide them.

## License

This sample is part of the Portfolio.Resilience repository and is licensed under the MIT license. See the repository LICENSE file.
