// filepath: src/Portfolio.Resilience.OpenTelemetry/OpenTelemetryMetricSink.cs
// layer: OpenTelemetry | package: Portfolio.Resilience.OpenTelemetry | since: v0.7.0
// purpose: Forwards IMetricSink samples to OpenTelemetry as histograms and counters.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Implements : IMetricSink
//   Depends on : System.Diagnostics.Metrics, IMetricSink
//   Used by    : ServiceCollectionExtensions (user opt-in via AddOpenTelemetrySinks)
//   See also   : docs/opentelemetry.md, SPEC.md section 15
// -----------------------------------------------------------------------------

using System.Diagnostics.Metrics;
using Portfolio.Resilience.Abstractions;

namespace Portfolio.Resilience.OpenTelemetry;

/// <summary>
/// An <see cref="IMetricSink"/> that records every call as OpenTelemetry
/// metrics: a duration histogram and per-outcome counters.
/// </summary>
/// <remarks>
/// <para>
/// This sink uses the <see cref="System.Diagnostics.Metrics"/> API - the same
/// API that <c>System.Diagnostics.Metrics.Meter</c> providers and the
/// <c>OpenTelemetry</c> package consume. Instruments are created once in the
/// constructor and reused for every recorded call.
/// </para>
/// <para>
/// Three instruments are created:
/// <list type="bullet">
///   <item><c>resilience.call.duration_ms</c> - histogram of call durations.</item>
///   <item><c>resilience.call.succeeded_total</c> - counter of successful calls.</item>
///   <item><c>resilience.call.failed_total</c> - counter of failed calls.</item>
/// </list>
/// All instruments are tagged with <c>policy_name</c>; counters additionally
/// carry the <c>attempts</c> tag.
/// </para>
/// <para>
/// <b>In-flight tracking is not exported.</b> The <see cref="IMetricSink"/>
/// contract only exposes <see cref="RecordCall"/>; the in-flight gauges
/// maintained by <c>InMemoryMetricSink</c> are not part of the interface and
/// therefore not bridged here. See <c>docs/opentelemetry.md</c> for details.
/// </para>
/// <para>
/// Thread-safe. Instrument recording is lock-free.
/// </para>
/// </remarks>
public sealed class OpenTelemetryMetricSink : IMetricSink
{
    /// <summary>Default meter name used when the caller does not supply one.</summary>
    public const string DefaultMeterName = "Portfolio.Resilience";

    private readonly Histogram<double> _durationHistogram;
    private readonly Counter<long> _succeededCounter;
    private readonly Counter<long> _failedCounter;

    /// <summary>
    /// Creates a metric sink that writes to instruments on
    /// <paramref name="meter"/>.
    /// </summary>
    /// <param name="meter">
    /// The meter that owns the instruments. Create one per service and share it.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="meter"/> is null.</exception>
    public OpenTelemetryMetricSink(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        _durationHistogram = meter.CreateHistogram<double>(
            name: "resilience.call.duration_ms",
            unit: "ms",
            description: "Duration of calls through the resilience pipeline.");

        _succeededCounter = meter.CreateCounter<long>(
            name: "resilience.call.succeeded_total",
            unit: "{call}",
            description: "Number of calls that completed successfully.");

        _failedCounter = meter.CreateCounter<long>(
            name: "resilience.call.failed_total",
            unit: "{call}",
            description: "Number of calls that failed after all pipeline layers.");
    }

    /// <summary>
    /// Creates a metric sink using a new <see cref="Meter"/> with the default
    /// name. Convenience overload for callers who do not need to share a meter.
    /// </summary>
    public OpenTelemetryMetricSink()
        : this(new Meter(DefaultMeterName))
    {
    }

    /// <inheritdoc />
    public void RecordCall(string policyName, TimeSpan duration, bool success, int attempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        var durationMs = duration.TotalMilliseconds;

        // Histogram: duration per call, tagged with policy name and outcome.
        _durationHistogram.Record(
            durationMs,
            new KeyValuePair<string, object?>("policy_name", policyName),
            new KeyValuePair<string, object?>("success", success));

        // Counters: success/failure totals, tagged with policy name and attempts.
        var counterTags = new[]
        {
            new KeyValuePair<string, object?>("policy_name", policyName),
            new KeyValuePair<string, object?>("attempts", attempts)
        };

        if (success)
        {
            _succeededCounter.Add(1, counterTags);
        }
        else
        {
            _failedCounter.Add(1, counterTags);
        }
    }
}
