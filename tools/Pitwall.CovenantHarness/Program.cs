using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pitwall.CovenantHarness;

internal static class Program
{
    private static readonly HashSet<string> MeasurableForFailHook = new(StringComparer.Ordinal)
    {
        "background-processes",
        "windows-services",
        "autostart-entries",
        "idle-working-set",
        "launch-time",
        "telemetry",
    };

    internal static async Task<int> Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        return await RunAsync(args, lifetime.Token).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? targetExe = null;
        string? outputJson = null;
        string? manifestPath = null;
        string? failCommitment = null;
        var sampleSeconds = 10;
        var windowTimeoutSeconds = 20;
        var settleSeconds = 5;

        try
        {
            var positionals = new List<string>();
            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (string.Equals(token, "--sample-seconds", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    if (!int.TryParse(args[index], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out sampleSeconds) || sampleSeconds <= 0 || sampleSeconds > 300)
                    {
                        await Console.Error.WriteLineAsync("covenant.invalid_sample_seconds").ConfigureAwait(false);
                        return 2;
                    }
                }
                else if (string.Equals(token, "--full-cpu-window", StringComparison.Ordinal))
                {
                    sampleSeconds = Measurements.CommitmentWindowSeconds;
                }
                else if (string.Equals(token, "--manifest", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    manifestPath = args[index];
                }
                else if (string.Equals(token, "--fail-commitment", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    failCommitment = args[index];
                }
                else if (string.Equals(token, "--timeout-seconds", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    if (!int.TryParse(args[index], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out windowTimeoutSeconds) || windowTimeoutSeconds <= 0 || windowTimeoutSeconds > 120)
                    {
                        await Console.Error.WriteLineAsync("covenant.invalid_timeout").ConfigureAwait(false);
                        return 2;
                    }
                }
                else if (string.Equals(token, "--settle-seconds", StringComparison.Ordinal) && index + 1 < args.Length)
                {
                    index++;
                    if (!int.TryParse(args[index], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out settleSeconds) || settleSeconds < 1 || settleSeconds > 60)
                    {
                        await Console.Error.WriteLineAsync("covenant.invalid_settle_seconds").ConfigureAwait(false);
                        return 2;
                    }
                }
                else if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    await Console.Error.WriteLineAsync("covenant.unknown_option:" + token).ConfigureAwait(false);
                    return 2;
                }
                else
                {
                    positionals.Add(token);
                }
            }

            if (positionals.Count > 2)
            {
                await Console.Error.WriteLineAsync("covenant.usage: Pitwall.CovenantHarness [targetExe] [outputJson] [options]").ConfigureAwait(false);
                return 2;
            }

            if (positionals.Count >= 1)
            {
                targetExe = positionals[0];
            }

            if (positionals.Count == 2)
            {
                outputJson = positionals[1];
            }

            if (!string.IsNullOrWhiteSpace(failCommitment) && !MeasurableForFailHook.Contains(failCommitment))
            {
                await Console.Error.WriteLineAsync("covenant.fail_hook_measurable_only:" + failCommitment).ConfigureAwait(false);
                return 2;
            }

            var manifest = string.IsNullOrWhiteSpace(manifestPath)
                ? ManifestLoader.LoadAdjacentToAssembly()
                : ManifestLoader.Load(Path.GetFullPath(manifestPath));

            targetExe ??= LocateDefaultTarget();
            var targetFullPath = Path.GetFullPath(targetExe);
            if (!File.Exists(targetFullPath))
            {
                await Console.Error.WriteLineAsync("covenant.target_missing:" + targetFullPath).ConfigureAwait(false);
                return 2;
            }

            if (outputJson is not null && File.Exists(Path.GetFullPath(outputJson)))
            {
                await Console.Error.WriteLineAsync("covenant.evidence_exists:" + Path.GetFullPath(outputJson)).ConfigureAwait(false);
                return 2;
            }

            var repositoryRoot = RepositoryRoot.Find();
            var windowTimeout = TimeSpan.FromSeconds(windowTimeoutSeconds);

            var launch = await Measurements.MeasureLaunchAsync(targetFullPath, windowTimeout, cancellationToken).ConfigureAwait(false);
            var running = await Measurements.MeasureRunningAppAsync(
                targetFullPath, sampleSeconds, windowTimeout, TimeSpan.FromSeconds(settleSeconds), cancellationToken).ConfigureAwait(false);
            var autostartCount = Measurements.CountAutostartEntries();
            var serviceCount = Measurements.CountMatchingServices();
            var telemetryRefs = Measurements.CountTelemetryReferences(repositoryRoot);

            var results = CovenantBuilder.BuildResults(
                manifest, launch, running, autostartCount, serviceCount, telemetryRefs, sampleSeconds, settleSeconds);

            if (!string.IsNullOrWhiteSpace(failCommitment))
            {
                results = CovenantBuilder.ApplyFailHook(results, failCommitment);
            }

            var report = CovenantReport.Create(manifest, DateTimeOffset.UtcNow, results);
            var payload = ResultPayload.FromReport(manifest, report, targetFullPath, launch, running, sampleSeconds, settleSeconds);

            var json = JsonSerializer.Serialize(payload, ResultJson.Options);
            if (outputJson is null)
            {
                await Console.Out.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var outputFullPath = Path.GetFullPath(outputJson);
                var parent = Path.GetDirectoryName(outputFullPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                try
                {
                    await using var stream = new FileStream(outputFullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Console.Error.WriteLineAsync("covenant.evidence_exists:" + outputFullPath).ConfigureAwait(false);
                    return 2;
                }
            }

            return report.HasFail ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("covenant.cancelled").ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("covenant.error:" + exception.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static string LocateDefaultTarget()
    {
        var root = RepositoryRoot.Find();
        var candidate = Path.Combine(
            root, "src", "Pitwall.App", "bin", "Release",
            "net10.0-windows10.0.26100.0", "win-x64", "Pitwall.App.exe");
        return candidate;
    }

    private sealed record ResultPayload(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("manifestVersion")] string ManifestVersion,
        [property: JsonPropertyName("measuredAt")] DateTimeOffset MeasuredAt,
        [property: JsonPropertyName("target")] TargetPayload Target,
        [property: JsonPropertyName("sample")] SamplePayload Sample,
        [property: JsonPropertyName("results")] IReadOnlyList<EntryPayload> Results)
    {
        internal static ResultPayload FromReport(
            CovenantManifest manifest,
            CovenantReport report,
            string targetExe,
            LaunchObservation launch,
            AppObservation running,
            int sampleSeconds,
            int settleSeconds) =>
            new(
                "pitwall.covenant-result/2",
                manifest.ManifestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                report.MeasuredAt,
                new TargetPayload(targetExe, running.ProcessId),
                new SamplePayload(sampleSeconds, settleSeconds, Measurements.CommitmentWindowSeconds, launch, running),
                manifest.Commitments.Select(commitment =>
                {
                    var result = report.Results[commitment.Id];
                    return new EntryPayload(
                        commitment.Id,
                        commitment.Name,
                        result.Status.ToString(),
                        result.Measurement?.NumericValue,
                        result.Measurement?.Unit,
                        result.Measurement?.Method,
                        result.Measurement?.Note,
                        result.BlockedBy,
                        result.Qualification,
                        result.QualificationReason);
                }).ToArray());
    }

    private sealed record TargetPayload(
        [property: JsonPropertyName("executable")] string Executable,
        [property: JsonPropertyName("processId")] int ProcessId);

    private sealed record SamplePayload(
        [property: JsonPropertyName("cpuSampleSeconds")] int CpuSampleSeconds,
        [property: JsonPropertyName("settleSeconds")] int SettleSeconds,
        [property: JsonPropertyName("commitmentWindowSeconds")] int CommitmentWindowSeconds,
        [property: JsonPropertyName("launch")] LaunchObservation Launch,
        [property: JsonPropertyName("running")] AppObservation Running);

    private sealed record EntryPayload(
        [property: JsonPropertyName("commitmentId")] string CommitmentId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("measuredValue")] double? MeasuredValue,
        [property: JsonPropertyName("unit")] string? Unit,
        [property: JsonPropertyName("method")] string? Method,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("blockedBy")] string? BlockedBy,
        [property: JsonPropertyName("qualification")] string? Qualification,
        [property: JsonPropertyName("qualificationReason")] string? QualificationReason);

    private static class ResultJson
    {
        internal static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
        };
    }
}
