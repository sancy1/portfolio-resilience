// filepath: dotnet/samples/Samples.App/Program.cs
// layer: root | package: Samples.App | since: n/a
// purpose: Entry point - bootstraps DI, runs scenarios, renders output, returns exit code
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (top-level statements)
//   Depends on : Infrastructure (ScenarioRunner, ConsoleRenderer, CapturingSink), Scenarios
//   Used by    : dotnet run, CI (samples.yml)
//   See also   : README.md - how to run
// -----------------------------------------------------------------------------

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Extensions;
using Samples.App.Infrastructure;
using Samples.App.Scenarios;

var libraryAssembly = typeof(IResilienceExecutor).Assembly;
var libraryVersion =
    libraryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? libraryAssembly.GetName().Version?.ToString()
    ?? "unknown";
var plusIndex = libraryVersion.IndexOf('+');
if (plusIndex > 0) libraryVersion = libraryVersion.Substring(0, plusIndex);

var runtimeVersion = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

ConsoleRenderer.RenderBanner(System.Console.Out, libraryVersion, runtimeVersion);

var capturingSink = new CapturingSink();

var services = new ServiceCollection();
services.AddPortfolioResilience(r => r
    .AddLogSink(capturingSink)
    .AddPolicy("retry-policy", p => { p.Retry.MaxAttempts = 3; p.Retry.BaseDelayMs = 10; })
    .AddPolicy("circuit-policy", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.Circuit.FailureThreshold = 2;
        p.Circuit.OpenDurationSeconds = 1;
        p.Circuit.SuccessThreshold = 1;
    })
    .AddPolicy("timeout-policy", p => { p.Retry.MaxAttempts = 1; p.Timeout.TimeoutMs = 100; })
    .AddPolicy("rate-limiter-token-bucket", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.RateLimiter.Enabled = true;
        p.RateLimiter.Strategy = RateLimitStrategy.TokenBucket;
        p.RateLimiter.PermitLimit = 3;
        p.RateLimiter.WindowSeconds = 60;
    })
    .AddPolicy("rate-limiter-sliding-window", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.RateLimiter.Enabled = true;
        p.RateLimiter.Strategy = RateLimitStrategy.SlidingWindow;
        p.RateLimiter.PermitLimit = 3;
        p.RateLimiter.WindowSeconds = 60;
    })
    .AddPolicy("rate-limiter-fixed-window", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.RateLimiter.Enabled = true;
        p.RateLimiter.Strategy = RateLimitStrategy.FixedWindow;
        p.RateLimiter.PermitLimit = 3;
        p.RateLimiter.WindowSeconds = 60;
    })
    .AddPolicy("rate-limiter-concurrency", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.RateLimiter.Enabled = true;
        p.RateLimiter.Strategy = RateLimitStrategy.ConcurrencyLimit;
        p.RateLimiter.PermitLimit = 3;
    })
    .AddPolicy("bulkhead-policy", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.Bulkhead.Enabled = true;
        p.Bulkhead.MaxConcurrency = 2;
        p.Bulkhead.MaxQueue = 1;
        p.Bulkhead.QueueTimeoutMs = 5000;
    })
    .AddPolicy("hedging-disabled-policy", p => { p.Retry.MaxAttempts = 1; p.Timeout.TimeoutMs = 5000; })
    .AddPolicy("hedging-policy", p =>
    {
        p.Retry.MaxAttempts = 1;
        p.Timeout.TimeoutMs = 5000;
        p.Hedging.Enabled = true;
        p.Hedging.MaxAttempts = 2;
        p.Hedging.DelayMs = 50;
        p.Hedging.CancelOnSuccess = true;
    })
    .AddPolicy("idempotency-policy", p => { p.Retry.MaxAttempts = 3; p.Retry.BaseDelayMs = 10; })
    .AddPolicy("scrub-policy", p => { p.Retry.MaxAttempts = 1; p.Logging.ScrubSensitiveData = true; })
    .AddPolicy("no-scrub-policy", p => { p.Retry.MaxAttempts = 1; p.Logging.ScrubSensitiveData = false; })
    .AddPolicy("budget-unbounded-policy", p =>
    {
        p.Retry.MaxAttempts = 5;
        p.Retry.BaseDelayMs = 100;
        p.Circuit.FailureThreshold = 100;
        p.Timeout.TimeoutMs = 5000;
    })
    .AddPolicy("budget-bounded-policy", p =>
    {
        p.Retry.MaxAttempts = 5;
        p.Retry.BaseDelayMs = 100;
        p.Circuit.FailureThreshold = 100;
        p.Timeout.TimeoutMs = 5000;
    }));

var provider = services.BuildServiceProvider();
var executor = provider.GetRequiredService<IResilienceExecutor>();
var monitor = provider.GetService<ICircuitBreakerMonitor>();

var results = new List<ScenarioResult>();
const int totalScenarios = 10;

var result01 = await ScenarioRunner.RunAsync("01", "Retry",
    ct => RetryScenario.RunAsync(executor, capturingSink, ct));
results.Add(result01);
ConsoleRenderer.RenderScenario(System.Console.Out, result01, totalScenarios);

var result02 = await ScenarioRunner.RunAsync("02", "Circuit",
    ct => CircuitScenario.RunAsync(executor, capturingSink, monitor, ct));
results.Add(result02);
ConsoleRenderer.RenderScenario(System.Console.Out, result02, totalScenarios);

var result03 = await ScenarioRunner.RunAsync("03", "Timeout",
    ct => TimeoutScenario.RunAsync(executor, capturingSink, ct));
results.Add(result03);
ConsoleRenderer.RenderScenario(System.Console.Out, result03, totalScenarios);

var result04 = await ScenarioRunner.RunAsync("04", "RateLimiter",
    ct => RateLimiterScenario.RunAsync(executor, capturingSink, ct));
results.Add(result04);
ConsoleRenderer.RenderScenario(System.Console.Out, result04, totalScenarios);

var result05 = await ScenarioRunner.RunAsync("05", "Bulkhead",
    ct => BulkheadScenario.RunAsync(executor, capturingSink, ct));
results.Add(result05);
ConsoleRenderer.RenderScenario(System.Console.Out, result05, totalScenarios);

var result06 = await ScenarioRunner.RunAsync("06", "Hedging",
    ct => HedgingScenario.RunAsync(executor, capturingSink, ct));
results.Add(result06);
ConsoleRenderer.RenderScenario(System.Console.Out, result06, totalScenarios);

var result07 = await ScenarioRunner.RunAsync("07", "Idempotency",
    ct => IdempotencyScenario.RunAsync(executor, capturingSink, ct));
results.Add(result07);
ConsoleRenderer.RenderScenario(System.Console.Out, result07, totalScenarios);

var result08 = await ScenarioRunner.RunAsync("08", "Scrubbing",
    ct => ScrubbingScenario.RunAsync(executor, capturingSink, ct));
results.Add(result08);
ConsoleRenderer.RenderScenario(System.Console.Out, result08, totalScenarios);

var result09 = await ScenarioRunner.RunAsync("09", "TimeBudget",
    ct => TimeBudgetScenario.RunAsync(executor, capturingSink, ct));
results.Add(result09);
ConsoleRenderer.RenderScenario(System.Console.Out, result09, totalScenarios);

var result10 = await ScenarioRunner.RunAsync("10", "Combined",
    ct => CombinedScenario.RunAsync(capturingSink, ct));
results.Add(result10);
ConsoleRenderer.RenderScenario(System.Console.Out, result10, totalScenarios);

ConsoleRenderer.RenderSummary(System.Console.Out, results);

return results.All(r => r.Passed) ? 0 : 1;