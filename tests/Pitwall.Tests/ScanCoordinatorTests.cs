using Pitwall.Domain;
using Pitwall.Scanning;

namespace Pitwall.Tests;

public sealed class ScanCoordinatorTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task Concurrency_never_exceeds_four()
    {
        var tracker = new ConcurrencyTracker();
        var probes = Enumerable.Range(0, 8)
            .Select(index => FakeProbe.Tracked(
                $"probe.{index}",
                FakeProbe.ValidOutcome($"probe.{index}", FakeProbe.Fact($"fact.{index}", index)),
                tracker,
                TimeSpan.FromMilliseconds(100)))
            .ToArray<IInventoryProbe>();
        var result = await new QuickScanCoordinator().RunAsync(
            probes, TimeSpan.FromSeconds(10), QuickScanCoordinator.MaxConcurrency, CancellationToken.None);
        Assert.Equal(8, result.MergedFacts.Length);
        Assert.True(tracker.ObservedMax <= 4, $"observed {tracker.ObservedMax}");
        Assert.True(tracker.ObservedMax > 1, "probes did not overlap at all");
    }

    [Fact]
    public async Task Timeout_isolates_the_hung_probe_and_keeps_completed_facts()
    {
        var good = FakeProbe.Instant("good", FakeProbe.ValidOutcome("good", FakeProbe.Fact("good.fact", 1)));
        var hung = FakeProbe.Hang("hung");
        var result = await new QuickScanCoordinator().RunAsync(
            [hung, good], Short, 4, CancellationToken.None);
        var byId = result.Outcomes.ToDictionary(static outcome => outcome.ProbeId, StringComparer.Ordinal);
        Assert.Equal(FactQuality.Valid, byId["good"].Coverage);
        Assert.Equal(FactQuality.Unavailable, byId["hung"].Coverage);
        Assert.Equal(ProbeErrorKind.Timeout, byId["hung"].ErrorKind);
        Assert.Equal("probe.timeout", byId["hung"].ErrorCode);
        Assert.Equal("good.fact", Assert.Single(result.MergedFacts).Key);
        Assert.Equal(FactQuality.Unavailable, result.Coverage["hung"]);
    }

    [Fact]
    public async Task Exception_isolates_the_throwing_probe()
    {
        var good = FakeProbe.Instant("good", FakeProbe.ValidOutcome("good", FakeProbe.Fact("good.fact", 1)));
        var bad = FakeProbe.Throw("bad", new InvalidOperationException("fake.boom"));
        var result = await new QuickScanCoordinator().RunAsync(
            [bad, good], TimeSpan.FromSeconds(10), 4, CancellationToken.None);
        var byId = result.Outcomes.ToDictionary(static outcome => outcome.ProbeId, StringComparer.Ordinal);
        Assert.Equal(FactQuality.Unavailable, byId["bad"].Coverage);
        Assert.Equal(ProbeErrorKind.Thrown, byId["bad"].ErrorKind);
        Assert.Equal("probe.unhandled", byId["bad"].ErrorCode);
        Assert.Contains("InvalidOperationException", byId["bad"].Note ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("good.fact", Assert.Single(result.MergedFacts).Key);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var probes = new[] { FakeProbe.Hang("hung") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuickScanCoordinator().RunAsync(probes, TimeSpan.FromSeconds(10), 4, cancelled.Token));
    }

    [Fact]
    public async Task Duplicate_fact_keys_are_refused_deterministically()
    {
        var first = FakeProbe.Delayed("a-probe",
            FakeProbe.ValidOutcome("a-probe", FakeProbe.Fact("shared.key", 1)), TimeSpan.FromMilliseconds(150));
        var second = FakeProbe.Instant("b-probe",
            FakeProbe.ValidOutcome("b-probe", FakeProbe.Fact("shared.key", 2)));
        var forward = await Assert.ThrowsAsync<ScanMergeException>(() =>
            new QuickScanCoordinator().RunAsync([first, second], TimeSpan.FromSeconds(10), 4, CancellationToken.None));
        Assert.Equal("shared.key", forward.Key);
        Assert.Contains("scan.duplicate_fact_key:shared.key", forward.Message, StringComparison.Ordinal);
        var backward = await Assert.ThrowsAsync<ScanMergeException>(() =>
            new QuickScanCoordinator().RunAsync([second, first], TimeSpan.FromSeconds(10), 4, CancellationToken.None));
        Assert.Equal("shared.key", backward.Key);
    }

    [Fact]
    public async Task Merge_order_follows_probe_id_not_finish_order()
    {
        var slow = FakeProbe.Delayed("a-probe",
            FakeProbe.ValidOutcome("a-probe", FakeProbe.Fact("a.fact", 1)), TimeSpan.FromMilliseconds(150));
        var fast = FakeProbe.Instant("b-probe",
            FakeProbe.ValidOutcome("b-probe", FakeProbe.Fact("b.fact", 2)));
        var result = await new QuickScanCoordinator().RunAsync(
            [slow, fast], TimeSpan.FromSeconds(10), 4, CancellationToken.None);
        Assert.Equal(new[] { "a-probe", "b-probe" }, result.Outcomes.Select(static outcome => outcome.ProbeId).ToArray());
        Assert.Equal(new[] { "a.fact", "b.fact" }, result.MergedFacts.Select(static fact => fact.Key).ToArray());
    }

    [Fact]
    public async Task Per_probe_deadline_is_configurable()
    {
        var slow = FakeProbe.Delayed("slow",
            FakeProbe.ValidOutcome("slow", FakeProbe.Fact("slow.fact", 1)), TimeSpan.FromSeconds(5));
        var timed = await new QuickScanCoordinator().RunAsync(
            [slow], Short, 4, CancellationToken.None);
        Assert.Equal(FactQuality.Unavailable, Assert.Single(timed.Outcomes).Coverage);
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected()
    {
        var coordinator = new QuickScanCoordinator();
        var probe = FakeProbe.Instant("p", FakeProbe.ValidOutcome("p", FakeProbe.Fact("p.f", 1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            coordinator.RunAsync([], TimeSpan.FromSeconds(1), 4, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.RunAsync([probe], TimeSpan.Zero, 4, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.RunAsync([probe], TimeSpan.FromSeconds(1), 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.RunAsync([probe], TimeSpan.FromSeconds(1), 5, CancellationToken.None));
    }
}
