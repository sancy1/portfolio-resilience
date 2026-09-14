// filepath: tests/Portfolio.Resilience.OpenTelemetry.Tests/OpenTelemetryMetricSinkTests.cs
// layer: Tests | package: Portfolio.Resilience.OpenTelemetry.Tests | since: v0.7.0
// purpose: Verifies the OTel metric sink records durations and outcomes on the expected instruments.
// -----------------------------------------------------------------------------
// RELATIONSHIPS
//   Tests      : OpenTelemetryMetricSink (OpenTelemetry/)
//   Depends on : Meter, MeterListener, xUnit, FluentAssertions
//   See also   : docs/opentelemetry.md, SPEC.md section 8
// -----------------------------------------------------------------------------

using System.Diagnostics.Metrics;
using FluentAssertions;
using Portfolio.Resilience.OpenTelemetry;
using Xunit;

namespace Portfolio.Resilience.OpenTelemetry.Tests;

public sealed class OpenTelemetryMetricSinkTests
{
    // ------------------------------------------------------------------------
    // Test double: captures instrument recordings from a Meter
    // ------------------------------------------------------------------------

    private sealed class Measurement
    {
        public string InstrumentName { get; init; } = string.Empty;
        public string InstrumentUnit { get; init; } = string.Empty;
        public object? Value { get; init; }
        public Dictionary<string, object?> Tags { get; init; } = new();
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        public List<Measurement> Measurements { get; } = new();

        public MetricCapture()
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                Measurements.Add(new Measurement
                {
                    InstrumentName = instrument.Name,
                    InstrumentUnit = instrument.Unit ?? string.Empty,
                    Value = value,
                    Tags = TagsToDict(tags)
                });
            });
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                Measurements.Add(new Measurement
                {
                    InstrumentName = instrument.Name,
                    InstrumentUnit = instrument.Unit ?? string.Empty,
                    Value = value,
                    Tags = TagsToDict(tags)
                });
            });
            _listener.Start();
        }

        private static Dictionary<string, object?> TagsToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kv in tags) dict[kv.Key] = kv.Value;
            return dict;
        }

        public void Dispose() => _listener.Dispose();
    }

    // ------------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsOnNullMeter()
    {
        Action act = () => new OpenTelemetryMetricSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RecordCall_ThrowsOnNullOrWhitespacePolicyName()
    {
        using var meter = new Meter("test");
        var sink = new OpenTelemetryMetricSink(meter);

        Action act1 = () => sink.RecordCall(null!, TimeSpan.FromMilliseconds(1), true, 1);
        Action act2 = () => sink.RecordCall("  ", TimeSpan.FromMilliseconds(1), true, 1);

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    // ------------------------------------------------------------------------
    // Duration histogram
    // ------------------------------------------------------------------------

    [Fact]
    public void RecordCall_RecordsDurationOnHistogram()
    {
        using var meter = new Meter("test");
        using var capture = new MetricCapture();
        var sink = new OpenTelemetryMetricSink(meter);

        sink.RecordCall("auth-service", TimeSpan.FromMilliseconds(42.5), success: true, attempts: 1);

        var histogram = capture.Measurements
            .Single(m => m.InstrumentName == "resilience.call.duration_ms");

        histogram.Value.Should().Be(42.5);
        histogram.InstrumentUnit.Should().Be("ms");
        histogram.Tags["policy_name"].Should().Be("auth-service");
        histogram.Tags["success"].Should().Be(true);
    }

    // ------------------------------------------------------------------------
    // Success and failure counters
    // ------------------------------------------------------------------------

    [Fact]
    public void RecordCall_Success_IncrementsSucceededCounter()
    {
        using var meter = new Meter("test");
        using var capture = new MetricCapture();
        var sink = new OpenTelemetryMetricSink(meter);

        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), success: true, attempts: 2);

        var succeeded = capture.Measurements
            .Where(m => m.InstrumentName == "resilience.call.succeeded_total")
            .ToArray();

        succeeded.Should().HaveCount(1);
        succeeded[0].Value.Should().Be(1L);
        succeeded[0].Tags["policy_name"].Should().Be("p");
        succeeded[0].Tags["attempts"].Should().Be(2);

        // No failure counter recorded
        capture.Measurements.Any(m => m.InstrumentName == "resilience.call.failed_total")
            .Should().BeFalse();
    }

    [Fact]
    public void RecordCall_Failure_IncrementsFailedCounter()
    {
        using var meter = new Meter("test");
        using var capture = new MetricCapture();
        var sink = new OpenTelemetryMetricSink(meter);

        sink.RecordCall("p", TimeSpan.FromMilliseconds(10), success: false, attempts: 3);

        var failed = capture.Measurements
            .Where(m => m.InstrumentName == "resilience.call.failed_total")
            .ToArray();

        failed.Should().HaveCount(1);
        failed[0].Value.Should().Be(1L);
        failed[0].Tags["policy_name"].Should().Be("p");
        failed[0].Tags["attempts"].Should().Be(3);

        // No success counter recorded
        capture.Measurements.Any(m => m.InstrumentName == "resilience.call.succeeded_total")
            .Should().BeFalse();
    }

    // ------------------------------------------------------------------------
    // Multiple recordings accumulate
    // ------------------------------------------------------------------------

    [Fact]
    public void RecordCall_MultipleCalls_CaptureEveryMeasurement()
    {
        using var meter = new Meter("test");
        using var capture = new MetricCapture();
        var sink = new OpenTelemetryMetricSink(meter);

        sink.RecordCall("p", TimeSpan.FromMilliseconds(1), success: true,  attempts: 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(2), success: true,  attempts: 1);
        sink.RecordCall("p", TimeSpan.FromMilliseconds(3), success: false, attempts: 2);

        var histograms = capture.Measurements.Where(m => m.InstrumentName == "resilience.call.duration_ms").ToArray();
        var succeeded = capture.Measurements.Where(m => m.InstrumentName == "resilience.call.succeeded_total").ToArray();
        var failed = capture.Measurements.Where(m => m.InstrumentName == "resilience.call.failed_total").ToArray();

        histograms.Should().HaveCount(3);
        succeeded.Should().HaveCount(2);
        failed.Should().HaveCount(1);
    }

    // ------------------------------------------------------------------------
    // Parameterless constructor
    // ------------------------------------------------------------------------

    [Fact]
    public void ParameterlessConstructor_UsesDefaultMeterName()
    {
        // Just verifies it doesn't throw and can record.
        var sink = new OpenTelemetryMetricSink();
        sink.RecordCall("p", TimeSpan.FromMilliseconds(1), success: true, attempts: 1);
    }

    // ------------------------------------------------------------------------
    // Distinct policies produce distinct tags
    // ------------------------------------------------------------------------

    [Fact]
    public void RecordCall_DistinctPolicies_TaggedSeparately()
    {
        using var meter = new Meter("test");
        using var capture = new MetricCapture();
        var sink = new OpenTelemetryMetricSink(meter);

        sink.RecordCall("policy-a", TimeSpan.FromMilliseconds(1), true, 1);
        sink.RecordCall("policy-b", TimeSpan.FromMilliseconds(1), true, 1);

        var policyTags = capture.Measurements
            .Where(m => m.InstrumentName == "resilience.call.duration_ms")
            .Select(m => (string?)m.Tags["policy_name"])
            .ToArray();

        policyTags.Should().BeEquivalentTo(new[] { "policy-a", "policy-b" });
    }
}
