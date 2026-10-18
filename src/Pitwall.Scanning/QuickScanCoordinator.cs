using System.Collections.Immutable;
using System.Diagnostics;
using Pitwall.Domain;

namespace Pitwall.Scanning;

public sealed class ScanMergeException(string key)
    : InvalidOperationException("scan.duplicate_fact_key:" + key)
{
    public string Key { get; } = key;
}

public sealed record ScanResult(
    ImmutableArray<ProbeOutcome> Outcomes,
    ImmutableArray<ObservedFact> MergedFacts,
    ImmutableDictionary<string, FactQuality> Coverage,
    TimeSpan Duration);

public sealed class QuickScanCoordinator
{
    public static TimeSpan DefaultProbeTimeout { get; } = TimeSpan.FromSeconds(10);
    public const int MaxConcurrency = 4;

    public async Task<ScanResult> RunAsync(
        IReadOnlyList<IInventoryProbe> probes,
        TimeSpan probeTimeout,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0) throw new ArgumentException("scan.probes_required", nameof(probes));
        if (probeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        if (maxConcurrency <= 0 || maxConcurrency > MaxConcurrency)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        var started = Stopwatch.GetTimestamp();
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = probes.Select(probe => RunOneAsync(probe, probeTimeout, gate, cancellationToken)).ToArray();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        var ordered = outcomes.OrderBy(static outcome => outcome.ProbeId, StringComparer.Ordinal).ToArray();
        var merged = Merge(ordered);
        return new ScanResult(
            [.. ordered],
            merged,
            ordered.ToImmutableDictionary(static outcome => outcome.ProbeId, static outcome => outcome.Coverage),
            Stopwatch.GetElapsedTime(started));
    }

    private static async Task<ProbeOutcome> RunOneAsync(
        IInventoryProbe probe,
        TimeSpan probeTimeout,
        SemaphoreSlim gate,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        await gate.WaitAsync(callerToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            deadline.CancelAfter(probeTimeout);
            try
            {
                return await probe.CollectAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
            {
                var probeSource = new FactSource("Pitwall.Inventory", "0.6.0", "scan:coordinator");
                return ProbeOutcome.Failed(
                    probe.ProbeId, FactQuality.Unavailable, Stopwatch.GetElapsedTime(started), probeSource,
                    ProbeErrorKind.Timeout, "probe.timeout",
                    $"deadline {probeTimeout.TotalSeconds:F0}s exceeded");
            }
            catch (Exception exception) when (!callerToken.IsCancellationRequested)
            {
                var probeSource = new FactSource("Pitwall.Inventory", "0.6.0", "scan:coordinator");
                return ProbeOutcome.Failed(
                    probe.ProbeId, FactQuality.Unavailable, Stopwatch.GetElapsedTime(started), probeSource,
                    ProbeErrorKind.Thrown, "probe.unhandled", exception.GetType().Name);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static ImmutableArray<ObservedFact> Merge(IReadOnlyList<ProbeOutcome> ordered)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ObservedFact>();
        foreach (var outcome in ordered)
        {
            foreach (var fact in outcome.Facts)
            {
                if (!seen.Add(fact.Key))
                {
                    throw new ScanMergeException(fact.Key);
                }

                merged.Add(fact);
            }
        }

        return [.. merged];
    }
}
