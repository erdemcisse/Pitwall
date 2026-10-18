using System.Text.Json;
using Pitwall.Domain;
using Pitwall.Infrastructure.Windows;

namespace Pitwall.Tests;

public sealed class WindowsProbeTests
{
    private static string? TextOf(ObservedFact fact) =>
        fact.Reading.Match(
            static (quality, payload) => quality == FactQuality.Valid && payload.ValueKind == JsonValueKind.String
                ? payload.GetString()
                : null,
            static _ => (string?)null);

    private static long? NumberOf(ObservedFact fact) =>
        fact.Reading.Match(
            static (quality, payload) => quality == FactQuality.Valid && payload.ValueKind == JsonValueKind.Number
                ? payload.GetInt64()
                : null,
            static _ => (long?)null);

    private static double? RealOf(ObservedFact fact) =>
        fact.Reading.Match(
            static (quality, payload) => quality == FactQuality.Valid && payload.ValueKind == JsonValueKind.Number
                ? payload.GetDouble()
                : null,
            static _ => (double?)null);

    private static bool? FlagOf(ObservedFact fact) =>
        fact.Reading.Match(
            static (quality, payload) => quality == FactQuality.Valid
                && (payload.ValueKind == JsonValueKind.True || payload.ValueKind == JsonValueKind.False)
                ? payload.GetBoolean()
                : null,
            static _ => (bool?)null);

    private static FactQuality QualityOf(IReadOnlyList<ObservedFact> facts, string key) =>
        facts.Single(fact => fact.Key == key).Reading.Quality;

    private static Dictionary<string, ObservedFact> ByKey(IReadOnlyList<ObservedFact> facts) =>
        facts.ToDictionary(static fact => fact.Key, StringComparer.Ordinal);

    [Fact]
    public async Task Os_probe_collects_build_and_boot()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_OperatingSystem", FakeWmi.Ok(FakeWmi.Row(
            ("Caption", (object?)"Microsoft Windows 11 Pro"),
            ("Version", (object?)"10.0.26100"),
            ("BuildNumber", (object?)"26100"),
            ("OSArchitecture", (object?)"64-bit"),
            ("LastBootUpTime", (object?)"20260903080000.000000+120"),
            ("InstallDate", (object?)"20250101120000.000000+120"))));
        var outcome = await new OsProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal("Microsoft Windows 11 Pro", TextOf(facts["os.caption"]));
        Assert.Equal("26100", TextOf(facts["os.build"]));
        Assert.NotNull(TextOf(facts["os.boot_time"]));
        Assert.Equal("wmi:Win32_OperatingSystem", facts["os.caption"].Source.ProvenanceReference);
    }

    [Fact]
    public async Task Os_probe_denied_keeps_no_facts()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_OperatingSystem", FakeWmi.Fail("wmi.access_denied"));
        var outcome = await new OsProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.PermissionDenied, outcome.Coverage);
        Assert.Empty(outcome.Facts);
        Assert.Equal(ProbeErrorKind.AccessDenied, outcome.ErrorKind);
        Assert.Equal("wmi.access_denied", outcome.ErrorCode);
    }

    [Fact]
    public async Task Cpu_probe_sums_topology_and_keeps_missing_clocks_missing()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_Processor", FakeWmi.Ok(
            FakeWmi.Row(
                ("DeviceID", (object?)"CPU0"), ("Name", (object?)"AMD Ryzen 9"),
                ("Manufacturer", (object?)"AuthenticAMD"),
                ("NumberOfCores", (object?)8L), ("NumberOfLogicalProcessors", (object?)16L),
                ("MaxClockSpeed", (object?)5200L), ("L2CacheSize", (object?)1024L),
                ("L3CacheSize", (object?)65536L), ("Architecture", (object?)9L)),
            FakeWmi.Row(
                ("DeviceID", (object?)"CPU1"), ("Name", (object?)"AMD Ryzen 9"),
                ("Manufacturer", (object?)null),
                ("NumberOfCores", (object?)8L), ("NumberOfLogicalProcessors", (object?)16L),
                ("MaxClockSpeed", (object?)null), ("L2CacheSize", (object?)null),
                ("L3CacheSize", (object?)null), ("Architecture", (object?)null))));
        var outcome = await new CpuProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(2L, NumberOf(facts["cpu.packages.count"]));
        Assert.Equal(16L, NumberOf(facts["cpu.cores_physical.total"]));
        Assert.Equal(32L, NumberOf(facts["cpu.cores_logical.total"]));
        Assert.Equal("AMD Ryzen 9", TextOf(facts["cpu.package.0.name"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "cpu.package.1.max_clock_mhz"));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "cpu.package.1.vendor"));
    }

    [Fact]
    public async Task Cpu_probe_with_no_packages_reports_zero()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_Processor", FakeWmi.Ok());
        var outcome = await new CpuProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        Assert.Equal(0L, NumberOf(ByKey(outcome.Facts)["cpu.packages.count"]));
    }

    [Fact]
    public async Task Memory_probe_reports_modules_and_zero_speed_as_missing()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_PhysicalMemory", FakeWmi.Ok(
            FakeWmi.Row(
                ("DeviceLocator", (object?)"DIMM 0"), ("Capacity", (object?)17179869184L),
                ("Speed", (object?)5600L), ("Manufacturer", (object?)"Samsung"),
                ("PartNumber", (object?)"M323R2GA3BB0")),
            FakeWmi.Row(
                ("DeviceLocator", (object?)"DIMM 1"), ("Capacity", (object?)17179869184L),
                ("Speed", (object?)0L), ("Manufacturer", (object?)null),
                ("PartNumber", (object?)null))));
        var outcome = await new MemoryProbe(wmi, new FakeMemory()).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(17179869184L, NumberOf(facts["mem.total_bytes"]));
        Assert.Equal(2L, NumberOf(facts["mem.modules.count"]));
        Assert.Equal(17179869184L, NumberOf(facts["mem.module.0.capacity_bytes"]));
        Assert.Equal("Samsung", TextOf(facts["mem.module.0.manufacturer"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "mem.module.1.speed_mts"));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "mem.module.1.manufacturer"));
    }

    [Fact]
    public async Task Memory_probe_native_failure_keeps_wmi_facts_with_note()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_PhysicalMemory", FakeWmi.Ok(FakeWmi.Row(
            ("DeviceLocator", (object?)"DIMM 0"), ("Capacity", (object?)8589934592L),
            ("Speed", (object?)4800L), ("Manufacturer", (object?)"X"), ("PartNumber", (object?)"Y"))));
        var memory = new FakeMemory { FailInstalled = true, FailAvailable = true };
        var outcome = await new MemoryProbe(wmi, memory).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "mem.total_bytes"));
        Assert.Equal(1L, NumberOf(ByKey(outcome.Facts)["mem.modules.count"]));
        Assert.Contains("native.memory_total:unavailable", outcome.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Board_probe_filters_placeholders_and_parses_uuid()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_ComputerSystem", FakeWmi.Ok(FakeWmi.Row(
            ("Manufacturer", (object?)"Micro-Star"), ("Model", (object?)"MS-7D25"))));
        wmi.Respond("Win32_ComputerSystemProduct", FakeWmi.Ok(FakeWmi.Row(
            ("UUID", (object?)"FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF"))));
        wmi.Respond("Win32_BaseBoard", FakeWmi.Ok(FakeWmi.Row(
            ("Manufacturer", (object?)"Micro-Star"), ("Product", (object?)"MAG X670E"),
            ("SerialNumber", (object?)"To be filled by O.E.M."))));
        wmi.Respond("Win32_BIOS", FakeWmi.Ok(FakeWmi.Row(
            ("Manufacturer", (object?)"American Megatrends Inc."),
            ("SMBIOSBIOSVersion", (object?)new[] { "1.20", "AGESA" }),
            ("ReleaseDate", (object?)"20240101000000.000000+000"),
            ("SerialNumber", (object?)"00000000"))));
        wmi.Respond("Win32_SystemEnclosure", FakeWmi.Ok(FakeWmi.Row(
            ("ChassisTypes", (object?)new ushort[] { 3 }), ("SerialNumber", (object?)"ABC123"))));
        var outcome = await new BoardProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal("Micro-Star", TextOf(facts["sys.manufacturer"]));
        Assert.Equal("MAG X670E", TextOf(facts["board.product"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "sys.uuid"));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "board.serial"));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "firmware.serial"));
        Assert.Equal("ABC123", TextOf(facts["chassis.serial"]));
        Assert.Equal(3L, NumberOf(facts["chassis.type"]));
        Assert.Equal("1.20 / AGESA", TextOf(facts["firmware.version"]));
        Assert.Equal("wmi:Win32_BaseBoard", facts["board.product"].Source.ProvenanceReference);
        Assert.Equal("wmi:Win32_BIOS", facts["firmware.version"].Source.ProvenanceReference);
    }

    [Fact]
    public async Task Gpu_probe_order_never_determines_index()
    {
        static FakeWmi TwoAdapters() => new();
        var first = TwoAdapters();
        first.Respond("Win32_VideoController", FakeWmi.Ok(
            FakeWmi.Row(
                ("PNPDeviceID", (object?)"PCI\\VEN_10DE&DEV_2B85"), ("Description", (object?)"NVIDIA RTX"),
                ("AdapterCompatibility", (object?)"NVIDIA"), ("DriverVersion", (object?)"31.0.15.1234"),
                ("DriverDate", (object?)"20240101000000.000000+000"), ("AdapterRAM", (object?)4294967295L)),
            FakeWmi.Row(
                ("PNPDeviceID", (object?)"PCI\\VEN_1002&DEV_13C0"), ("Description", (object?)"AMD Radeon"),
                ("AdapterCompatibility", (object?)"AMD"), ("DriverVersion", (object?)"32.0.1"),
                ("DriverDate", (object?)null), ("AdapterRAM", (object?)536870912L))));
        var second = TwoAdapters();
        second.Respond("Win32_VideoController", FakeWmi.Ok(
            FakeWmi.Row(
                ("PNPDeviceID", (object?)"PCI\\VEN_1002&DEV_13C0"), ("Description", (object?)"AMD Radeon"),
                ("AdapterCompatibility", (object?)"AMD"), ("DriverVersion", (object?)"32.0.1"),
                ("DriverDate", (object?)null), ("AdapterRAM", (object?)536870912L)),
            FakeWmi.Row(
                ("PNPDeviceID", (object?)"PCI\\VEN_10DE&DEV_2B85"), ("Description", (object?)"NVIDIA RTX"),
                ("AdapterCompatibility", (object?)"NVIDIA"), ("DriverVersion", (object?)"31.0.15.1234"),
                ("DriverDate", (object?)"20240101000000.000000+000"), ("AdapterRAM", (object?)4294967295L))));
        var firstOutcome = await new GpuProbe(first).CollectAsync(CancellationToken.None);
        var secondOutcome = await new GpuProbe(second).CollectAsync(CancellationToken.None);
        string Fingerprint(ProbeOutcome outcome) => string.Join("|",
            outcome.Facts.OrderBy(static fact => fact.Key, StringComparer.Ordinal)
                .Select(static fact => fact.Key + "=" + fact.Reading.Match(
                    static (quality, payload) => quality.ToString() + ":" + payload.GetRawText(),
                    static quality => quality.ToString())));
        Assert.Equal(Fingerprint(firstOutcome), Fingerprint(secondOutcome));
        var facts = ByKey(firstOutcome.Facts);
        Assert.Equal("PCI\\VEN_1002&DEV_13C0", TextOf(facts["gpu.adapter.0.pnp_id"]));
        Assert.Equal("PCI\\VEN_10DE&DEV_2B85", TextOf(facts["gpu.adapter.1.pnp_id"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(firstOutcome.Facts, "gpu.adapter.1.adapter_ram_bytes"));
        Assert.Equal(536870912L, NumberOf(facts["gpu.adapter.0.adapter_ram_bytes"]));
        Assert.Contains("gpu.wmi_ccd_mapping:unresolved", firstOutcome.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Display_probe_reports_paths_and_missing_names()
    {
        var display = new FakeDisplay
        {
            Paths =
            [
                new DisplayPathInfo("luid:0000000000000001", 0, 0, "DELL U2720Q", "\\\\?\\DISPLAY#DEL", 3840, 2160, 59.94, 0),
                new DisplayPathInfo("luid:0000000000000001", 1, 1, null, null, null, null, null, null),
            ],
        };
        var outcome = await new DisplayProbe(display).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(2L, NumberOf(facts["display.paths.count"]));
        Assert.Equal("DELL U2720Q", TextOf(facts["display.path.0.monitor_name"]));
        Assert.Equal("3840x2160", TextOf(facts["display.path.0.resolution"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "display.path.1.monitor_name"));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "display.path.1.refresh_hz"));
    }

    [Fact]
    public async Task Display_probe_failure_maps_access_denied()
    {
        var display = new FakeDisplay
        {
            ResultOverride = new DisplayQueryResult(false, [], "ccd.access_denied", "ccd.query:win32:5"),
        };
        var outcome = await new DisplayProbe(display).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.PermissionDenied, outcome.Coverage);
        Assert.Equal(ProbeErrorKind.AccessDenied, outcome.ErrorKind);
        Assert.Empty(outcome.Facts);
        Assert.Equal("ccd.access_denied", outcome.ErrorCode);
        Assert.Equal("ccd.query:win32:5", outcome.Note);
    }

    [Fact]
    public async Task Display_probe_buffer_failure_maps_specific_code()
    {
        var display = new FakeDisplay
        {
            ResultOverride = new DisplayQueryResult(false, [], "ccd.buffer_sizes_failed", "ccd.buffer:win32:122"),
        };
        var outcome = await new DisplayProbe(display).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Unavailable, outcome.Coverage);
        Assert.Equal(ProbeErrorKind.SourceMissing, outcome.ErrorKind);
        Assert.Empty(outcome.Facts);
        Assert.Equal("ccd.buffer_sizes_failed", outcome.ErrorCode);
        Assert.Equal("ccd.buffer:win32:122", outcome.Note);
    }

    [Fact]
    public async Task Display_probe_query_failure_maps_specific_code()
    {
        var display = new FakeDisplay
        {
            ResultOverride = new DisplayQueryResult(false, [], "ccd.query_failed", "ccd.query:win32:87"),
        };
        var outcome = await new DisplayProbe(display).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Unavailable, outcome.Coverage);
        Assert.Equal(ProbeErrorKind.SourceMissing, outcome.ErrorKind);
        Assert.Empty(outcome.Facts);
        Assert.Equal("ccd.query_failed", outcome.ErrorCode);
        Assert.Equal("ccd.query:win32:87", outcome.Note);
    }

    [Fact]
    public async Task Storage_probe_reports_volumes_disks_and_missing_mapping()
    {
        var volumes = new FakeVolumes
        {
            Roots = ["C:\\", "D:\\"],
            Infos =
            {
                ["C:\\"] = new VolumeLayout("C:\\", string.Empty, "NTFS", 1000204886016L, 500107008L),
                ["D:\\"] = new VolumeLayout("D:\\", "Data", "NTFS", 2000409776128L, 1900000000000L),
            },
            Maps =
            {
                ["C:\\"] = new VolumeDiskMapping("C:\\", [0], 1),
            },
            MapFail = false,
        };
        var wmi = new FakeWmi();
        wmi.Respond("Win32_DiskDrive", FakeWmi.Ok(
            FakeWmi.Row(
                ("Index", (object?)0L), ("Model", (object?)"Samsung SSD 990 PRO 2TB"),
                ("SerialNumber", (object?)"S7DHNX0W123456"), ("Size", (object?)2000398934016L),
                ("InterfaceType", (object?)"NVMe"), ("PNPDeviceID", (object?)"SCSI\\DISK&VEN_NVME")),
            FakeWmi.Row(
                ("Index", (object?)1L), ("Model", (object?)null),
                ("SerialNumber", (object?)null), ("Size", (object?)null),
                ("InterfaceType", (object?)null), ("PNPDeviceID", (object?)null))));
        var outcome = await new StorageProbe(wmi, volumes).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Invalid, outcome.Coverage);
        Assert.Equal(ProbeErrorKind.InvalidPayload, outcome.ErrorKind);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(string.Empty, TextOf(facts["volume.c.label"]));
        Assert.Equal(1000204886016L, NumberOf(facts["volume.c.capacity_bytes"]));
        Assert.Equal(0L, NumberOf(facts["volume.c.disk.0"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "volume.d.disks.count"));
        Assert.Equal(2L, NumberOf(facts["storage.disks.count"]));
        Assert.Equal("S7DHNX0W123456", TextOf(facts["disk.0.serial"]));
    }

    [Fact]
    public async Task Storage_probe_denied_disk_class_keeps_volumes()
    {
        var volumes = new FakeVolumes
        {
            Infos = { ["C:\\"] = new VolumeLayout("C:\\", "OS", "NTFS", 100L, 50L) },
            Maps = { ["C:\\"] = new VolumeDiskMapping("C:\\", [0], 1) },
        };
        var wmi = new FakeWmi();
        wmi.Respond("Win32_DiskDrive", FakeWmi.Fail("wmi.access_denied"));
        var outcome = await new StorageProbe(wmi, volumes).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        Assert.Equal("OS", TextOf(ByKey(outcome.Facts)["volume.c.label"]));
        Assert.Contains("wmi.access_denied:Win32_DiskDrive", outcome.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_probe_volume_info_failure_yields_unavailable_without_facts()
    {
        var volumes = new FakeVolumes
        {
            InfoFail = true,
            Infos = { ["C:\\"] = new VolumeLayout("C:\\", "OS", "NTFS", 100L, 50L) },
        };
        var wmi = new FakeWmi();
        wmi.Respond("Win32_DiskDrive", FakeWmi.Fail("wmi.source_missing"));
        var outcome = await new StorageProbe(wmi, volumes).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Unavailable, outcome.Coverage);
        Assert.Equal("storage.no_data", outcome.ErrorCode);
        Assert.Empty(outcome.Facts);
        Assert.Contains("native.volume_info:unavailable:c", outcome.Note ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("wmi.source_missing:Win32_DiskDrive", outcome.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_probe_disk_mapping_failure_marks_count_unavailable()
    {
        var volumes = new FakeVolumes
        {
            Infos = { ["C:\\"] = new VolumeLayout("C:\\", "OS", "NTFS", 100L, 50L) },
            Maps = { ["C:\\"] = new VolumeDiskMapping("C:\\", [0], 1) },
            MapFail = true,
        };
        var wmi = new FakeWmi();
        wmi.Respond("Win32_DiskDrive", FakeWmi.Ok(FakeWmi.Row(
            ("Index", (object?)0L), ("Model", (object?)"Samsung SSD 990 PRO 2TB"),
            ("SerialNumber", (object?)"S7DHNX0W123456"), ("Size", (object?)2000398934016L),
            ("InterfaceType", (object?)"NVMe"), ("PNPDeviceID", (object?)"SCSI\\DISK&VEN_NVME"))));
        var outcome = await new StorageProbe(wmi, volumes).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal("OS", TextOf(facts["volume.c.label"]));
        var disksCount = facts["volume.c.disks.count"];
        Assert.Equal(FactQuality.Unavailable, disksCount.Reading.Quality);
        Assert.False(disksCount.Reading.HasPayload);
        Assert.False(facts.ContainsKey("volume.c.disk.0"));
        Assert.Contains("native.disk_extents:unavailable:c", outcome.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protection_probe_keeps_configured_and_running_distinct()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_ComputerSystem", FakeWmi.Ok(FakeWmi.Row(("HypervisorPresent", (object?)true))));
        wmi.Respond("Win32_Processor", FakeWmi.Ok(FakeWmi.Row(("VirtualizationFirmwareEnabled", (object?)true))));
        wmi.Respond("Win32_DeviceGuard", FakeWmi.Ok(FakeWmi.Row(
            ("Version", (object?)"10.0.26100"),
            ("VirtualizationBasedSecurityStatus", (object?)2L),
            ("SecurityServicesConfigured", (object?)1L),
            ("SecurityServicesRunning", (object?)0L))));
        var outcome = await new ProtectionProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(true, FlagOf(facts["virt.hypervisor_present"]));
        Assert.Equal(true, FlagOf(facts["virt.firmware_enabled"]));
        Assert.Equal(1L, NumberOf(facts["protection.services_configured"]));
        Assert.Equal(0L, NumberOf(facts["protection.services_running"]));
        Assert.Equal(2L, NumberOf(facts["protection.vbs_status"]));
    }

    [Fact]
    public async Task Protection_probe_unknown_enum_stays_valid_raw()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_ComputerSystem", FakeWmi.Ok(FakeWmi.Row(("HypervisorPresent", (object?)false))));
        wmi.Respond("Win32_Processor", FakeWmi.Ok(FakeWmi.Row(("VirtualizationFirmwareEnabled", (object?)null))));
        wmi.Respond("Win32_DeviceGuard", FakeWmi.Ok(FakeWmi.Row(
            ("Version", (object?)null),
            ("VirtualizationBasedSecurityStatus", (object?)99L),
            ("SecurityServicesConfigured", (object?)null),
            ("SecurityServicesRunning", (object?)null))));
        var outcome = await new ProtectionProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(99L, NumberOf(facts["protection.vbs_status"]));
        Assert.Equal(false, FlagOf(facts["virt.hypervisor_present"]));
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "virt.firmware_enabled"));
    }

    [Fact]
    public async Task Protection_probe_missing_namespace_marks_deviceguard_missing()
    {
        var wmi = new FakeWmi();
        wmi.Respond("Win32_ComputerSystem", FakeWmi.Ok(FakeWmi.Row(("HypervisorPresent", (object?)false))));
        wmi.Respond("Win32_Processor", FakeWmi.Ok(FakeWmi.Row(("VirtualizationFirmwareEnabled", (object?)false))));
        wmi.Respond("Win32_DeviceGuard", FakeWmi.Fail("wmi.source_missing"));
        var outcome = await new ProtectionProbe(wmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        Assert.Equal(FactQuality.Unavailable, QualityOf(outcome.Facts, "protection.services_running"));
        Assert.Contains("wmi.source_missing:Win32_DeviceGuard", outcome.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensor_probe_converts_thermal_and_preserves_valid_zero()
    {
        var wmi = new FakeWmi();
        wmi.Respond("MSAcpi_ThermalZoneTemperature", FakeWmi.Ok(FakeWmi.Row(
            ("CurrentTemperature", (object?)3292L), ("CriticalTripPoint", (object?)3782L))));
        wmi.Respond("Win32_TemperatureProbe", FakeWmi.Ok());
        wmi.Respond("Win32_Battery", FakeWmi.Ok(FakeWmi.Row(("EstimatedChargeRemaining", (object?)0L))));
        wmi.Respond("SELECT LoadPercentage", FakeWmi.Ok(FakeWmi.Row(("LoadPercentage", (object?)0L))));
        var outcome = await new SensorProbe(wmi, new FakePower { AcLine = 1 }).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, outcome.Coverage);
        var facts = ByKey(outcome.Facts);
        Assert.Equal(56.05, RealOf(facts["sensor.thermal.0.celsius"])!.Value, 2);
        Assert.Equal(105.05, RealOf(facts["sensor.thermal.0.critical_celsius"])!.Value, 2);
        Assert.Equal(0L, NumberOf(facts["sensor.battery.charge_percent"]));
        Assert.Equal(0L, NumberOf(facts["sensor.cpu.load_percent"]));
        Assert.Equal(true, FlagOf(facts["sensor.power.ac_online"]));
    }

    [Fact]
    public async Task Sensor_probe_with_no_signal_reports_unavailable()
    {
        var wmi = new FakeWmi();
        wmi.Respond("MSAcpi_ThermalZoneTemperature", FakeWmi.Fail("wmi.source_missing"));
        wmi.Respond("Win32_TemperatureProbe", FakeWmi.Ok());
        wmi.Respond("Win32_Battery", FakeWmi.Ok());
        wmi.Respond("SELECT LoadPercentage", FakeWmi.Fail("wmi.source_missing"));
        var outcome = await new SensorProbe(wmi, new FakePower { Fail = true }).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Unavailable, outcome.Coverage);
        Assert.Equal("sensor.no_data", outcome.ErrorCode);
        var only = Assert.Single(outcome.Facts);
        Assert.Equal("sensor.power.ac_online", only.Key);
        Assert.False(only.Reading.HasPayload);
    }

    [Fact]
    public async Task Sensor_probe_rejects_out_of_range_charge()
    {
        var wmi = new FakeWmi();
        wmi.Respond("MSAcpi_ThermalZoneTemperature", FakeWmi.Ok());
        wmi.Respond("Win32_TemperatureProbe", FakeWmi.Ok());
        wmi.Respond("Win32_Battery", FakeWmi.Ok(FakeWmi.Row(("EstimatedChargeRemaining", (object?)255L))));
        wmi.Respond("SELECT LoadPercentage", FakeWmi.Ok(FakeWmi.Row(("LoadPercentage", (object?)12L))));
        var outcome = await new SensorProbe(wmi, new FakePower()).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Invalid, outcome.Coverage);
        Assert.Equal(12L, NumberOf(ByKey(outcome.Facts)["sensor.cpu.load_percent"]));
    }

    [Fact]
    public async Task Sensor_probe_denied_surfaces_permission_denied()
    {
        var wmi = new FakeWmi();
        wmi.Respond("MSAcpi_ThermalZoneTemperature", FakeWmi.Fail("wmi.access_denied"));
        wmi.Respond("Win32_TemperatureProbe", FakeWmi.Fail("wmi.access_denied"));
        wmi.Respond("Win32_Battery", FakeWmi.Fail("wmi.access_denied"));
        wmi.Respond("SELECT LoadPercentage", FakeWmi.Fail("wmi.access_denied"));
        var outcome = await new SensorProbe(wmi, new FakePower { Fail = true }).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.PermissionDenied, outcome.Coverage);
        Assert.Equal(ProbeErrorKind.AccessDenied, outcome.ErrorKind);
        Assert.Equal("sensor.access_denied", outcome.ErrorCode);
        Assert.All(outcome.Facts, static fact => Assert.False(fact.Reading.HasPayload));
    }

    [Fact]
    public async Task Empty_device_lists_report_zero_counts()
    {
        var gpu = await new GpuProbe(Responded("Win32_VideoController", FakeWmi.Ok())).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, gpu.Coverage);
        Assert.Equal(0L, NumberOf(ByKey(gpu.Facts)["gpu.adapters.count"]));

        var displayOutcome = await new DisplayProbe(new FakeDisplay()).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, displayOutcome.Coverage);
        Assert.Equal(0L, NumberOf(ByKey(displayOutcome.Facts)["display.paths.count"]));

        var memory = await new MemoryProbe(Responded("Win32_PhysicalMemory", FakeWmi.Ok()), new FakeMemory())
            .CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, memory.Coverage);
        Assert.Equal(0L, NumberOf(ByKey(memory.Facts)["mem.modules.count"]));

        var disks = await new StorageProbe(
            Responded("Win32_DiskDrive", FakeWmi.Ok()), new FakeVolumes { Roots = [] })
            .CollectAsync(CancellationToken.None);
        Assert.Equal(0L, NumberOf(ByKey(disks.Facts)["storage.disks.count"]));
    }

    [Fact]
    public async Task Denied_sources_fail_closed_without_payloads()
    {
        var cpu = await new CpuProbe(Responded("Win32_Processor", FakeWmi.Fail("wmi.access_denied")))
            .CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.PermissionDenied, cpu.Coverage);
        Assert.Empty(cpu.Facts);

        var gpu = await new GpuProbe(Responded("Win32_VideoController", FakeWmi.Fail("wmi.access_denied")))
            .CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.PermissionDenied, gpu.Coverage);
        Assert.Empty(gpu.Facts);

        var boardWmi = new FakeWmi();
        boardWmi.Respond("Win32_ComputerSystem", FakeWmi.Fail("wmi.access_denied"));
        boardWmi.Respond("Win32_ComputerSystemProduct", FakeWmi.Fail("wmi.access_denied"));
        boardWmi.Respond("Win32_BaseBoard", FakeWmi.Fail("wmi.access_denied"));
        boardWmi.Respond("Win32_BIOS", FakeWmi.Fail("wmi.access_denied"));
        boardWmi.Respond("Win32_SystemEnclosure", FakeWmi.Fail("wmi.access_denied"));
        var board = await new BoardProbe(boardWmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.PermissionDenied, board.Coverage);
        Assert.Equal(ProbeErrorKind.AccessDenied, board.ErrorKind);
        Assert.Equal("board.access_denied", board.ErrorCode);
        Assert.All(board.Facts, static fact => Assert.False(fact.Reading.HasPayload));

        var guardDenied = new FakeWmi();
        guardDenied.Respond("Win32_ComputerSystem", FakeWmi.Ok(FakeWmi.Row(("HypervisorPresent", (object?)true))));
        guardDenied.Respond("Win32_Processor", FakeWmi.Ok(FakeWmi.Row(("VirtualizationFirmwareEnabled", (object?)true))));
        guardDenied.Respond("Win32_DeviceGuard", FakeWmi.Fail("wmi.access_denied"));
        var protection = await new ProtectionProbe(guardDenied).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Valid, protection.Coverage);
        Assert.Equal(FactQuality.PermissionDenied, QualityOf(protection.Facts, "protection.services_running"));
    }

    [Fact]
    public async Task Unexpected_shapes_produce_invalid_coverage_with_kept_facts()
    {
        var cpuWmi = Responded("Win32_Processor", FakeWmi.Ok(
            FakeWmi.Row(
                ("DeviceID", (object?)"CPU0"), ("Name", (object?)"Good CPU"),
                ("Manufacturer", (object?)"V"), ("NumberOfCores", (object?)4L),
                ("NumberOfLogicalProcessors", (object?)8L), ("MaxClockSpeed", (object?)4000L),
                ("L2CacheSize", (object?)512L), ("L3CacheSize", (object?)16384L), ("Architecture", (object?)9L)),
            FakeWmi.Row(
                ("DeviceID", (object?)"CPU1"), ("Name", (object?)null),
                ("Manufacturer", (object?)null), ("NumberOfCores", (object?)null),
                ("NumberOfLogicalProcessors", (object?)null), ("MaxClockSpeed", (object?)null),
                ("L2CacheSize", (object?)null), ("L3CacheSize", (object?)null), ("Architecture", (object?)null))));
        var cpu = await new CpuProbe(cpuWmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Invalid, cpu.Coverage);
        Assert.Equal(ProbeErrorKind.InvalidPayload, cpu.ErrorKind);
        Assert.Equal("Good CPU", TextOf(ByKey(cpu.Facts)["cpu.package.0.name"]));

        var memWmi = Responded("Win32_PhysicalMemory", FakeWmi.Ok(
            FakeWmi.Row(
                ("DeviceLocator", (object?)"DIMM 0"), ("Capacity", (object?)null),
                ("Speed", (object?)null), ("Manufacturer", (object?)null), ("PartNumber", (object?)null))));
        var memory = await new MemoryProbe(memWmi, new FakeMemory()).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Invalid, memory.Coverage);
        Assert.Equal("mem.invalid_row", memory.ErrorCode);

        var gpuWmi = Responded("Win32_VideoController", FakeWmi.Ok(
            FakeWmi.Row(
                ("PNPDeviceID", (object?)null), ("Description", (object?)"Nameless"),
                ("AdapterCompatibility", (object?)null), ("DriverVersion", (object?)null),
                ("DriverDate", (object?)null), ("AdapterRAM", (object?)null))));
        var gpu = await new GpuProbe(gpuWmi).CollectAsync(CancellationToken.None);
        Assert.Equal(FactQuality.Invalid, gpu.Coverage);
        Assert.Equal("gpu.invalid_row", gpu.ErrorCode);
        Assert.Equal(1L, NumberOf(ByKey(gpu.Facts)["gpu.adapters.count"]));
    }

    [Fact]
    public void Catalog_wires_nine_distinct_probes()
    {
        var probes = WindowsProbeCatalog.Create(
            new FakeWmi(), new FakeMemory(), new FakePower(), new FakeVolumes(), new FakeDisplay());
        Assert.Equal(9, probes.Count);
        Assert.Equal(9, probes.Select(static probe => probe.ProbeId).Distinct(StringComparer.Ordinal).Count());
    }

    private static FakeWmi Responded(string queryContains, WmiQueryResult result)
    {
        var wmi = new FakeWmi();
        wmi.Respond(queryContains, result);
        return wmi;
    }
}
