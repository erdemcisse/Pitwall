using Pitwall.Domain;

namespace Pitwall.Infrastructure.Windows;

public sealed class OsProbe : ProbeBase
{
    public override string ProbeId => "os";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:os");

    private readonly IWmiSource wmi;
    public OsProbe(IWmiSource wmi) => this.wmi = wmi;

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var result = await wmi.QueryAsync(
            "root\\cimv2",
            "SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, InstallDate FROM Win32_OperatingSystem",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Rows.Count == 0)
        {
            var emptyFacts = new List<ObservedFact>();
            (FactQuality Coverage, ProbeErrorKind Kind, string Code) fallback = !result.Ok
                ? WmiFallback(result)
                : (FactQuality.Unavailable, ProbeErrorKind.SourceMissing, "wmi.no_instances");
            return Finish(emptyFacts, started, emptyFallback: fallback);
        }

        var row = result.Rows[0];
        var source = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_OperatingSystem");
        var facts = new List<ObservedFact>
        {
            TextOrMissing("os.caption", "sys", row.GetString("Caption"), source, observedAt),
            TextOrMissing("os.version", "sys", row.GetString("Version"), source, observedAt),
            TextOrMissing("os.build", "sys", row.GetString("BuildNumber"), source, observedAt),
            TextOrMissing("os.architecture", "sys", row.GetString("OSArchitecture"), source, observedAt),
            TimeOrMissing("os.boot_time", "sys", row.GetDateTime("LastBootUpTime"), source, observedAt),
            TimeOrMissing("os.install_time", "sys", row.GetDateTime("InstallDate"), source, observedAt),
        };
        return Finish(facts, started);
    }

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);

    private static ObservedFact TimeOrMissing(string key, string device, DateTimeOffset? value, FactSource source, DateTimeOffset observedAt) =>
        value is null
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, IsoTime)
            : InventoryFact.Text(key, device, value.Value.UtcDateTime.ToString("o"), source, observedAt, IsoTime);
}

public sealed class CpuProbe : ProbeBase
{
    public override string ProbeId => "cpu";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:cpu");

    private readonly IWmiSource wmi;
    public CpuProbe(IWmiSource wmi) => this.wmi = wmi;

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var result = await wmi.QueryAsync(
            "root\\cimv2",
            "SELECT DeviceID, Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, Architecture FROM Win32_Processor",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return Finish([], started, emptyFallback: WmiFallback(result));
        }

        var rows = result.Rows
            .OrderBy(static row => row.GetString("DeviceID"), StringComparer.Ordinal)
            .ToArray();
        var source = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_Processor");
        var facts = new List<ObservedFact>
        {
            InventoryFact.Number("cpu.packages.count", "sys", rows.Length, source, observedAt, Count),
        };
        var invalidRows = 0;
        var physical = 0L;
        var logical = 0L;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var device = $"cpu.{index}";
            var name = row.GetString("Name");
            var cores = row.GetInteger("NumberOfCores");
            var threads = row.GetInteger("NumberOfLogicalProcessors");
            if (string.IsNullOrWhiteSpace(name) || cores is null || threads is null)
            {
                invalidRows++;
                continue;
            }

            physical += cores.Value;
            logical += threads.Value;
            facts.Add(InventoryFact.Text($"cpu.package.{index}.name", device, name, source, observedAt, NoUnits));
            facts.Add(TextOrMissing($"cpu.package.{index}.vendor", device, row.GetString("Manufacturer"), source, observedAt));
            facts.Add(InventoryFact.Number($"cpu.package.{index}.cores_physical", device, cores.Value, source, observedAt, Count));
            facts.Add(InventoryFact.Number($"cpu.package.{index}.cores_logical", device, threads.Value, source, observedAt, Count));
            facts.Add(NumberOrMissing($"cpu.package.{index}.max_clock_mhz", device, row.GetInteger("MaxClockSpeed"), source, observedAt, Megahertz));
            facts.Add(NumberOrMissing($"cpu.package.{index}.l2_cache_kb", device, row.GetInteger("L2CacheSize"), source, observedAt, new UnitMetadata("KB", "KB", null)));
            facts.Add(NumberOrMissing($"cpu.package.{index}.l3_cache_kb", device, row.GetInteger("L3CacheSize"), source, observedAt, new UnitMetadata("KB", "KB", null)));
            facts.Add(NumberOrMissing($"cpu.package.{index}.architecture", device, row.GetInteger("Architecture"), source, observedAt, new UnitMetadata("wmi-arch-code", "wmi-arch-code", null)));
        }

        if (rows.Length > 0 && invalidRows < rows.Length)
        {
            facts.Add(InventoryFact.Number("cpu.cores_physical.total", "sys", physical, source, observedAt, Count));
            facts.Add(InventoryFact.Number("cpu.cores_logical.total", "sys", logical, source, observedAt, Count));
        }

        return Finish(facts, started, invalidRows);
    }

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);

    private static ObservedFact NumberOrMissing(string key, string device, long? value, FactSource source, DateTimeOffset observedAt, UnitMetadata units) =>
        value is null
            ? InventoryFact.Missing(key, device, FactType.Number, FactQuality.Unavailable, source, observedAt, units)
            : InventoryFact.Number(key, device, value.Value, source, observedAt, units);
}

public sealed class MemoryProbe : ProbeBase
{
    public override string ProbeId => "mem";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:mem");

    private readonly IWmiSource wmi;
    private readonly IMemorySource memory;
    public MemoryProbe(IWmiSource wmi, IMemorySource memory)
    {
        this.wmi = wmi;
        this.memory = memory;
    }

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var nativeSource = new FactSource("Pitwall.Inventory", "0.6.0", "native:memory");
        var wmiSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_PhysicalMemory");
        var facts = new List<ObservedFact>();
        var notes = new List<string>();

        if (memory.TryGetInstalledKilobytes(out var kilobytes, out _))
        {
            facts.Add(InventoryFact.Number("mem.total_bytes", "sys", (long)kilobytes * 1024L, nativeSource, observedAt, Bytes));
        }
        else
        {
            facts.Add(InventoryFact.Missing("mem.total_bytes", "sys", FactType.Number, FactQuality.Unavailable, nativeSource, observedAt, Bytes));
            notes.Add("native.memory_total:unavailable");
        }

        if (memory.TryGetAvailableBytes(out var available, out _))
        {
            facts.Add(InventoryFact.Number("mem.available_bytes", "sys", (long)available, nativeSource, observedAt, Bytes));
        }
        else
        {
            facts.Add(InventoryFact.Missing("mem.available_bytes", "sys", FactType.Number, FactQuality.Unavailable, nativeSource, observedAt, Bytes));
            notes.Add("native.memory_available:unavailable");
        }

        var invalidRows = 0;
        (FactQuality Coverage, ProbeErrorKind Kind, string Code)? modulesFallback = null;
        var modules = await wmi.QueryAsync(
            "root\\cimv2",
            "SELECT DeviceLocator, Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!modules.Ok)
        {
            modulesFallback = WmiFallback(modules);
        }

        if (!modules.Ok)
        {
            notes.Add(modules.ErrorCode + ":Win32_PhysicalMemory");
            if (modules.ErrorCode == "wmi.access_denied")
            {
                foreach (var key in new[] { "mem.modules.count" })
                {
                    facts.Add(InventoryFact.Missing(key, "sys", FactType.Number, FactQuality.PermissionDenied, wmiSource, observedAt, Count));
                }
            }
        }
        else
        {
            var rows = modules.Rows
                .OrderBy(static row => row.GetString("DeviceLocator"), StringComparer.Ordinal)
                .ToArray();
            facts.Add(InventoryFact.Number("mem.modules.count", "sys", rows.Length, wmiSource, observedAt, Count));
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                var device = $"mem.module.{index}";
                var capacity = row.GetInteger("Capacity");
                var locator = row.GetString("DeviceLocator");
                if (capacity is null || capacity <= 0 || string.IsNullOrWhiteSpace(locator))
                {
                    invalidRows++;
                    continue;
                }

                facts.Add(InventoryFact.Number($"mem.module.{index}.capacity_bytes", device, capacity.Value, wmiSource, observedAt, Bytes));
                facts.Add(TextOrMissing($"mem.module.{index}.locator", device, locator, wmiSource, observedAt));
                facts.Add(SpeedOrMissing($"mem.module.{index}.speed_mts", device, row.GetInteger("Speed"), wmiSource, observedAt));
                facts.Add(TextOrMissing($"mem.module.{index}.manufacturer", device, row.GetString("Manufacturer"), wmiSource, observedAt));
                facts.Add(TextOrMissing($"mem.module.{index}.part_number", device, row.GetString("PartNumber"), wmiSource, observedAt));
            }
        }

        var note = notes.Count == 0 ? null : string.Join(";", notes);
        return Finish(facts, started, invalidRows, note, modulesFallback);
    }

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);

    private static ObservedFact SpeedOrMissing(string key, string device, long? value, FactSource source, DateTimeOffset observedAt) =>
        value is not > 0
            ? InventoryFact.Missing(key, device, FactType.Number, FactQuality.Unavailable, source, observedAt, new UnitMetadata("MT/s", "MT/s", null))
            : InventoryFact.Number(key, device, value.Value, source, observedAt, new UnitMetadata("MT/s", "MT/s", null));
}
