using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Pitwall.Domain;

namespace Pitwall.Scanning;

public sealed record ScanProbeRow(
    string ProbeId,
    FactQuality Coverage,
    double DurationMs,
    int FactCount,
    string? ErrorCode);

public sealed record ScanSummary(
    Guid RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    int OverallDeadlineSeconds,
    bool Elevated,
    ImmutableArray<ScanProbeRow> Probes,
    string SnapshotFile,
    int FactCount,
    string? ErrorCode);

public sealed record ScanRunResult(
    int ExitCode,
    Guid RunId,
    string? SnapshotPath,
    string? SummaryPath,
    string? ErrorCode);

public sealed class QuickScanRunner
{
    public static TimeSpan DefaultOverallDeadline { get; } = TimeSpan.FromSeconds(90);

    public async Task<ScanRunResult> RunAsync(
        IReadOnlyList<IInventoryProbe> probes,
        string outputDirectory,
        TimeSpan overallDeadline,
        TimeSpan probeTimeout,
        int maxConcurrency,
        string? localMachineKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (overallDeadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(overallDeadline));
        cancellationToken.ThrowIfCancellationRequested();

        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        if (Directory.Exists(outputDirectory))
        {
            return new ScanRunResult(2, runId, null, null, "scan.output_exists");
        }

        Directory.CreateDirectory(outputDirectory);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(overallDeadline);
        var coordinator = new QuickScanCoordinator();
        try
        {
            var result = await coordinator.RunAsync(probes, probeTimeout, maxConcurrency, deadline.Token).ConfigureAwait(false);
            var finished = DateTimeOffset.UtcNow;
            var snapshot = SnapshotAssembler.Assemble(
                runId, finished, string.IsNullOrWhiteSpace(localMachineKey) ? Environment.MachineName : localMachineKey,
                result.Outcomes, result.MergedFacts, result.Coverage);
            var snapshotPath = Path.Combine(outputDirectory, "snapshot.json");
            var summaryPath = Path.Combine(outputDirectory, "summary.json");
            await File.WriteAllTextAsync(snapshotPath,
                JsonSerializer.Serialize(snapshot, SchemaJson.Options), cancellationToken).ConfigureAwait(false);
            var summary = new ScanSummary(
                runId, started, finished, (int)overallDeadline.TotalSeconds, IsElevated(),
                [.. result.Outcomes.Select(static outcome => new ScanProbeRow(
                    outcome.ProbeId, outcome.Coverage, outcome.Duration.TotalMilliseconds,
                    outcome.Facts.Length, outcome.ErrorCode))],
                "snapshot.json", result.MergedFacts.Length, null);
            await File.WriteAllTextAsync(summaryPath,
                JsonSerializer.Serialize(summary, SchemaJson.Options), cancellationToken).ConfigureAwait(false);
            return new ScanRunResult(0, runId, snapshotPath, summaryPath, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var finished = DateTimeOffset.UtcNow;
            var summary = new ScanSummary(
                runId, started, finished, (int)overallDeadline.TotalSeconds, IsElevated(),
                [], string.Empty, 0, "scan.overall_deadline");
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.json"),
                JsonSerializer.Serialize(summary, SchemaJson.Options), CancellationToken.None).ConfigureAwait(false);
            return new ScanRunResult(2, runId, null, Path.Combine(outputDirectory, "summary.json"), "scan.overall_deadline");
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
