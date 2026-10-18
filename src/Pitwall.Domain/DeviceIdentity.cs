using System.Collections.Immutable;

namespace Pitwall.Domain;

public sealed record DeviceIdentity(string? SmbiosSystemUuid, string? BoardSerial, string? ChassisSerial, ImmutableArray<string> StorageIdentities);
public enum IdentityMatchKind { NoMatch, PartialMatch, Match, Ambiguous }
public sealed record IdentityCandidate(Guid DeviceId, double Confidence, ImmutableArray<string> Matches, ImmutableArray<string> Conflicts);
public sealed record IdentityResolution(IdentityMatchKind Kind, Guid? DeviceId, ImmutableArray<IdentityCandidate> Candidates);

public static class DeviceResolver
{
    public static IdentityResolution Resolve(DeviceIdentity identity, IEnumerable<Device> devices)
    {
        var candidates = devices.Select(device => Compare(identity, device)).Where(candidate => candidate.Matches.Length > 0)
            .OrderByDescending(candidate => candidate.Confidence).ThenBy(candidate => candidate.DeviceId).ToImmutableArray();
        if (candidates.IsEmpty) return new(IdentityMatchKind.NoMatch, null, candidates);
        var best = candidates[0];
        if (candidates.Length > 1 && candidates[1].Confidence == best.Confidence)
            return new(IdentityMatchKind.Ambiguous, null, candidates);
        var confident = best.Confidence >= 0.65 && best.Matches.Length >= 2 && best.Conflicts.IsEmpty;
        return new(confident ? IdentityMatchKind.Match : IdentityMatchKind.PartialMatch, confident ? best.DeviceId : null, candidates);
    }

    private static IdentityCandidate Compare(DeviceIdentity input, Device device)
    {
        var matches = ImmutableArray.CreateBuilder<string>();
        var conflicts = ImmutableArray.CreateBuilder<string>();
        var score = 0.0;
        CompareSignal("smbiosUuid", NormalizeUuid(input.SmbiosSystemUuid), NormalizeUuid(device.Identity.SmbiosSystemUuid), 0.45);
        CompareSignal("boardSerial", Normalize(input.BoardSerial), Normalize(device.Identity.BoardSerial), 0.25);
        CompareSignal("chassisSerial", Normalize(input.ChassisSerial), Normalize(device.Identity.ChassisSerial), 0.20);
        var incomingStorage = input.StorageIdentities.Select(Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var existingStorage = device.Identity.StorageIdentities.Select(Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (incomingStorage.Count > 0 && existingStorage.Count > 0)
        {
            if (incomingStorage.Overlaps(existingStorage)) { score += 0.10; matches.Add("storage"); }
            else conflicts.Add("storage");
        }
        return new(device.Id, Math.Round(score, 2), matches.ToImmutable(), conflicts.ToImmutable());

        void CompareSignal(string name, string? first, string? second, double weight)
        {
            if (first is null || second is null) return;
            if (first == second) { score += weight; matches.Add(name); }
            else conflicts.Add(name);
        }
    }

    private static string? NormalizeUuid(string? input) => Guid.TryParse(input, out var uuid) && uuid != Guid.Empty &&
        uuid != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff") ? uuid.ToString("D") : null;

    private static string? Normalize(string? input)
    {
        var value = input?.Trim().ToUpperInvariant();
        return value is null or "" or "UNKNOWN" or "NONE" or "DEFAULT STRING" or "TO BE FILLED BY O.E.M." or "SYSTEM SERIAL NUMBER" ||
            value.All(character => character == '0' || character == 'F' || character == '-') ? null : value;
    }
}
