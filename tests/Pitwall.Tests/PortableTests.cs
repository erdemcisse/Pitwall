using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Pitwall.Domain;
using Pitwall.Storage;

namespace Pitwall.Tests;

[CollectionDefinition("Footprint", DisableParallelization = true)]
public sealed class FootprintCollection;

[Collection("Footprint")]
public sealed class PortableTests
{
    [Fact]
    public async Task Full_synthetic_session_stays_in_root_and_stray_write_is_detected()
    {
        var sandbox = Fixtures.Sandbox();
        var root = Path.Combine(sandbox, "app");
        var before = Footprint.Capture(sandbox);
        var host = await PortableHost.OpenAsync(root);
        var snapshot = Fixtures.Snapshot();
        var session = await host.Store.StartPersonalSessionAsync(snapshot, "de");
        var bytes = Encoding.UTF8.GetBytes("Synthetic artifact; no hardware was accessed.");
        await using var stream = new MemoryStream(bytes);
        var artifact = await host.WriteArtifactAsync(stream);
        var evidence = new EvidenceItem { Id = snapshot.Facts[0].EvidenceIds[0], Origin = EvidenceOrigin.Observed, SnapshotId = snapshot.Id,
            Window = new(snapshot.CapturedAt, snapshot.CapturedAt), ArtifactSha256 = artifact.Sha256, AnalysisVersion = "fixture-v1", ProvenanceReference = "fixture:portable-session" };
        await host.Store.SaveEvidenceAsync(session.Session.Id, evidence);
        await host.CompleteSessionAsync(session.Session, snapshot.CapturedAt.AddMinutes(1));
        Assert.Equal(bytes, await host.ReadArtifactAsync(artifact.Sha256));
        Assert.NotNull((await host.Store.LoadSessionAsync(session.Session.Id)).EndedAt);
        Assert.Equal(artifact.Sha256, (await host.Store.LoadEvidenceAsync(evidence.Id)).ArtifactSha256);
        var after = Footprint.Capture(sandbox);
        Assert.Empty(before.RegistryErrors);
        Assert.Empty(after.RegistryErrors);
        var positive = Footprint.Compare(before, after, root);
        Assert.Empty(positive.OutsideFiles);
        Assert.Empty(positive.CreatedRegistryKeys);
        Assert.Empty(positive.RemovedRegistryKeys);

        var stray = Path.Combine(sandbox, "deliberate-stray.txt");
        await File.WriteAllTextAsync(stray, "Negative fixture, confined to the test sandbox.");
        var negative = Footprint.Compare(after, Footprint.Capture(sandbox), root);
        Assert.Contains(stray, negative.OutsideFiles);
        Assert.False(negative.Passed);
        var artifactPath = Path.Combine(Fixtures.RepositoryRoot, "artifacts", "phase0a", "portable-session-diff.json");
        await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(new
        {
            scope = "Recursive test sandbox filesystem content hashes; read-only HKCU key tree. No registry writes. Not a host-wide or transient-write audit.",
            sandbox, root, beforeRegistryKeyCount = before.RegistryKeys.Count, afterRegistryKeyCount = after.RegistryKeys.Count,
            registryReadErrors = before.RegistryErrors.Concat(after.RegistryErrors).Distinct().ToArray(), positive, negative,
            inRootFiles = after.Files.Keys.Where(path => Footprint.IsInside(path, root)).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(positive.Passed);
    }

    [Fact]
    public async Task Unwritable_root_reports_clear_failure_without_partial_state()
    {
        var sandbox = Fixtures.Sandbox();
        var root = Path.Combine(sandbox, "root-is-a-file");
        await File.WriteAllTextAsync(root, "Occupied path prevents creating portable state.");
        var before = Footprint.FilesOnly(sandbox);
        var exception = await Assert.ThrowsAsync<PortableRootException>(() => PortableHost.OpenAsync(root));
        Assert.Equal("PortableRootUnwritable", exception.ResourceKey);
        Assert.Equal(root, exception.Root);
        Assert.Equal(before, Footprint.FilesOnly(sandbox));
    }

    [Fact]
    public async Task Locked_existing_database_fails_before_creating_config_or_logs()
    {
        var root = Fixtures.Sandbox();
        var database = Path.Combine(root, "metadata.db");
        await File.WriteAllTextAsync(database, "locked fixture");
        await using var locked = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAsync<PortableRootException>(() => PortableHost.OpenAsync(root));
        Assert.Equal(new[] { "metadata.db" }, Directory.GetFileSystemEntries(root).Select(path => Path.GetFileName(path)).ToArray());
    }

    [Fact]
    public async Task Content_addressing_deduplicates_and_detects_corruption_and_path_traversal()
    {
        var host = await PortableHost.OpenAsync(Path.Combine(Fixtures.Sandbox(), "app"));
        await using var first = new MemoryStream([1, 2, 3]);
        var reference = await host.WriteArtifactAsync(first);
        await using var second = new MemoryStream([1, 2, 3]);
        Assert.Equal(reference, await host.WriteArtifactAsync(second));
        Assert.Single(Directory.GetFiles(Path.Combine(host.Root, "artifacts")));
        await File.WriteAllBytesAsync(Path.Combine(host.Root, reference.RelativePath), [4, 5, 6]);
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReadArtifactAsync(reference.Sha256));
        await Assert.ThrowsAsync<ArgumentException>(() => host.ReadArtifactAsync("../metadata.db"));
    }

    [Fact]
    public void Runtime_source_has_no_ambient_state_or_registry_apis()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Fixtures.RepositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            foreach (var forbidden in new[] { "Environment.SpecialFolder", "GetFolderPath(", "APPDATA", "LOCALAPPDATA", "Microsoft.Win32.Registry", "RegistryKey", "ServiceBase", "TaskScheduler" })
                Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
        }
    }
}

internal sealed record Footprint(Dictionary<string, string> Files, HashSet<string> RegistryKeys, List<string> RegistryErrors)
{
    internal static Footprint Capture(string sandbox)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        ReadKeys(Registry.CurrentUser, "HKCU", keys, errors);
        return new(FilesOnly(sandbox), keys, errors);
    }
    internal static Dictionary<string, string> FilesOnly(string sandbox) => Directory.EnumerateFiles(sandbox, "*", SearchOption.AllDirectories)
        .Order(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);

    private static void ReadKeys(RegistryKey key, string prefix, HashSet<string> keys, List<string> errors)
    {
        try
        {
            foreach (var name in key.GetSubKeyNames())
            {
                var path = prefix + "\\" + name;
                keys.Add(path);
                try { using var child = key.OpenSubKey(name, writable: false); if (child is not null) ReadKeys(child, path, keys, errors); }
                catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { errors.Add(path + ": " + exception.GetType().Name); }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException) { errors.Add(prefix + ": " + exception.GetType().Name); }
    }
    internal static bool IsInside(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static FootprintDiff Compare(Footprint before, Footprint after, string root) => new(
        before.Files.Keys.Union(after.Files.Keys).Where(path => !IsInside(path, root) && (!before.Files.TryGetValue(path, out var first) || !after.Files.TryGetValue(path, out var second) || first != second)).Order().ToArray(),
        after.RegistryKeys.Except(before.RegistryKeys).Order().ToArray(), before.RegistryKeys.Except(after.RegistryKeys).Order().ToArray());
}
internal sealed record FootprintDiff(string[] OutsideFiles, string[] CreatedRegistryKeys, string[] RemovedRegistryKeys)
{
    public bool Passed => OutsideFiles.Length == 0 && CreatedRegistryKeys.Length == 0 && RemovedRegistryKeys.Length == 0;
}
