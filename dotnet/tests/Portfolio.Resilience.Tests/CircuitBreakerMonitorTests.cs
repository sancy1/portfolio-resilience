// filepath: tests/Portfolio.Resilience.Tests/CircuitBreakerMonitorTests.cs
// layer: Tests | package: Portfolio.Resilience.Tests | since: v0.3.0
// purpose: Verifies CircuitBreakerMonitor aggregates snapshots and resolves names across sources.
// ─────────────────────────────────────────────────────────────────────────────
// RELATIONSHIPS
//   Tests      : CircuitBreakerMonitor (Implementation/CircuitBreakerMonitor.cs)
//   Depends on : ICircuitBreakerMonitor, CircuitSnapshot, xUnit, FluentAssertions
//   See also   : docs/circuit-breaker.md
// ─────────────────────────────────────────────────────────────────────────────

using FluentAssertions;
using Portfolio.Resilience.Abstractions;
using Portfolio.Resilience.Implementation;
using Xunit;

namespace Portfolio.Resilience.Tests;

public sealed class CircuitBreakerMonitorTests
{
    private sealed class FakeMonitor : ICircuitBreakerMonitor
    {
        private readonly Dictionary<string, CircuitSnapshot> _byName = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string policyName, CircuitState state, int failures = 0)
        {
            _byName[policyName] = new CircuitSnapshot(
                PolicyName: policyName,
                State: state,
                ConsecutiveFailures: failures,
                OpenedAtUtc: null,
                NextProbeAtUtc: null);
        }

        public IReadOnlyCollection<CircuitSnapshot> Snapshot() => _byName.Values.ToArray();

        public CircuitSnapshot? Get(string policyName)
            => _byName.TryGetValue(policyName, out var s) ? s : null;
    }

    [Fact]
    public void Constructor_ThrowsOnNullSources()
    {
        Action act = () => new CircuitBreakerMonitor(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_WithEmptySources_ReturnsEmptySnapshot()
    {
        var monitor = new CircuitBreakerMonitor(Array.Empty<ICircuitBreakerMonitor>());

        monitor.SourceCount.Should().Be(0);
        monitor.Snapshot().Should().BeEmpty();
        monitor.Get("anything").Should().BeNull();
    }

    [Fact]
    public void Constructor_FiltersNullSources()
    {
        var a = new FakeMonitor();
        a.Add("p1", CircuitState.Closed);

        var monitor = new CircuitBreakerMonitor(new ICircuitBreakerMonitor[]
        {
            a, null!, null!
        });

        monitor.SourceCount.Should().Be(1);
    }

    [Fact]
    public void Get_ThrowsOnNullOrWhitespacePolicyName()
    {
        var monitor = new CircuitBreakerMonitor(Array.Empty<ICircuitBreakerMonitor>());

        Action actNull = () => monitor.Get(null!);
        Action actEmpty = () => monitor.Get("");
        Action actWhitespace = () => monitor.Get("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void Snapshot_MergesAcrossSources()
    {
        var a = new FakeMonitor();
        a.Add("auth-service", CircuitState.Closed);
        a.Add("notification-service", CircuitState.Open, failures: 5);

        var b = new FakeMonitor();
        b.Add("db.contact.read", CircuitState.Closed);
        b.Add("redis.cache", CircuitState.HalfOpen);

        var monitor = new CircuitBreakerMonitor(new ICircuitBreakerMonitor[] { a, b });

        var all = monitor.Snapshot();
        all.Should().HaveCount(4);
        all.Select(s => s.PolicyName)
            .Should().BeEquivalentTo(new[]
            {
                "auth-service", "notification-service", "db.contact.read", "redis.cache"
            });
    }

    [Fact]
    public void Get_ResolvesAcrossSources()
    {
        var a = new FakeMonitor();
        a.Add("auth-service", CircuitState.Open, failures: 3);

        var b = new FakeMonitor();
        b.Add("db.contact.read", CircuitState.Closed);

        var monitor = new CircuitBreakerMonitor(new ICircuitBreakerMonitor[] { a, b });

        monitor.Get("auth-service")!.State.Should().Be(CircuitState.Open);
        monitor.Get("db.contact.read")!.State.Should().Be(CircuitState.Closed);
        monitor.Get("nonexistent").Should().BeNull();
    }

    [Fact]
    public void Snapshot_DuplicateNames_FirstSourceWins()
    {
        var a = new FakeMonitor();
        a.Add("shared-policy", CircuitState.Open, failures: 99);

        var b = new FakeMonitor();
        b.Add("shared-policy", CircuitState.Closed);

        var monitor = new CircuitBreakerMonitor(new ICircuitBreakerMonitor[] { a, b });

        monitor.Snapshot().Should().HaveCount(1);
        monitor.Get("shared-policy")!.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        var a = new FakeMonitor();
        a.Add("Auth-Service", CircuitState.Closed);

        var monitor = new CircuitBreakerMonitor(new ICircuitBreakerMonitor[] { a });

        monitor.Get("auth-service").Should().NotBeNull();
        monitor.Get("AUTH-SERVICE").Should().NotBeNull();
    }
}
