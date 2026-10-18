using System.Collections.Immutable;
using System.Text.Json;
using Pitwall.Domain;
using Pitwall.Infrastructure.Windows;

namespace Pitwall.Tests;

public sealed class InventoryProbeTests
{
    private static readonly FactSource Source = new("test", "1.0", "fixture:probe-model");
    private static readonly UnitMetadata Units = new("count", "count", null);
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UnixEpoch;

    private static ObservedFact NumberFact(string key, long value) =>
        InventoryFact.Number(key, "test-device", value, Source, ObservedAt, Units);

    [Fact]
    public void Completed_carries_facts_and_no_error()
    {
        var outcome = ProbeOutcome.Completed("probe", [NumberFact("a", 1)], TimeSpan.FromSeconds(1), Source);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        Assert.Single(outcome.Facts);
        Assert.Equal(ProbeErrorKind.None, outcome.ErrorKind);
        Assert.Null(outcome.ErrorCode);
    }

    [Fact]
    public void Completed_rejects_empty_facts_but_keeps_caveat_notes()
    {
        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Completed("probe", [], TimeSpan.Zero, Source));
        var noted = ProbeOutcome.Completed("probe", [NumberFact("a", 1)], TimeSpan.Zero, Source, "caveat");
        Assert.Equal("caveat", noted.Note);
        Assert.Equal(ProbeErrorKind.None, noted.ErrorKind);
        Assert.Null(noted.ErrorCode);
        var facts = ProbeOutcome.Completed("probe", [NumberFact("a", 1)], TimeSpan.Zero, Source).Facts;
        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Partial("probe", facts, TimeSpan.Zero, Source, ProbeErrorKind.None, "probe.x"));
    }

    [Theory]
    [InlineData(FactQuality.Unsupported)]
    [InlineData(FactQuality.PermissionDenied)]
    [InlineData(FactQuality.Unavailable)]
    public void Failed_terminal_outcomes_retain_no_payload(FactQuality coverage)
    {
        var outcome = ProbeOutcome.Failed("probe", coverage, TimeSpan.Zero, Source, ProbeErrorKind.AccessDenied, "test.denied");
        Assert.Empty(outcome.Facts);
        Assert.Equal(coverage, outcome.Coverage);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ErrorCode));

        var missing = InventoryFact.Missing("k", "d", FactType.Text, FactQuality.Unavailable, Source, ObservedAt, Units);
        var withMissing = ProbeOutcome.Partial("probe", [missing], TimeSpan.Zero, Source,
            ProbeErrorKind.SourceMissing, "test.partial");
        Assert.Equal(FactQuality.Invalid, withMissing.Coverage);

        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Partial("probe", [NumberFact("a", 1)], TimeSpan.Zero, Source,
                ProbeErrorKind.None, "test.x"));
    }

    [Theory]
    [InlineData(FactQuality.Valid)]
    [InlineData(FactQuality.Estimated)]
    [InlineData(FactQuality.Stale)]
    [InlineData(FactQuality.Invalid)]
    public void Failed_rejects_non_terminal_coverage(FactQuality coverage)
    {
        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Failed("probe", coverage, TimeSpan.Zero, Source, ProbeErrorKind.Thrown, "test.x"));
    }

    [Fact]
    public void Failed_requires_error_kind_and_code()
    {
        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Failed("probe", FactQuality.Unavailable, TimeSpan.Zero, Source, ProbeErrorKind.None, "test.x"));
        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Failed("probe", FactQuality.Unavailable, TimeSpan.Zero, Source, ProbeErrorKind.Thrown, ""));
    }

    [Fact]
    public void Partial_keeps_facts_with_invalid_coverage_and_code()
    {
        var outcome = ProbeOutcome.Partial("probe", [NumberFact("a", 1)], TimeSpan.Zero, Source,
            ProbeErrorKind.InvalidPayload, "probe.invalid_row");
        Assert.Equal(FactQuality.Invalid, outcome.Coverage);
        Assert.Single(outcome.Facts);
        Assert.Equal("probe.invalid_row", outcome.ErrorCode);
    }

    [Fact]
    public void Outcome_rejects_negative_duration_and_blank_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProbeOutcome.Completed("probe", [NumberFact("a", 1)], TimeSpan.FromSeconds(-1), Source));
        Assert.Throws<ArgumentException>(() =>
            ProbeOutcome.Completed("  ", [NumberFact("a", 1)], TimeSpan.Zero, Source));
    }

    [Fact]
    public void Fact_helpers_preserve_a_real_zero()
    {
        var fact = InventoryFact.Number("sensor.cpu.load_percent", "cpu.0", 0, Source, ObservedAt, Units);
        var seen = fact.Reading.Match(
            static (quality, payload) => (quality, payload.GetInt64()),
            static _ => (FactQuality.Unavailable, -1));
        Assert.Equal(FactQuality.Valid, seen.quality);
        Assert.Equal(0L, seen.Item2);
    }

    [Fact]
    public void Fact_helpers_reject_bad_arguments()
    {
        Assert.Throws<ArgumentException>(() =>
            InventoryFact.Missing("k", "d", FactType.Number, FactQuality.Valid, Source, ObservedAt, Units));
        Assert.Throws<ArgumentException>(() =>
            InventoryFact.Real("k", "d", double.NaN, Source, ObservedAt, Units));
        Assert.Throws<ArgumentException>(() =>
            InventoryFact.Text("  ", "d", "v", Source, ObservedAt, Units));
    }

    [Fact]
    public async Task Probes_propagate_caller_cancellation()
    {
        var wmi = new FakeWmi();
        var probe = new OsProbe(wmi);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CollectAsync(cancelled.Token));
    }

    [Fact]
    public void Missing_facts_carry_no_payload()
    {
        var fact = InventoryFact.Missing("k", "d", FactType.Text, FactQuality.Unavailable, Source, ObservedAt, Units);
        Assert.False(fact.Reading.HasPayload);
        Assert.Equal(FactQuality.Unavailable, fact.Reading.Quality);
    }
}
