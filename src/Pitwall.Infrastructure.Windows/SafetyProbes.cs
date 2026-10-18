using Pitwall.Domain;

namespace Pitwall.Infrastructure.Windows;

public sealed class ProtectionProbe : ProbeBase
{
    public override string ProbeId => "virt-protection";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:virt-protection");

    private readonly IWmiSource wmi;
    public ProtectionProbe(IWmiSource wmi) => this.wmi = wmi;

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var facts = new List<ObservedFact>();
        var notes = new List<string>();

        var system = await wmi.QueryAsync("root\\cimv2",
            "SELECT HypervisorPresent FROM Win32_ComputerSystem", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var systemSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_ComputerSystem");
        if (!system.Ok)
        {
            notes.Add(system.ErrorCode + ":Win32_ComputerSystem");
            facts.Add(InventoryFact.Missing("virt.hypervisor_present", "sys", FactType.Boolean,
                system.ErrorCode == "wmi.access_denied" ? FactQuality.PermissionDenied : FactQuality.Unavailable,
                systemSource, observedAt, NoUnits));
        }
        else if (system.Rows.Count == 0)
        {
            facts.Add(InventoryFact.Missing("virt.hypervisor_present", "sys", FactType.Boolean, FactQuality.Unavailable, systemSource, observedAt, NoUnits));
        }
        else
        {
            var present = system.Rows[0].GetBoolean("HypervisorPresent");
            facts.Add(present is null
                ? InventoryFact.Missing("virt.hypervisor_present", "sys", FactType.Boolean, FactQuality.Unavailable, systemSource, observedAt, NoUnits)
                : InventoryFact.Boolean("virt.hypervisor_present", "sys", present.Value, systemSource, observedAt, NoUnits));
        }

        var processor = await wmi.QueryAsync("root\\cimv2",
            "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor", WmiTimeout, cancellationToken).ConfigureAwait(false);
        var processorSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_Processor");
        if (!processor.Ok)
        {
            notes.Add(processor.ErrorCode + ":Win32_Processor");
            facts.Add(InventoryFact.Missing("virt.firmware_enabled", "sys", FactType.Boolean,
                processor.ErrorCode == "wmi.access_denied" ? FactQuality.PermissionDenied : FactQuality.Unavailable,
                processorSource, observedAt, NoUnits));
        }
        else
        {
            var enabled = processor.Rows.Count == 0 ? null : processor.Rows[0].GetBoolean("VirtualizationFirmwareEnabled");
            facts.Add(enabled is null
                ? InventoryFact.Missing("virt.firmware_enabled", "sys", FactType.Boolean, FactQuality.Unavailable, processorSource, observedAt, NoUnits)
                : InventoryFact.Boolean("virt.firmware_enabled", "sys", enabled.Value, processorSource, observedAt, NoUnits));
        }

        var guard = await wmi.QueryAsync("root\\Microsoft\\Windows\\DeviceGuard",
            "SELECT Version, VirtualizationBasedSecurityStatus, SecurityServicesConfigured, SecurityServicesRunning FROM Win32_DeviceGuard",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        var guardSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_DeviceGuard");
        if (!guard.Ok)
        {
            notes.Add(guard.ErrorCode + ":Win32_DeviceGuard");
            var missing = guard.ErrorCode == "wmi.access_denied" ? FactQuality.PermissionDenied : FactQuality.Unavailable;
            facts.Add(InventoryFact.Missing("protection.vbs_status", "sys", FactType.Number, missing, guardSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Missing("protection.services_configured", "sys", FactType.Number, missing, guardSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Missing("protection.services_running", "sys", FactType.Number, missing, guardSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Missing("protection.class_version", "sys", FactType.Text, missing, guardSource, observedAt, NoUnits));
        }
        else if (guard.Rows.Count == 0)
        {
            notes.Add("wmi.no_instances:Win32_DeviceGuard");
            facts.Add(InventoryFact.Missing("protection.vbs_status", "sys", FactType.Number, FactQuality.Unavailable, guardSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Missing("protection.services_configured", "sys", FactType.Number, FactQuality.Unavailable, guardSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Missing("protection.services_running", "sys", FactType.Number, FactQuality.Unavailable, guardSource, observedAt, NoUnits));
            facts.Add(InventoryFact.Missing("protection.class_version", "sys", FactType.Text, FactQuality.Unavailable, guardSource, observedAt, NoUnits));
        }
        else
        {
            var row = guard.Rows[0];
            facts.Add(NumberOrMissing("protection.vbs_status", "sys", row.GetInteger("VirtualizationBasedSecurityStatus"), guardSource, observedAt));
            facts.Add(NumberOrMissing("protection.services_configured", "sys", row.GetInteger("SecurityServicesConfigured"), guardSource, observedAt));
            facts.Add(NumberOrMissing("protection.services_running", "sys", row.GetInteger("SecurityServicesRunning"), guardSource, observedAt));
            facts.Add(TextOrMissing("protection.class_version", "sys", row.GetString("Version"), guardSource, observedAt));
        }

        var note = notes.Count == 0 ? null : string.Join(";", notes);
        return Finish(facts, started, note: note);
    }

    private static ObservedFact NumberOrMissing(string key, string device, long? value, FactSource source, DateTimeOffset observedAt) =>
        value is null
            ? InventoryFact.Missing(key, device, FactType.Number, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Number(key, device, value.Value, source, observedAt, NoUnits);

    private static ObservedFact TextOrMissing(string key, string device, string? value, FactSource source, DateTimeOffset observedAt) =>
        string.IsNullOrWhiteSpace(value)
            ? InventoryFact.Missing(key, device, FactType.Text, FactQuality.Unavailable, source, observedAt, NoUnits)
            : InventoryFact.Text(key, device, value, source, observedAt, NoUnits);
}

public sealed class SensorProbe : ProbeBase
{
    public override string ProbeId => "sensor";
    protected override FactSource Source => new("Pitwall.Inventory", "0.6.0", "probe:sensor");

    private readonly IWmiSource wmi;
    private readonly IPowerSource power;
    public SensorProbe(IWmiSource wmi, IPowerSource power)
    {
        this.wmi = wmi;
        this.power = power;
    }

    protected override async Task<ProbeOutcome> CollectCoreAsync(CancellationToken cancellationToken)
    {
        var started = Start();
        var observedAt = Now();
        var facts = new List<ObservedFact>();
        var notes = new List<string>();
        var invalidRows = 0;

        var denied = false;
        var thermalSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:MSAcpi_ThermalZoneTemperature");
        var thermal = await wmi.QueryAsync("root\\WMI",
            "SELECT CurrentTemperature, CriticalTripPoint FROM MSAcpi_ThermalZoneTemperature",
            WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!thermal.Ok)
        {
            notes.Add(thermal.ErrorCode + ":MSAcpi_ThermalZoneTemperature");
            denied |= thermal.ErrorCode == "wmi.access_denied";
        }
        else
        {
            for (var index = 0; index < thermal.Rows.Count; index++)
            {
                var row = thermal.Rows[index];
                var device = $"sensor.thermal.{index}";
                var current = row.GetInteger("CurrentTemperature");
                if (current is null)
                {
                    invalidRows++;
                    continue;
                }

                facts.Add(InventoryFact.Real($"sensor.thermal.{index}.celsius", device,
                    current.Value / 10.0 - 273.15, thermalSource, observedAt, Celsius));
                var trip = row.GetInteger("CriticalTripPoint");
                facts.Add(trip is null
                    ? InventoryFact.Missing($"sensor.thermal.{index}.critical_celsius", device, FactType.Number, FactQuality.Unavailable, thermalSource, observedAt, Celsius)
                    : InventoryFact.Real($"sensor.thermal.{index}.critical_celsius", device,
                        trip.Value / 10.0 - 273.15, thermalSource, observedAt, Celsius));
            }
        }

        var cimThermalSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_TemperatureProbe");
        var cimThermal = await wmi.QueryAsync("root\\cimv2",
            "SELECT CurrentReading FROM Win32_TemperatureProbe", WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!cimThermal.Ok)
        {
            notes.Add(cimThermal.ErrorCode + ":Win32_TemperatureProbe");
            denied |= cimThermal.ErrorCode == "wmi.access_denied";
        }
        else
        {
            for (var index = 0; index < cimThermal.Rows.Count; index++)
            {
                var reading = cimThermal.Rows[index].GetInteger("CurrentReading");
                facts.Add(reading is null
                    ? InventoryFact.Missing($"sensor.cim_thermal.{index}.reading_raw", $"sensor.cim_thermal.{index}", FactType.Number, FactQuality.Unavailable, cimThermalSource, observedAt, NoUnits)
                    : InventoryFact.Number($"sensor.cim_thermal.{index}.reading_raw", $"sensor.cim_thermal.{index}", reading.Value, cimThermalSource, observedAt, NoUnits));
            }
        }

        var batterySource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_Battery");
        var battery = await wmi.QueryAsync("root\\cimv2",
            "SELECT EstimatedChargeRemaining FROM Win32_Battery", WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!battery.Ok)
        {
            notes.Add(battery.ErrorCode + ":Win32_Battery");
            denied |= battery.ErrorCode == "wmi.access_denied";
        }
        else if (battery.Rows.Count == 0)
        {
            notes.Add("wmi.no_instances:Win32_Battery");
        }
        else
        {
            var charge = battery.Rows[0].GetInteger("EstimatedChargeRemaining");
            if (charge is null or < 0 or > 100)
            {
                invalidRows++;
            }
            else
            {
                facts.Add(InventoryFact.Number("sensor.battery.charge_percent", "battery", charge.Value, batterySource, observedAt, Percent));
            }
        }

        var powerSource = new FactSource("Pitwall.Inventory", "0.6.0", "native:power");
        if (power.TryGetStatus(out var acLine, out _, out _))
        {
            if (acLine is 0 or 1)
            {
                facts.Add(InventoryFact.Boolean("sensor.power.ac_online", "sys", acLine == 1, powerSource, observedAt, NoUnits));
            }
            else
            {
                facts.Add(InventoryFact.Missing("sensor.power.ac_online", "sys", FactType.Boolean, FactQuality.Unavailable, powerSource, observedAt, NoUnits));
            }
        }
        else
        {
            notes.Add("native.power_status:unavailable");
            facts.Add(InventoryFact.Missing("sensor.power.ac_online", "sys", FactType.Boolean, FactQuality.Unavailable, powerSource, observedAt, NoUnits));
        }

        var loadSource = new FactSource("Pitwall.Inventory", "0.6.0", "wmi:Win32_Processor");
        var load = await wmi.QueryAsync("root\\cimv2",
            "SELECT LoadPercentage FROM Win32_Processor", WmiTimeout, cancellationToken).ConfigureAwait(false);
        if (!load.Ok)
        {
            notes.Add(load.ErrorCode + ":Win32_Processor.LoadPercentage");
            denied |= load.ErrorCode == "wmi.access_denied";
        }
        else if (load.Rows.Count != 0)
        {
            var percent = load.Rows[0].GetInteger("LoadPercentage");
            if (percent is < 0 or > 100)
            {
                invalidRows++;
            }
            else if (percent is not null)
            {
                facts.Add(InventoryFact.Number("sensor.cpu.load_percent", "cpu.0", percent.Value, loadSource, observedAt, Percent));
            }
        }

        var noteParts = new List<string>(notes)
        {
            "sensor.cpu.load_percent is a point-in-time reading, not a rate",
        };
        (FactQuality Coverage, ProbeErrorKind Kind, string Code)? fallback = denied
            ? (FactQuality.PermissionDenied, ProbeErrorKind.AccessDenied, "sensor.access_denied")
            : null;
        return Finish(facts, started, invalidRows, string.Join(";", noteParts), fallback);
    }
}

public static class WindowsProbeCatalog
{
    public static IReadOnlyList<IInventoryProbe> Create() =>
        Create(WmiSource.Default, KernelMemorySource.Default, KernelPowerSource.Default, KernelVolumeSource.Default, CcdDisplaySource.Default);

    public static IReadOnlyList<IInventoryProbe> Create(
        IWmiSource wmi,
        IMemorySource memory,
        IPowerSource power,
        IVolumeSource volumes,
        IDisplaySource display)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(display);
        return
        [
            new OsProbe(wmi),
            new CpuProbe(wmi),
            new MemoryProbe(wmi, memory),
            new BoardProbe(wmi),
            new GpuProbe(wmi),
            new DisplayProbe(display),
            new StorageProbe(wmi, volumes),
            new ProtectionProbe(wmi),
            new SensorProbe(wmi, power),
        ];
    }
}
