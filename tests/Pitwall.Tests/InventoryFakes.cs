using Pitwall.Domain;
using Pitwall.Infrastructure.Windows;
using Pitwall.Scanning;

namespace Pitwall.Tests;

internal sealed class FakeWmi : IWmiSource
{
    private readonly Dictionary<string, WmiQueryResult> responses = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Queries = [];

    public void Respond(string queryContains, WmiQueryResult result) => responses[queryContains] = result;

    public Task<WmiQueryResult> QueryAsync(string scope, string query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (key, result) in responses.OrderByDescending(static entry => entry.Key.Length))
        {
            if (query.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(result);
            }
        }

        return Task.FromResult(new WmiQueryResult(false, [], "wmi.source_missing", 0));
    }

    public static WmiQueryResult Ok(params IReadOnlyDictionary<string, object?>[] rows) =>
        new(true, rows, "ok", 0);

    public static WmiQueryResult Fail(string code) => new(false, [], code, 0);

    public static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(static value => value.Key, static value => value.Value, StringComparer.OrdinalIgnoreCase);
}

internal sealed class FakeMemory : IMemorySource
{
    public ulong InstalledKilobytes = 16777216;
    public ulong AvailableBytes = 8589934592;
    public bool FailInstalled;
    public bool FailAvailable;

    public bool TryGetInstalledKilobytes(out ulong kilobytes, out int win32Error)
    {
        kilobytes = FailInstalled ? 0 : InstalledKilobytes;
        win32Error = FailInstalled ? 1 : 0;
        return !FailInstalled;
    }

    public bool TryGetAvailableBytes(out ulong bytes, out int win32Error)
    {
        bytes = FailAvailable ? 0 : AvailableBytes;
        win32Error = FailAvailable ? 1 : 0;
        return !FailAvailable;
    }
}

internal sealed class FakePower : IPowerSource
{
    public int AcLine = 1;
    public int Percent = 100;
    public bool Fail;

    public bool TryGetStatus(out int acLineStatus, out int batteryPercent, out int win32Error)
    {
        acLineStatus = Fail ? 255 : AcLine;
        batteryPercent = Fail ? 255 : Percent;
        win32Error = Fail ? 1 : 0;
        return !Fail;
    }
}

internal sealed class FakeVolumes : IVolumeSource
{
    public List<string> Roots { get; set; } = ["C:\\"];
    public Dictionary<string, VolumeLayout> Infos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VolumeDiskMapping> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool InfoFail;
    public bool MapFail;

    public IReadOnlyList<string> GetDriveRoots() => Roots;

    public bool TryGetVolumeInfo(string root, out VolumeLayout layout, out int win32Error)
    {
        if (InfoFail || !Infos.TryGetValue(root, out var found))
        {
            layout = new VolumeLayout(root, string.Empty, string.Empty, 0, 0);
            win32Error = 1;
            return false;
        }

        layout = found;
        win32Error = 0;
        return true;
    }

    public bool TryGetDiskMapping(string root, out VolumeDiskMapping mapping, out int win32Error)
    {
        if (MapFail || !Maps.TryGetValue(root, out var found))
        {
            mapping = new VolumeDiskMapping(root, [], 0);
            win32Error = 1;
            return false;
        }

        mapping = found;
        win32Error = 0;
        return true;
    }
}

internal sealed class FakeDisplay : IDisplaySource
{
    public List<DisplayPathInfo> Paths { get; set; } = [];
    public DisplayQueryResult? ResultOverride;

    public DisplayQueryResult QueryActivePaths() =>
        ResultOverride ?? new DisplayQueryResult(true, Paths, null, null);
}

internal sealed class ConcurrencyTracker
{
    private int current;
    private int observedMax;
    private readonly object gate = new();

    public int ObservedMax
    {
        get { lock (gate) { return observedMax; } }
    }

    public IDisposable Track()
    {
        var now = Interlocked.Increment(ref current);
        lock (gate)
        {
            observedMax = Math.Max(observedMax, now);
        }

        return new Release(this);
    }

    private sealed class Release(ConcurrencyTracker tracker) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref tracker.current);
    }
}

internal sealed class FakeProbe : IInventoryProbe
{
    public string ProbeId { get; }
    private readonly Func<CancellationToken, Task<ProbeOutcome>> run;

    public FakeProbe(string probeId, Func<CancellationToken, Task<ProbeOutcome>> run)
    {
        ProbeId = probeId;
        this.run = run;
    }

    public Task<ProbeOutcome> CollectAsync(CancellationToken cancellationToken) => run(cancellationToken);

    public static FakeProbe Instant(string probeId, ProbeOutcome outcome) =>
        new(probeId, _ => Task.FromResult(outcome));

    public static FakeProbe Delayed(string probeId, ProbeOutcome outcome, TimeSpan delay) =>
        new(probeId, async cancellationToken =>
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return outcome;
        });

    public static FakeProbe Hang(string probeId) =>
        new(probeId, async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("fake.hang_survived_cancellation");
        });

    public static FakeProbe Throw(string probeId, Exception exception) =>
        new(probeId, _ => Task.FromException<ProbeOutcome>(exception));

    public static FakeProbe Tracked(string probeId, ProbeOutcome outcome, ConcurrencyTracker tracker, TimeSpan delay) =>
        new(probeId, async cancellationToken =>
        {
            using (tracker.Track())
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        });

    public static ProbeOutcome ValidOutcome(string probeId, params ObservedFact[] facts) =>
        ProbeOutcome.Completed(
            probeId, [.. facts], TimeSpan.FromMilliseconds(5),
            new FactSource("test", "1.0", "fixture:" + probeId));

    public static ObservedFact Fact(string key, long value) =>
        InventoryFact.Number(key, "test-device", value,
            new FactSource("test", "1.0", "fixture"),
            DateTimeOffset.UnixEpoch, new UnitMetadata("count", "count", null));
}
