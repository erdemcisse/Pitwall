using System.Security.Cryptography;
using System.Text.Json;
using Pitwall.Domain;

namespace Pitwall.Storage;

public sealed class PortableRootException(string root, Exception innerException) : IOException("portable.root_unwritable", innerException)
{
    public string Root { get; } = root;
    public string ResourceKey => "PortableRootUnwritable";
}
public sealed record ArtifactReference(string Sha256, long ByteLength, string RelativePath);

public sealed class PortableHost
{
    public string Root { get; }
    public SnapshotStore Store { get; }
    private PortableHost(string root)
    {
        Root = root;
        Store = new SnapshotStore(Path.Combine(root, "metadata.db"));
    }

    public static async Task<PortableHost> OpenAsync(string root, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("portable.absolute_root_required", nameof(root));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var rootExisted = Directory.Exists(root);
        try
        {
            EnsureNoReparsePoints(root);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            var directories = new[] { root, Path.Combine(root, "artifacts"), Path.Combine(root, "logs"), Path.Combine(root, "config") };
            foreach (var directory in directories)
            {
                EnsureNoReparsePoints(directory);
                if (File.Exists(directory)) throw new IOException("portable.directory_is_file");
                if (Directory.Exists(directory)) await ProbeAsync(directory, cancellationToken);
            }
            foreach (var file in new[] { "metadata.db", "config/settings.json", "logs/session.jsonl" })
            {
                var path = Path.Combine(root, file);
                EnsureNoReparsePoints(path);
                if (!File.Exists(path)) continue;
                await using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            var host = new PortableHost(root);
            await host.Store.InitializeAsync(cancellationToken);
            foreach (var directory in directories) Directory.CreateDirectory(directory);
            var settings = Path.Combine(root, "config", "settings.json");
            if (!File.Exists(settings)) await File.WriteAllTextAsync(settings, "{\"schemaVersion\":1,\"telemetryEnabled\":false}", cancellationToken);
            return host;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!rootExisted && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            throw new PortableRootException(root, exception);
        }
    }

    public async Task<ArtifactReference> WriteArtifactAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var artifactDirectory = Path.Combine(Root, "artifacts");
        EnsureNoReparsePoints(artifactDirectory);
        var temporary = Path.Combine(artifactDirectory, $"{Guid.NewGuid():N}.pending");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            string hash;
            await using (var input = File.OpenRead(temporary)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
            var length = new FileInfo(temporary).Length;
            var relativePath = Path.Combine("artifacts", hash);
            var destination = Path.Combine(Root, relativePath);
            EnsureNoReparsePoints(destination);
            if (File.Exists(destination))
            {
                await using var existing = File.OpenRead(destination);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, cancellationToken)) != hash) throw new InvalidDataException("artifact.hash_mismatch");
            }
            else File.Move(temporary, destination);
            await Store.RegisterArtifactAsync(hash, length, relativePath, cancellationToken);
            return new(hash, length, relativePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<byte[]> ReadArtifactAsync(string sha256, CancellationToken cancellationToken = default)
    {
        if (sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigitLower(character))) throw new ArgumentException("artifact.invalid_hash", nameof(sha256));
        var path = Path.Combine(Root, "artifacts", sha256);
        EnsureNoReparsePoints(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256) throw new InvalidDataException("artifact.hash_mismatch");
        return bytes;
    }

    public async Task CompleteSessionAsync(Session session, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        await Store.EndSessionAsync(session, endedAt, cancellationToken);
        var logPath = Path.Combine(Root, "logs", "session.jsonl");
        EnsureNoReparsePoints(logPath);
        var entry = JsonSerializer.Serialize(new { eventCode = "session.completed", sessionId = session.Id, at = endedAt }, SchemaJson.Options);
        await File.AppendAllTextAsync(logPath, entry + Environment.NewLine, cancellationToken);
    }

    private static async Task ProbeAsync(string directory, CancellationToken cancellationToken)
    {
        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
        await using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await stream.WriteAsync(new byte[] { 0 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("portable.reparse_point_not_supported");
        }
    }
}
