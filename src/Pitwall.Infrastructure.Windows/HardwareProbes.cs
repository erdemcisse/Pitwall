using Pitwall.Domain;

namespace Pitwall.Infrastructure.Windows;

public sealed class BoardProbe : ProbeBase
{
    public override string ProbeId => "board";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:board");

    private static readonly string[] Placeholders =
        ["", "UNKNOWN", "NONE", "DEFAULT STRING", "TO BE FILLED BY O.E.M.", "TO BE FILLED BY OEM", "SYSTEM SERIAL NUMBER"];

    private readonly IWmiSource wmi;
    public BoardProbe(IWmiSource wmi) => this.wmi = wmi;

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var facts = new List<ObservedFact>();
        var notes = new List<string>();
        var failedClasses = 0;
        var deniedClasses = 0;
        string? Track(string? code)
        {
            if (code is not null)
            {
                failedClasses++;
                if (code == "wmi.access_denied")
                {
                    deniedClasses++;
                }
            }

            return code;
        }

        var system = await wmi.QueryAsync("root\\cimv2",
            "SELECT Manufacturer, Model FROM Win32_ComputerSystem", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var systemSource = ClassSource("Win32_ComputerSystem");
        Track(AddClass(system, systemSource, facts, notes, (row, emit) =>
        {
            emit(TextOrMissing("sys.manufacturer", "sys", row.GetString("Manufacturer"), systemSource, observedAt));
            emit(TextOrMissing("sys.model", "sys", row.GetString("Model"), systemSource, observedAt));
        }));

        var product = await wmi.QueryAsync("root\\cimv2",
            "SELECT UUID FROM Win32_ComputerSystemProduct", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var productSource = ClassSource("Win32_ComputerSystemProduct");
        Track(AddClass(product, productSource, facts, notes, (row, emit) =>
        {
            emit(UuidOrMissing("sys.uuid", "sys", row.GetString("UUID"), productSource, observedAt));
        }));

        var board = await wmi.QueryAsync("root\\cimv2",
            "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var boardSource = ClassSource("Win32_BaseBoard");
        Track(AddClass(board, boardSource, facts, notes, (row, emit) =>
        {
            emit(TextOrMissing("board.manufacturer", "sys", row.GetString("Manufacturer"), boardSource, observedAt));
            emit(TextOrMissing("board.product", "sys", row.GetString("Product"), boardSource, observedAt));
            emit(SerialOrMissing("board.serial", "sys", row.GetString("SerialNumber"), boardSource, observedAt));
        }));

        var bios = await wmi.QueryAsync("root\\cimv2",
            "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var biosSource = ClassSource("Win32_BIOS");
        Track(AddClass(bios, biosSource, facts, notes, (row, emit) =>
        {
            emit(TextOrMissing("firmware.vendor", "sys", row.GetString("Manufacturer"), biosSource, observedAt));
            var versions = row.GetStrings("SMBIOSBIOSVersion");
            emit(versions.Count == 0
                ? InventoryFact.Missing("firmware.version", "sys", FactType.Text, FactQuality.Unavailable, biosSource, observedAt, NoUnits)
                : InventoryFact.Text("firmware.version", "sys", string.Join(" / ", versions), biosSource, observedAt, NoUnits));
            emit(TimeOrMissing("firmware.date", "sys", row.GetDateTime("ReleaseDate"), biosSource, observedAt));
            emit(SerialOrMissing("firmware.serial", "sys", row.GetString("SerialNumber"), biosSource, observedAt));
        }));

        var enclosure = await wmi.QueryAsync("root\\cimv2",
            "SELECT ChassisTypes, SerialNumber FROM Win32_SystemEnclosure", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var enclosureSource = ClassSource("Win32_SystemEnclosure");
        var chassisUnits = new UnitMetadata("dmtf-chassis-type", "dmtf-chassis-type", null);
        Track(AddClass(enclosure, enclosureSource, facts, notes, (row, emit) =>
        {
            var types = row.GetIntegers("ChassisTypes");
            emit(types.Count == 0
                ? InventoryFact.Missing("chassis.type", "sys", FactType.Number, FactQuality.Unavailable, enclosureSource, observedAt, chassisUnits)
                : InventoryFact.Number("chassis.type", "sys", types[0], enclosureSource, observedAt, chassisUnits));
            emit(SerialOrMissing("chassis.serial", "sys", row.GetString("SerialNumber"), enclosureSource, observedAt));
        }));

        (FactQuality Coverage, ProbeErrorKind Kind, string Code)? fallback = failedClasses == 5 && deniedClasses == 5
            ? (FactQuality.PermissionDenied, ProbeErrorKind.AccessDenied, "board.access_denied")
            : null;
        var note = notes.Count == 0 ? null : string.Join(";", notes);
        return Finish(facts, started, note: note, emptyFallback: fallback);
    }

    private static string? AddClass(
        WmiQueryResult result,
        FactSource source,
        List<ObservedFact> facts,
        List<string> notes,
        Action<IReadOnlyDictionary<string, object?>, Action<ObservedFact>> map)
    {
        if (!result.Ok)
        {
            notes.Add(result.ErrorCode + ":" + source.ProvenanceReference);
            return result.ErrorCode;
        }

        foreach (var row in result.Rows)
        {
            map(row, facts.Add);
        }

        return null;
    }

    private static FactSource ClassSource(string className) =>
        new("Pitwall.Inventory", "0.6.0", "wmi:" + className);

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);

    private static ObservedFact SerialOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        var usable = !string.IsNullOrWhiteSpace(normalized)
            && Array.IndexOf(Placeholders, normalized) < 0
            && !normalized.All(static character => character == '0' || character == 'F' || character == '-');
        return usable
            ? InventoryFact.Text(key, device, value!.Trim(), source, observedAt, NoUnits)
            : InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits);
    }

    private static ObservedFact UuidOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt)
    {
        var usable = Guid.TryParse(value, out var uuid)
            && uuid != Guid.Empty
            && uuid != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
        return usable
            ? InventoryFact.Text(key, device, uuid.ToString("D"), source, observedAt, NoUnits)
            : InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits);
    }

    private static ObservedFact TimeOrMissing(string key, string device, DateTimeOffset? value, FactSource source, DateTimeOffset observedAt) =>
        value is null
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, IsoTime)
            : InventoryFact.Text(key, device, value.Value.UtcDateTime.ToString("o"), source, observedAt, IsoTime);
}

public sealed class GpuProbe : ProbeBase
{
    public override string ProbeId => "gpu";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:gpu");

    private readonly IWmiSource wmi;
    public GpuProbe(IWmiSource wmi) => this.wmi = wmi;

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var result = await wmi.QueryAsync(
            "root\\cimv2",
            "SELECT PNPDeviceID, Description, AdapterCompatibility, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            return Finish([], started, emptyFallback: WmiFallback(result));
        }

        var rows = result.Rows
            .OrderBy(static row => row.GetString("PNPDeviceID"), StringComparer.Ordinal)
            .ToArray();
        var source = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_VideoController");
        var facts = new List<ObservedFact>
        {
            InventoryFact.Number("gpu.adapters.count", "sys", rows.Length, source, observedAt, Count),
        };
        var invalidRows = 0;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var device = $"gpu.{index}";
            var description = row.GetString("Description");
            var pnp = row.GetString("PNPDeviceID");
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(pnp))
            {
                invalidRows++;
                continue;
            }

            facts.Add(InventoryFact.Text($"gpu.adapter.{index}.description", device, description, source, observedAt, NoUnits));
            facts.Add(InventoryFact.Text($"gpu.adapter.{index}.pnp_id", device, pnp, source, observedAt, NoUnits));
            facts.Add(TextOrMissing($"gpu.adapter.{index}.vendor", device, row.GetString("AdapterCompatibility"), source, observedAt));
            facts.Add(TextOrMissing($"gpu.adapter.{index}.driver_version", device, row.GetString("DriverVersion"), source, observedAt));
            facts.Add(TextOrMissing($"gpu.adapter.{index}.driver_date", device, row.GetString("DriverDate"), source, observedAt));
            var ram = row.GetInteger("AdapterRAM");
            var ramKnown = ram is > 0 and < uint.MaxValue;
            facts.Add(!ramKnown
                ? InventoryFact.Missing($"gpu.adapter.{index}.adapter_ram_bytes", device, FactType.Number, FactQuality.Unavailable, source, observedAt, Bytes)
                : InventoryFact.Number($"gpu.adapter.{index}.adapter_ram_bytes", device, ram!.Value, source, observedAt, Bytes));
        }

        return Finish(facts, started, invalidRows,
            "gpu.wmi_ccd_mapping:unresolved;adapter LUIDs are reported per display path by the display probe; no reliable join key to Win32_VideoController rows exists, and list order never identifies a primary adapter");
    }

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);
}

public sealed class DisplayProbe : ProbeBase
{
    public override string ProbeId => "display";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:display");

    private readonly IDisplaySource display;
    public DisplayProbe(IDisplaySource display) => this.display = display;

    protected override Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        cancellationToken.ThrowIfCancellationRequested();
        var query = display.QueryActivePaths();
        if (!query.Ok)
        {
            var emptyFacts = new List<ObservedFact>();
            var denied = string.Equals(query.ErrorCode, "ccd.access_denied", StringComparison.Ordinal);
            (FactQuality Coverage, ProbeErrorKind Kind, string Code) fallback = denied
                ? (FactQuality.PermissionDenied, ProbeErrorKind.AccessDenied, "ccd.access_denied")
                : (FactQuality.Unavailable, ProbeErrorKind.SourceMissing, query.ErrorCode ?? "ccd.failed");
            return Task.FromResult(Finish(emptyFacts, started, note: query.Detail, emptyFallback: fallback));
        }

        var source = new FactSource("Pitwall.Inventory", "0.6.0", "ccd:active-paths");
        var paths = query.Paths;
        var facts = new List<ObservedFact>
        {
            InventoryFact.Number("display.paths.count", "sys", paths.Count, source, observedAt, Count),
        };
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var device = $"display.{index}";
            facts.Add(InventoryFact.Text($"display.path.{index}.adapter_luid", device, path.AdapterLuid, source, observedAt, NoUnits));
            facts.Add(InventoryFact.Number($"display.path.{index}.source_id", device, path.SourceId, source, observedAt, Count));
            facts.Add(InventoryFact.Number($"display.path.{index}.target_id", device, path.TargetId, source, observedAt, Count));
            facts.Add(path.MonitorName is null
                ? InventoryFact.Missing($"display.path.{index}.monitor_name", device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
                : InventoryFact.Text($"display.path.{index}.monitor_name", device, path.MonitorName, source, observedAt, NoUnits));
            facts.Add(path.MonitorPath is null
                ? InventoryFact.Missing($"display.path.{index}.monitor_path", device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
                : InventoryFact.Text($"display.path.{index}.monitor_path", device, path.MonitorPath, source, observedAt, NoUnits));
            facts.Add(path.Width is null || path.Height is null
                ? InventoryFact.Missing($"display.path.{index}.resolution", device, FactType.Text, FactQuality.Unavailable, source, observedAt, new UnitMetadata("pixels", "pixels", null))
                : InventoryFact.Text($"display.path.{index}.resolution", device, $"{path.Width.Value}x{path.Height.Value}", source, observedAt, new UnitMetadata("pixels", "pixels", null)));
            facts.Add(path.RefreshHz is null || !double.IsFinite(path.RefreshHz.Value)
                ? InventoryFact.Missing($"display.path.{index}.refresh_hz", device, FactType.Number, FactQuality.Unavailable, source, observedAt, new UnitMetadata("Hz", "Hz", null))
                : InventoryFact.Real($"display.path.{index}.refresh_hz", device, path.RefreshHz.Value, source, observedAt, new UnitMetadata("Hz", "Hz", null)));
            facts.Add(path.AdvancedColorValue is null
                ? InventoryFact.Missing($"display.path.{index}.advanced_color_raw", device, FactType.Number, FactQuality.Unavailable, source, observedAt, new UnitMetadata("bitmask", "bitmask", null))
                : InventoryFact.Number($"display.path.{index}.advanced_color_raw", device, path.AdvancedColorValue.Value, source, observedAt, new UnitMetadata("bitmask", "bitmask", null)));
        }

        return Task.FromResult(Finish(facts, started, note:
            "display.render_mapping:unresolved;active display paths only; which adapter renders versus displays on hybrid systems is not established"));
    }
}

public sealed class StorageProbe : ProbeBase
{
    public override string ProbeId => "storage";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:storage");

    private readonly IWmiSource wmi;
    private readonly IVolumeSource volumes;
    public StorageProbe(IWmiSource wmi, IVolumeSource volumes)
    {
        this.wmi = wmi;
        this.volumes = volumes;
    }

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var volumeSource = new FactSource("Pitwall.Inventory", "0.6.0", "native:volume");
        var diskSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_DiskDrive");
        var facts = new List<ObservedFact>();
        var notes = new List<string>();
        var invalidRows = 0;

        foreach (var root in volumes.GetDriveRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var letter = root.TrimEnd('\\', ':').ToLowerInvariant();
            var device = $"volume.{letter}";
            if (!volumes.TryGetVolumeInfo(root, out var layout, out _))
            {
                notes.Add("native.volume_info:unavailable:" + letter);
                continue;
            }

            facts.Add(InventoryFact.Text($"volume.{letter}.label", device, layout.Label, volumeSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Text($"volume.{letter}.filesystem", device, layout.FileSystem, volumeSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Number($"volume.{letter}.capacity_bytes", device, (long)layout.CapacityBytes, volumeSource, observedAt, Bytes));
            facts.Add(InventoryFact.Number($"volume.{letter}.free_bytes", device, (long)layout.FreeBytes, volumeSource, observedAt, Bytes));
            if (volumes.TryGetDiskMapping(root, out var mapping, out _))
            {
                facts.Add(InventoryFact.Number($"volume.{letter}.disks.count", device, mapping.DiskNumbers.Count, volumeSource, observedAt, Count));
                for (var index = 0; index < mapping.DiskNumbers.Count; index++)
                {
                    facts.Add(InventoryFact.Number($"volume.{letter}.disk.{index}", device, mapping.DiskNumbers[index], volumeSource, observedAt, Count));
                }
            }
            else
            {
                notes.Add("native.disk_extents:unavailable:" + letter);
                facts.Add(InventoryFact.Missing($"volume.{letter}.disks.count", device, FactType.Number, FactQuality.Unavailable, volumeSource, observedAt, Count));
            }
        }

        var disks = await wmi.QueryAsync("root\\cimv2",
            "SELECT Index, Model, SerialNumber, Size, InterfaceType, PNPDeviceID FROM Win32_DiskDrive",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!disks.Ok)
        {
            notes.Add(disks.ErrorCode + ":Win32_DiskDrive");
        }
        else
        {
            var rows = disks.Rows
                .OrderBy(static row => row.GetInteger("Index") ?? long.MaxValue)
                .ToArray();
            facts.Add(InventoryFact.Number("storage.disks.count", "sys", rows.Length, diskSource, observedAt, Count));
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                var device = $"disk.{index}";
                var model = row.GetString("Model");
                if (string.IsNullOrWhiteSpace(model))
                {
                    invalidRows++;
                    continue;
                }

                facts.Add(InventoryFact.Text($"disk.{index}.model", device, model, diskSource, observedAt, NoUnits));
                facts.Add(SerialOrMissing($"disk.{index}.serial", device, row.GetString("SerialNumber"), diskSource, observedAt));
                var size = row.GetInteger("Size");
                facts.Add(size is not > 0
                    ? InventoryFact.Missing($"disk.{index}.size_bytes", device, FactType.Number, FactQuality.Unavailable, diskSource, observedAt, Bytes)
                    : InventoryFact.Number($"disk.{index}.size_bytes", device, size.Value, diskSource, observedAt, Bytes));
                facts.Add(TextOrMissing($"disk.{index}.interface_type", device, row.GetString("InterfaceType"), diskSource, observedAt));
                facts.Add(TextOrMissing($"disk.{index}.pnp_id", device, row.GetString("PNPDeviceID"), diskSource, observedAt));
            }
        }

        var note = notes.Count == 0 ? null : string.Join(";", notes);
        return Finish(facts, started, invalidRows, note);
    }

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);

    private static ObservedFact SerialOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value.Trim(), source, observedAt, NoUnits);
}
