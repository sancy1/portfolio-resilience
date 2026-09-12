# filepath: audit.ps1
# package: Portfolio.Resilience
# purpose: Structural audit — verifies every source file has the standard header + key symbols.

param(
    [string]$Root = "C:\Users\HP\Desktop\portfolio-resilience\dotnet"
)

Write-Host "=================================================="
Write-Host " Source file audit (excluding obj/bin)"
Write-Host "=================================================="

$files = Get-ChildItem "$Root\src\Portfolio.Resilience" -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' }

$bad = @()
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Root.Length + 1)
    $head = Get-Content $f.FullName -TotalCount 14

    $hasFilepath = ($head | Where-Object { $_ -match '^// filepath:' }).Count -gt 0
    $hasLayer    = ($head | Where-Object { $_ -match '^// layer:' }).Count -gt 0
    $hasPurpose  = ($head | Where-Object { $_ -match '^// purpose:' }).Count -gt 0
    $hasRels     = ($head | Where-Object { $_ -match '^// RELATIONSHIPS' }).Count -gt 0
    $lineCount   = (Get-Content $f.FullName | Measure-Object -Line).Lines

    $status = if ($hasFilepath -and $hasLayer -and $hasPurpose -and $hasRels) { "OK" } else { "MISSING" }
    Write-Host ("  {0,-8} {1,5} lines  {2}" -f $status, $lineCount, $rel)
    if ($status -eq "MISSING") { $bad += $rel }
}

Write-Host ""
Write-Host ("  Source files: {0}   Missing headers: {1}" -f $files.Count, $bad.Count)

Write-Host ""
Write-Host "=================================================="
Write-Host " Symbol presence audit (key public API)"
Write-Host "=================================================="

$expectations = @(
    @{ File = "src\Portfolio.Resilience\Correlation\CorrelationContext.cs";                 Symbols = @("public static class CorrelationContext", "public static IDisposable Push", "public static string NewId") },
    @{ File = "src\Portfolio.Resilience\Correlation\AsyncLocalCorrelationAccessor.cs";     Symbols = @("public sealed class AsyncLocalCorrelationAccessor", "public string? Current", "public IDisposable Push") },
    @{ File = "src\Portfolio.Resilience\Sinks\ConsoleLogSink.cs";                           Symbols = @("public sealed class ConsoleLogSink", "public void Emit") },
    @{ File = "src\Portfolio.Resilience\Sinks\FileLogSink.cs";                              Symbols = @("public sealed class FileLogSink", "public void Emit", "public void Dispose") },
    @{ File = "src\Portfolio.Resilience\Sinks\NullLogSink.cs";                              Symbols = @("public sealed class NullLogSink", "public static readonly NullLogSink Instance", "public void Emit") },
    @{ File = "src\Portfolio.Resilience\Sinks\CompositeLogSink.cs";                         Symbols = @("public sealed class CompositeLogSink", "public void Emit", "public int Count") },
    @{ File = "src\Portfolio.Resilience\Sinks\InMemoryMetricSink.cs";                       Symbols = @("public sealed class InMemoryMetricSink", "public void RecordCall", "public void BeginInFlight", "public void EndInFlight", "public IReadOnlyCollection<LatencySnapshot> Snapshot", "public LatencySnapshot? Get") },
    @{ File = "src\Portfolio.Resilience\Sinks\CompositeMetricSink.cs";                      Symbols = @("public sealed class CompositeMetricSink", "public void RecordCall", "public int Count") },
    @{ File = "src\Portfolio.Resilience\Errors\ErrorClassifier.cs";                         Symbols = @("public sealed class ErrorClassifier", "public ResilienceErrorCategory Classify") },
    @{ File = "src\Portfolio.Resilience\Policies\RetryPolicyBuilder.cs";                    Symbols = @("public sealed class RetryPolicyBuilder", "Task<T> ExecuteAsync<T>", "public TimeSpan CalculateDelay") },
    @{ File = "src\Portfolio.Resilience\Policies\TimeoutPolicyBuilder.cs";                  Symbols = @("public sealed class TimeoutPolicyBuilder", "Task<T> ExecuteAsync<T>") },
    @{ File = "src\Portfolio.Resilience\Policies\CircuitPolicyBuilder.cs";                  Symbols = @("public sealed class CircuitPolicyBuilder", "Task<T> ExecuteAsync<T>", "public IReadOnlyCollection<CircuitSnapshot> Snapshot", "public CircuitSnapshot? Get") },
    @{ File = "src\Portfolio.Resilience\Policies\CompositePolicyBuilder.cs";                Symbols = @("public sealed class CompositePolicyBuilder", "Task<T> ExecuteAsync<T>", "public CircuitPolicyBuilder Circuit") },
    @{ File = "src\Portfolio.Resilience\Implementation\ResilienceEventEmitter.cs";          Symbols = @("public sealed class ResilienceEventEmitter", "public void Emit", "public void EmitCallStarted", "public void EmitCallSucceeded", "public void EmitCallFailed", "public void EmitRetryAttempted", "public void EmitCircuitOpened", "public void EmitCircuitClosed", "public void EmitCircuitHalfOpened", "public void EmitFallbackUsed", "public void EmitTimeoutBreached") },
    @{ File = "src\Portfolio.Resilience\Implementation\ResiliencePolicyRegistry.cs";        Symbols = @("public sealed class ResiliencePolicyRegistry", "public PolicyDefinition Resolve", "public IReadOnlyCollection<string> KnownPolicies") },
    @{ File = "src\Portfolio.Resilience\Implementation\LatencyTracker.cs";                  Symbols = @("public sealed class LatencyTracker", "public IReadOnlyCollection<LatencySnapshot> Snapshot", "public LatencySnapshot? Get") },
    @{ File = "src\Portfolio.Resilience\Implementation\CircuitBreakerMonitor.cs";           Symbols = @("public sealed class CircuitBreakerMonitor", "public IReadOnlyCollection<CircuitSnapshot> Snapshot", "public CircuitSnapshot? Get", "public int SourceCount") },
    @{ File = "src\Portfolio.Resilience\Implementation\ResilienceExecutor.cs";              Symbols = @("public sealed class ResilienceExecutor", "Task<T> ExecuteAsync<T>", "Task ExecuteAsync") },
    @{ File = "src\Portfolio.Resilience\Extensions\ServiceCollectionExtensions.cs";         Symbols = @("public static class ServiceCollectionExtensions", "public static IServiceCollection AddPortfolioResilience") },
    @{ File = "src\Portfolio.Resilience\Extensions\ConfigurationExtensions.cs";             Symbols = @("public static class ConfigurationExtensions", "public static ResilienceBuilder LoadFromConfiguration") },
    @{ File = "src\Portfolio.Resilience\Configuration\ResilienceBuilder.cs";                Symbols = @("public sealed class ResilienceBuilder", "public ResilienceOptions Options", "public ResilienceBuilder AddLogSink", "public ResilienceBuilder AddMetricSink", "public ResilienceBuilder AddPolicy", "public ResilienceBuilder UseDefaultPolicy") }
)

$problems = @()
foreach ($e in $expectations) {
    $fp = Join-Path $Root $e.File
    if (-not (Test-Path $fp)) { Write-Host ("  MISSING FILE: {0}" -f $e.File) -ForegroundColor Red; $problems += $e.File; continue }

    $c = Get-Content $fp -Raw
    $missing = @()
    foreach ($s in $e.Symbols) {
        if ($c -notmatch [regex]::Escape($s)) { $missing += $s }
    }

    if ($missing.Count -eq 0) { Write-Host ("  OK         {0}" -f $e.File) }
    else {
        Write-Host ("  INCOMPLETE {0}" -f $e.File) -ForegroundColor Yellow
        $missing | ForEach-Object { Write-Host ("      missing: {0}" -f $_) }
        $problems += $e.File
    }
}

Write-Host ""
if ($problems.Count -eq 0) { Write-Host "  ALL FILES COMPLETE." -ForegroundColor Green }
else { Write-Host ("  {0} FILES NEED ATTENTION." -f $problems.Count) -ForegroundColor Yellow }
