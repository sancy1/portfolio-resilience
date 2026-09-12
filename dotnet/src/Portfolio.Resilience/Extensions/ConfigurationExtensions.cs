// filepath: src/Portfolio.Resilience/Extensions/ConfigurationExtensions.cs
// layer: Extensions | package: Portfolio.Resilience | since: v0.3.0
// purpose: Binds ResilienceOptions from IConfiguration (appsettings.json + environment variables).
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Implements : n/a (static extension class)
//   Depends on : IConfiguration, ResilienceBuilder, PolicyDefinition, RetryOptions, CircuitOptions, TimeoutOptions
//   Used by    : services that prefer JSON/ENV config over fluent C# registration
//   See also   : docs/executor.md, README.md
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.Extensions.Configuration;
using Portfolio.Resilience.Configuration;

namespace Portfolio.Resilience.Extensions;

/// <summary>
/// Loads resilience configuration from <see cref="IConfiguration"/>. Supports
/// both JSON (appsettings.json) and environment-variable configuration.
/// </summary>
/// <remarks>
/// Configuration layout (default section name "Resilience"):
/// <code>
/// Resilience:
///   DefaultPolicy:
///     Retry:
///       MaxAttempts: 3
///       BaseDelayMs: 500
///     Circuit:
///       FailureThreshold: 5
///     Timeout:
///       TimeoutMs: 10000
///   Policies:
///     auth-service:
///       Retry:
///         MaxAttempts: 2
///       Timeout:
///         TimeoutMs: 5000
/// </code>
/// Environment variable form uses double underscore as the separator:
/// <c>Resilience__Policies__auth-service__Timeout__TimeoutMs=5000</c>
/// </remarks>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Loads policies and the default policy into the builder from configuration.
    /// Existing policies in the builder are preserved; configuration entries override on collision.
    /// </summary>
    public static ResilienceBuilder LoadFromConfiguration(
        this ResilienceBuilder builder,
        IConfiguration configuration,
        string sectionName = "Resilience")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new ArgumentException("Section name must be non-empty.", nameof(sectionName));
        }

        var section = configuration.GetSection(sectionName);
        if (!section.Exists())
        {
            return builder; // Nothing to load — no-op.
        }

        // --- Default policy ---
        var defaultSection = section.GetSection("DefaultPolicy");
        if (defaultSection.Exists())
        {
            LoadPolicyInto(builder.Options.DefaultPolicy, defaultSection);
        }

        // --- Named policies ---
        var policiesSection = section.GetSection("Policies");
        foreach (var child in policiesSection.GetChildren())
        {
            var policy = new PolicyDefinition { Name = child.Key };
            LoadPolicyInto(policy, child);
            builder.Options.Policies[child.Key] = policy;
        }

        return builder;
    }
    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private static void LoadPolicyInto(PolicyDefinition target, IConfiguration section)
    {
        // Retry
        var retry = section.GetSection("Retry");
        if (retry.Exists())
        {
            target.Retry.MaxAttempts      = retry.GetValue<int?>("MaxAttempts")      ?? target.Retry.MaxAttempts;
            target.Retry.BaseDelayMs      = retry.GetValue<int?>("BaseDelayMs")      ?? target.Retry.BaseDelayMs;
            target.Retry.MaxDelayMs       = retry.GetValue<int?>("MaxDelayMs")       ?? target.Retry.MaxDelayMs;
            target.Retry.JitterRatio      = retry.GetValue<double?>("JitterRatio")   ?? target.Retry.JitterRatio;
            target.Retry.RetryOnPermanent = retry.GetValue<bool?>("RetryOnPermanent") ?? target.Retry.RetryOnPermanent;
        }

        // Circuit
        var circuit = section.GetSection("Circuit");
        if (circuit.Exists())
        {
            target.Circuit.FailureThreshold     = circuit.GetValue<int?>("FailureThreshold")     ?? target.Circuit.FailureThreshold;
            target.Circuit.OpenDurationSeconds  = circuit.GetValue<int?>("OpenDurationSeconds")  ?? target.Circuit.OpenDurationSeconds;
            target.Circuit.SuccessThreshold     = circuit.GetValue<int?>("SuccessThreshold")     ?? target.Circuit.SuccessThreshold;
            target.Circuit.OnlyCountTransient   = circuit.GetValue<bool?>("OnlyCountTransient")  ?? target.Circuit.OnlyCountTransient;
        }

        // Timeout
        var timeout = section.GetSection("Timeout");
        if (timeout.Exists())
        {
            target.Timeout.TimeoutMs = timeout.GetValue<int?>("TimeoutMs") ?? target.Timeout.TimeoutMs;
        }

        // Fallback
        var fallback = section.GetSection("Fallback");
        if (fallback.Exists())
        {
            target.Fallback.Enabled = fallback.GetValue<bool?>("Enabled") ?? target.Fallback.Enabled;
            target.Fallback.Reason  = fallback.GetValue<string?>("Reason") ?? target.Fallback.Reason;
        }

        // Logging
        var logging = section.GetSection("Logging");
        if (logging.Exists())
        {
            target.Logging.EmitCallStarted     = logging.GetValue<bool?>("EmitCallStarted")     ?? target.Logging.EmitCallStarted;
            target.Logging.EmitRetryAttempted  = logging.GetValue<bool?>("EmitRetryAttempted")  ?? target.Logging.EmitRetryAttempted;
            target.Logging.EmitCallSucceeded   = logging.GetValue<bool?>("EmitCallSucceeded")   ?? target.Logging.EmitCallSucceeded;
            target.Logging.EmitCallFailed      = logging.GetValue<bool?>("EmitCallFailed")      ?? target.Logging.EmitCallFailed;
            target.Logging.EmitCircuitEvents   = logging.GetValue<bool?>("EmitCircuitEvents")   ?? target.Logging.EmitCircuitEvents;
            target.Logging.EmitFallbackUsed    = logging.GetValue<bool?>("EmitFallbackUsed")    ?? target.Logging.EmitFallbackUsed;
            target.Logging.EmitTimeoutBreached = logging.GetValue<bool?>("EmitTimeoutBreached") ?? target.Logging.EmitTimeoutBreached;
        }
    }
}
