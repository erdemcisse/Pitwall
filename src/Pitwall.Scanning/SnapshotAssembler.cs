using System.Collections.Immutable;
using System.Text.Json;
using Pitwall.Domain;

namespace Pitwall.Scanning;

public static class SnapshotAssembler
{
    public static MachineSnapshot Assemble(
        Guid runId,
        DateTimeOffset capturedAt,
        string localMachineKey,
        ImmutableArray<ProbeOutcome> outcomes,
        ImmutableArray<ObservedFact> mergedFacts,
        ImmutableDictionary<string, FactQuality> coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (string.IsNullOrWhiteSpace(localMachineKey))
            throw new ArgumentException("scan.machine_key_required", nameof(localMachineKey));

        return new MachineSnapshot
        {
            Id = runId,
            CapturedAt = capturedAt,
            LocalMachineKey = localMachineKey,
            OsBuild = ResolveOsBuild(mergedFacts),
            Identity = ResolveIdentity(mergedFacts),
            Capabilities = [],
            ComponentIdentities = [.. outcomes
                .Where(static outcome => outcome.Coverage == FactQuality.Valid)
                .Select(static outcome => outcome.ProbeId)
                .OrderBy(static id => id, StringComparer.Ordinal)],
            ProbeCoverage = coverage,
            Facts = mergedFacts,
        };
    }

    private static string ResolveOsBuild(ImmutableArray<ObservedFact> facts)
    {
        var caption = ValidText(facts, "os.caption");
        var build = ValidText(facts, "os.build");
        return caption is null ? "unresolved" : build is null ? caption : $"{caption} ({build})";
    }

    private static DeviceIdentity ResolveIdentity(ImmutableArray<ObservedFact> facts) =>
        new(
            ValidText(facts, "sys.uuid"),
            ValidText(facts, "board.serial"),
            ValidText(facts, "chassis.serial"),
            [.. facts
                .Where(static fact => fact.Key.StartsWith("disk.", StringComparison.Ordinal)
                    && fact.Key.EndsWith(".serial", StringComparison.Ordinal))
                .Select(static fact => fact.Reading.Match(
                    static (quality, payload) => quality == FactQuality.Valid && payload.ValueKind == JsonValueKind.String
                        ? payload.GetString()
                        : null,
                    static _ => (string?)null))
                .OfType<string>()
                .OrderBy(static identity => identity, StringComparer.Ordinal)]);

    private static string? ValidText(ImmutableArray<ObservedFact> facts, string key)
    {
        foreach (var fact in facts)
        {
            if (string.Equals(fact.Key, key, StringComparison.Ordinal))
            {
                return fact.Reading.Match(
                    static (quality, payload) => quality == FactQuality.Valid && payload.ValueKind == JsonValueKind.String
                        ? payload.GetString()
                        : null,
                    static _ => (string?)null);
            }
        }

        return null;
    }
}
