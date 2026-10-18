using System.Text.Json;
using Pitwall.Domain;
using Pitwall.Scanning;

namespace Pitwall.Tests;

public sealed class QuickScanRunnerTests
{
    private static string FreshDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pitwall-scan-test", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(path));
        return path;
    }

    private static Task<ScanRunResult> RunProbes(
        string directory,
        TimeSpan? overall = null,
        TimeSpan? probeTimeout = null,
        params IInventoryProbe[] probes) =>
        new QuickScanRunner().RunAsync(
            probes, directory,
            overall ?? TimeSpan.FromSeconds(30),
            probeTimeout ?? TimeSpan.FromSeconds(5),
            4, "test-machine", CancellationToken.None);

    [Fact]
    public async Task Refuses_an_existing_output_directory()
    {
        var directory = FreshDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sentinel.txt"), "sentinel");
        try
        {
            var result = await RunProbes(directory,
                probes: [FakeProbe.Instant("p", FakeProbe.ValidOutcome("p", FakeProbe.Fact("p.f", 1)))]);
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("scan.output_exists", result.ErrorCode);
            Assert.Null(result.SnapshotPath);
            var remaining = Assert.Single(Directory.GetFiles(directory));
            Assert.Equal("sentinel.txt", Path.GetFileName(remaining));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Writes_round_trippable_snapshot_and_summary()
    {
        var directory = FreshDirectory();
        try
        {
            var result = await RunProbes(directory,
                probes:
                [
                    FakeProbe.Instant("b-probe", FakeProbe.ValidOutcome("b-probe", FakeProbe.Fact("b.f", 2))),
                    FakeProbe.Instant("a-probe", FakeProbe.ValidOutcome("a-probe", FakeProbe.Fact("a.f", 1))),
                ]);
            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.SnapshotPath);
            Assert.NotNull(result.SummaryPath);

            var snapshotJson = await File.ReadAllTextAsync(result.SnapshotPath);
            var snapshot = JsonSerializer.Deserialize<MachineSnapshot>(snapshotJson, SchemaJson.Options);
            Assert.NotNull(snapshot);
            Assert.Equal(result.RunId, snapshot!.Id);
            Assert.Equal("test-machine", snapshot.LocalMachineKey);
            Assert.Equal(2, snapshot.Facts.Length);
            Assert.Equal(new[] { "a.f", "b.f" }, snapshot.Facts.Select(static fact => fact.Key).ToArray());
            Assert.Equal(2, snapshot.ProbeCoverage.Count);

            var summaryJson = await File.ReadAllTextAsync(result.SummaryPath);
            var summary = JsonSerializer.Deserialize<ScanSummary>(summaryJson, SchemaJson.Options);
            Assert.NotNull(summary);
            Assert.Equal(result.RunId, summary!.RunId);
            Assert.Equal("snapshot.json", summary.SnapshotFile);
            Assert.Equal(2, summary.FactCount);
            Assert.Null(summary.ErrorCode);
            Assert.Equal(2, summary.Probes.Length);

            var reserialized = JsonSerializer.Serialize(snapshot, SchemaJson.Options);
            var reparsed = JsonSerializer.Deserialize<MachineSnapshot>(reserialized, SchemaJson.Options);
            Assert.NotNull(reparsed);
            Assert.Equal(snapshot.Id, reparsed!.Id);
            Assert.Equal(snapshot.Facts.Length, reparsed.Facts.Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Overall_deadline_writes_a_stub_summary_and_exits_two()
    {
        var directory = FreshDirectory();
        try
        {
            var result = await RunProbes(directory,
                overall: TimeSpan.FromMilliseconds(300),
                probeTimeout: TimeSpan.FromSeconds(30),
                FakeProbe.Hang("hung"));
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("scan.overall_deadline", result.ErrorCode);
            Assert.Null(result.SnapshotPath);
            Assert.NotNull(result.SummaryPath);
            var summary = JsonSerializer.Deserialize<ScanSummary>(
                await File.ReadAllTextAsync(result.SummaryPath), SchemaJson.Options);
            Assert.NotNull(summary);
            Assert.Equal("scan.overall_deadline", summary!.ErrorCode);
            Assert.False(File.Exists(Path.Combine(directory, "snapshot.json")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_files()
    {
        var directory = FreshDirectory();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuickScanRunner().RunAsync(
                [FakeProbe.Hang("hung")], directory,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), 4, "test-machine", cancelled.Token));
        Assert.False(Directory.Exists(directory));
    }
}
