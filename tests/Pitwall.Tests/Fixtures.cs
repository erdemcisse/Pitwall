using System.Collections.Immutable;
using System.Text.Json;
using Pitwall.Domain;

namespace Pitwall.Tests;

internal static class Fixtures
{
    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
    internal static string Sandbox()
    {
        var path = Path.Combine(RepositoryRoot, "artifacts", "phase0a", "sandboxes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    internal static readonly DeviceIdentity Identity = new("780d4d3e-e6dd-4af8-a01b-77411c3a5322", "BOARD-SYNTHETIC", "CHASSIS-SYNTHETIC", ["DISK-SYNTHETIC"]);
    internal static MachineSnapshot Snapshot() => new()
    {
        Id = Guid.NewGuid(), CapturedAt = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero), LocalMachineKey = "installation-before", OsBuild = "synthetic-before", Identity = Identity,
        ComponentIdentities = ["synthetic.cpu"], ProbeCoverage = ImmutableDictionary<string, FactQuality>.Empty.Add("synthetic", FactQuality.Unavailable),
        Facts = [Missing("unsupportedNumber", FactQuality.Unsupported, FactType.Number), Missing("permissionBoolean", FactQuality.PermissionDenied, FactType.Boolean),
            Missing("unavailableText", FactQuality.Unavailable, FactType.Text), Missing("invalidNumber", FactQuality.Invalid, FactType.Number),
            Present("realZero", FactQuality.Valid, FactType.Number, 0), Present("realFalse", FactQuality.Valid, FactType.Boolean, false),
            Present("realEmpty", FactQuality.Valid, FactType.Text, ""), Present("stale", FactQuality.Stale, FactType.Number, 41.25),
            Present("estimated", FactQuality.Estimated, FactType.Number, 9.5)]
    };
    private static ObservedFact Missing(string key, FactQuality quality, FactType type) => Fact(key, type, Observation<JsonElement>.Missing(quality));
    private static ObservedFact Present<T>(string key, FactQuality quality, FactType type, T value) => Fact(key, type, Observation<JsonElement>.Present(quality, JsonSerializer.SerializeToElement(value)));
    private static ObservedFact Fact(string key, FactType type, Observation<JsonElement> reading) => new()
    {
        Key = key, Type = type, Reading = reading, DeviceIdentity = "synthetic.cpu", Units = new("degF", "degC", "synthetic-units-v1"),
        Source = new("synthetic", "1.0", "fixture:partial-snapshot"), ObservedAt = DateTimeOffset.Parse("2026-09-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        Interval = new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1)), EvidenceIds = [Guid.Parse("bd05a0b8-82cc-4f49-81ee-ed23d01bc67e")]
    };
}
