using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pitwall.CovenantHarness;

public sealed record CovenantThreshold
{
    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = string.Empty;

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = string.Empty;

    [JsonPropertyName("qualifier")]
    public string? Qualifier { get; init; }

    public double? AsDouble() =>
        Value.ValueKind == JsonValueKind.Number && Value.TryGetDouble(out var number) ? number : null;

    public string? AsString() =>
        Value.ValueKind == JsonValueKind.String ? Value.GetString() : null;
}

public sealed record CovenantCommitment
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("thresholdDisplay")]
    public string ThresholdDisplay { get; init; } = string.Empty;

    [JsonPropertyName("thresholds")]
    public IReadOnlyList<CovenantThreshold> Thresholds { get; init; } = [];

    [JsonPropertyName("adrSection")]
    public string AdrSection { get; init; } = string.Empty;
}

public sealed record CovenantManifest
{
    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; }

    [JsonPropertyName("sourceDocument")]
    public string SourceDocument { get; init; } = string.Empty;

    [JsonPropertyName("sourceSection")]
    public string SourceSection { get; init; } = string.Empty;

    [JsonPropertyName("commitments")]
    public IReadOnlyList<CovenantCommitment> Commitments { get; init; } = [];
}

public static class ManifestLoader
{
    public static CovenantManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<CovenantManifest>(json, ManifestJson.Options)
            ?? throw new InvalidDataException("covenant.manifest_unreadable");
        Validate(manifest);
        return manifest;
    }

    public static CovenantManifest LoadAdjacentToAssembly()
    {
        var directory = AppContext.BaseDirectory;
        var path = Path.Combine(directory, "covenant-manifest.json");
        if (!File.Exists(path))
        {
            var repositoryRoot = RepositoryRoot.Find();
            path = Path.Combine(repositoryRoot, "tools", "Pitwall.CovenantHarness", "covenant-manifest.json");
        }

        return Load(path);
    }

    private sealed record ExpectedThreshold(string Unit, string Direction, bool Numeric);

    private static readonly IReadOnlyDictionary<string, ExpectedThreshold[]> ExpectedShapes =
        new Dictionary<string, ExpectedThreshold[]>(StringComparer.Ordinal)
        {
            ["background-processes"] = [new("count", "equal", true)],
            ["windows-services"] = [new("count", "equal", true)],
            ["autostart-entries"] = [new("count", "equal", true)],
            ["idle-working-set"] = [new("MB", "lessOrEqual", true)],
            ["idle-cpu"] = [new("percent", "lessOrEqual", true)],
            ["sensor-cpu"] = [new("percent", "lessOrEqual", true)],
            ["launch-time"] = [new("seconds", "lessOrEqual", true), new("seconds", "lessOrEqual", true)],
            ["chart-capture"] = [new("fps", "greaterOrEqual", true), new("percent", "lessThan", true)],
            ["uninstall-residue"] = [new("residue-items", "equal", true)],
            ["telemetry"] = [new("state", "equal", false)],
        };

    public static void Validate(CovenantManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ManifestVersion <= 0)
        {
            throw new InvalidDataException("covenant.manifest_version_invalid");
        }

        if (manifest.Commitments.Count == 0)
        {
            throw new InvalidDataException("covenant.manifest_empty");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commitment in manifest.Commitments)
        {
            if (string.IsNullOrWhiteSpace(commitment.Id) || !ids.Add(commitment.Id))
            {
                throw new InvalidDataException("covenant.manifest_duplicate_or_empty_id");
            }

            if (string.IsNullOrWhiteSpace(commitment.Name))
            {
                throw new InvalidDataException("covenant.manifest_name_required:" + commitment.Id);
            }

            if (string.IsNullOrWhiteSpace(commitment.ThresholdDisplay))
            {
                throw new InvalidDataException("covenant.manifest_display_required:" + commitment.Id);
            }

            if (commitment.Thresholds.Count == 0)
            {
                throw new InvalidDataException("covenant.manifest_thresholds_required:" + commitment.Id);
            }

            foreach (var threshold in commitment.Thresholds)
            {
                if (string.IsNullOrWhiteSpace(threshold.Unit))
                {
                    throw new InvalidDataException("covenant.manifest_unit_required:" + commitment.Id);
                }

                if (threshold.Direction is not ("equal" or "lessOrEqual" or "lessThan" or "greaterOrEqual"))
                {
                    throw new InvalidDataException("covenant.manifest_direction_invalid:" + commitment.Id);
                }

                if (threshold.Value.ValueKind == JsonValueKind.Number)
                {
                    if (!threshold.AsDouble().HasValue || !double.IsFinite(threshold.AsDouble()!.Value))
                    {
                        throw new InvalidDataException("covenant.manifest_value_nonfinite:" + commitment.Id);
                    }
                }
                else if (threshold.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("covenant.manifest_value_type_invalid:" + commitment.Id);
                }
            }

            ValidateShape(commitment);
        }

        foreach (var required in ExpectedShapes.Keys)
        {
            if (!ids.Contains(required))
            {
                throw new InvalidDataException("covenant.manifest_missing_commitment:" + required);
            }
        }
    }

    private static void ValidateShape(CovenantCommitment commitment)
    {
        if (!ExpectedShapes.TryGetValue(commitment.Id, out var expected))
        {
            return;
        }

        if (commitment.Thresholds.Count != expected.Length)
        {
            throw new InvalidDataException("covenant.manifest_thresholds_invalid:" + commitment.Id);
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var threshold = commitment.Thresholds[index];
            if (!string.Equals(threshold.Unit, expected[index].Unit, StringComparison.Ordinal))
            {
                throw new InvalidDataException("covenant.manifest_unit_invalid:" + commitment.Id);
            }

            if (!string.Equals(threshold.Direction, expected[index].Direction, StringComparison.Ordinal))
            {
                throw new InvalidDataException("covenant.manifest_direction_invalid:" + commitment.Id);
            }

            var isNumeric = threshold.Value.ValueKind == JsonValueKind.Number;
            if (isNumeric != expected[index].Numeric)
            {
                throw new InvalidDataException("covenant.manifest_threshold_type_invalid:" + commitment.Id);
            }

            if (!isNumeric && !string.Equals(threshold.AsString(), "off", StringComparison.Ordinal))
            {
                throw new InvalidDataException("covenant.manifest_value_invalid:" + commitment.Id);
            }
        }
    }
}

internal static class ManifestJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };
}

internal static class RepositoryRoot
{
    internal static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
