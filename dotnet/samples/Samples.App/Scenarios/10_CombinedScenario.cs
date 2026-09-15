// filepath: dotnet/samples/Samples.App/Scenarios/10_CombinedScenario.cs
// layer: Scenarios | package: Samples.App | since: n/a
// purpose: Demonstrates the payment-safe preset with all layers composed in the safe order (v0.8.0)
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : n/a (static class)
//   Depends on : ResiliencePipeline.WithPaymentSafeDefaults, PolicyDefinition, IdempotencyContext
//   Used by    : Program.cs, Integration tests
//   See also   : docs/composition.md, docs/idempotency.md, README.md - Findings
// -----------------------------------------------------------------------------

using Portfolio.Resilience.Configuration;
using Portfolio.Resilience.Correlation;
using Portfolio.Resilience.Policies;
using Samples.App.Infrastructure;

namespace Samples.App.Scenarios;

/// <summary>
/// Scenario 10 - Combined. Uses <see cref="ResiliencePipeline.WithPaymentSafeDefaults"/>
/// to build a pipeline with the payment-safe layer order:
/// RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout -> Operation.
/// Asserts the order is enforced, runs a failing charge operation that succeeds on
/// retry, and confirms the idempotency key is preserved across attempts.
/// </summary>
public static class CombinedScenario
{
    /// <summary>Runs the scenario and returns a one-line success message, or throws on failure.</summary>
    public static async Task<string> RunAsync(
        CapturingSink sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);

        sink.Clear();

        // --- Build the payment-safe pipeline ---
        var pipeline = ResiliencePipeline.WithPaymentSafeDefaults();

        // --- Assert the layer order ---
        var layers = pipeline.Layers;
        if (layers.Count != 6)
        {
            throw new InvalidOperationException(
                $"expected 6 layers in the payment-safe pipeline, got {layers.Count}");
        }

        var expectedOrder = new[]
        {
            nameof(RateLimiterPolicyBuilder),
            nameof(BulkheadPolicyBuilder),
            nameof(CircuitPolicyBuilder),
            nameof(HedgingPolicyBuilder),
            nameof(RetryPolicyBuilder),
            nameof(TimeoutPolicyBuilder),
        };

        for (var i = 0; i < expectedOrder.Length; i++)
        {
            var actual = layers[i].GetType().Name;
            if (actual != expectedOrder[i])
            {
                throw new InvalidOperationException(
                    $"layer {i} should be {expectedOrder[i]}, got {actual}");
            }
        }

        // --- Configure the policy definition ---
        var definition = new PolicyDefinition
        {
            Name = "payment-safe-policy",
        };
        definition.RateLimiter.Enabled = true;
        definition.RateLimiter.Strategy = RateLimitStrategy.TokenBucket;
        definition.RateLimiter.PermitLimit = 100;
        definition.RateLimiter.WindowSeconds = 60;
        definition.Bulkhead.Enabled = true;
        definition.Bulkhead.MaxConcurrency = 10;
        definition.Bulkhead.MaxQueue = 5;
        definition.Bulkhead.QueueTimeoutMs = 2000;
        definition.Circuit.FailureThreshold = 5;
        definition.Circuit.OpenDurationSeconds = 30;
        definition.Circuit.SuccessThreshold = 1;
        // Hedging is left disabled - it is not safe on writes without an idempotency key.
        definition.Hedging.Enabled = false;
        definition.Retry.MaxAttempts = 3;
        definition.Retry.BaseDelayMs = 10;
        definition.Timeout.TimeoutMs = 5000;

        // --- Run the charge operation. ---
        var attempts = 0;
        var keysObserved = new List<string?>();

        var result = await pipeline.ExecuteAsync<int>(
            policyName: "payment-safe-policy",
            operation: _ =>
            {
                attempts++;
                keysObserved.Add(IdempotencyContext.CurrentKey);
                if (attempts == 1)
                {
                    throw new TimeoutException("simulated charge gateway timeout");
                }
                return Task.FromResult(200);
            },
            definition: definition,
            ct: ct);

        if (result != 200)
        {
            throw new InvalidOperationException(
                $"expected charge to return 200, got {result}");
        }
        if (attempts != 2)
        {
            throw new InvalidOperationException(
                $"expected exactly 2 charge invocations (1 fail + 1 retry), got {attempts}");
        }

        // When the pipeline is driven directly (not through IResilienceExecutor),
        // the caller is responsible for setting the ambient idempotency key. The
        // scenario only asserts that both attempts observed the same value - whether
        // that value is a caller-supplied key or null.
        if (keysObserved.Count == 2 && keysObserved[0] != keysObserved[1])
        {
            throw new InvalidOperationException(
                $"expected the same idempotency key across attempts, " +
                $"got '{keysObserved[0]}' and '{keysObserved[1]}'");
        }

        return $"Payment-safe pipeline composed 6 layers in order " +
               $"(RateLimiter -> Bulkhead -> Circuit -> Hedging -> Retry -> Timeout). " +
               $"Charge succeeded on attempt 2 of 3.";
    }
}