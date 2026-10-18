namespace Pitwall.CovenantHarness;

public static class CovenantBuilder
{
    public static List<CovenantCommitmentResult> BuildResults(
        CovenantManifest manifest,
        LaunchObservation launch,
        AppObservation running,
        int autostartCount,
        int serviceCount,
        int telemetryRefs,
        int sampleSeconds,
        int settleSeconds)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(running);
        ManifestLoader.Validate(manifest);

        var results = new List<CovenantCommitmentResult>(manifest.Commitments.Count);

        foreach (var commitment in manifest.Commitments)
        {
            CovenantCommitmentResult result = commitment.Id switch
            {
                "background-processes" => Classify(
                    commitment.Id,
                    CovenantClassifier.ClassifyBackgroundProcesses(
                        running.BackgroundProcesses, Threshold(commitment, 0)),
                    new CovenantMeasurement(
                        running.BackgroundProcesses,
                        "count",
                        "process-enumeration-by-image-path",
                        FormattableString.Invariant(
                            $"Same-name processes observed: {running.SameNameProcesses}. Main PID {running.ProcessId} excluded; only same-image-path peers count as background.")),
                    CovenantCommitmentResult.QualificationFinal,
                    null),

                "windows-services" => Classify(
                    commitment.Id,
                    CovenantClassifier.ClassifyServices(serviceCount, Threshold(commitment, 0)),
                    new CovenantMeasurement(
                        serviceCount,
                        "count",
                        "registry-services-scan-readonly",
                        "no installer exists; nothing can install a service. Trivially true: not a verified property of a shipped product."),
                    CovenantCommitmentResult.QualificationProvisional,
                    "trivially_zero_no_installer"),

                "autostart-entries" => Classify(
                    commitment.Id,
                    CovenantClassifier.ClassifyAutostartEntries(autostartCount, Threshold(commitment, 0)),
                    new CovenantMeasurement(
                        autostartCount,
                        "count",
                        "hkcu-run-keys-plus-startup-folder-readonly",
                        "Read-only scan of HKCU Run/RunOnce values and Startup folder names containing 'pitwall'. No writes."),
                    CovenantCommitmentResult.QualificationFinal,
                    null),

                "idle-working-set" => Classify(
                    commitment.Id,
                    CovenantClassifier.ClassifyMemoryMegabytes(running.PrivateMegabytes, Threshold(commitment, 0)),
                    new CovenantMeasurement(
                        running.PrivateMegabytes,
                        "MB",
                        FormattableString.Invariant($"process-private-memory-after-{settleSeconds}s-settle"),
                        FormattableString.Invariant(
                            $"Empty-window baseline, not proof the shipped product meets {Threshold(commitment, 0):F1} MB. Private {running.PrivateMegabytes:F1} MB after a {settleSeconds}s settle; working set {running.WorkingSetMegabytes:F1} MB; PID {running.ProcessId}.")),
                    CovenantCommitmentResult.QualificationProvisional,
                    "empty_window_baseline"),

                "idle-cpu" => CovenantCommitmentResult.NotMeasurable(
                    commitment.Id,
                    FormattableString.Invariant(
                        $"Idle CPU commitment requires a 5-minute average ({Measurements.CommitmentWindowSeconds}s total CPU); the gate records a {sampleSeconds}s regression-signal sample after a {settleSeconds}s settle, which cannot support the published window. Observed {running.CpuPercent:F4}% over {sampleSeconds}s is recorded in sample.running, not classified.")),

                "sensor-cpu" => CovenantCommitmentResult.NotMeasurable(
                    commitment.Id,
                    "No sensor subsystem exists (P0-06 / Phase 1)"),

                "launch-time" => ClassifyLaunch(commitment, launch),

                "chart-capture" => CovenantCommitmentResult.NotMeasurable(
                    commitment.Id,
                    "ADR-008, Deferred — see docs/ADR-008-CLOSURE-PLAN.md"),

                "uninstall-residue" => CovenantCommitmentResult.NotMeasurable(
                    commitment.Id,
                    "No installer or uninstaller exists (P4-01)"),

                "telemetry" => Classify(
                    commitment.Id,
                    CovenantClassifier.ClassifyTelemetryState(telemetryRefs, commitment.Thresholds[0].AsString()),
                    new CovenantMeasurement(
                        telemetryRefs,
                        "count",
                        "static-dependency-check",
                        "Static dependency/reference check only, not a runtime measurement. Scanned src/ for telemetry package and client references."),
                    CovenantCommitmentResult.QualificationProvisional,
                    "static_check_only"),

                _ => throw new InvalidDataException("covenant.unknown_commitment:" + commitment.Id),
            };
            results.Add(result);
        }

        return results;

        static CovenantCommitmentResult Classify(
            string id,
            CovenantStatus status,
            CovenantMeasurement measurement,
            string qualification,
            string? qualificationReason) =>
            status == CovenantStatus.Pass
                ? CovenantCommitmentResult.Pass(id, measurement, qualification, qualificationReason)
                : CovenantCommitmentResult.Fail(id, measurement, qualification, qualificationReason);
    }

    public static List<CovenantCommitmentResult> ApplyFailHook(List<CovenantCommitmentResult> results, string commitmentId)
    {
        const string HookMethod = "synthetic-regression-fixture";
        const string HookNote = "Test-only gate proof: forces Fail to verify Verify.ps1 fails. Not a product measurement.";
        var replaced = new List<CovenantCommitmentResult>(results.Count);
        foreach (var result in results)
        {
            if (!string.Equals(result.CommitmentId, commitmentId, StringComparison.Ordinal))
            {
                replaced.Add(result);
                continue;
            }

            CovenantMeasurement regressed = commitmentId switch
            {
                "background-processes" => new CovenantMeasurement(2, "count", HookMethod, HookNote),
                "windows-services" => new CovenantMeasurement(1, "count", HookMethod, HookNote),
                "autostart-entries" => new CovenantMeasurement(1, "count", HookMethod, HookNote),
                "idle-working-set" => new CovenantMeasurement(400, "MB", HookMethod, HookNote),
                "launch-time" => new CovenantMeasurement(30, "seconds", HookMethod, HookNote + " Cold 30s; warm 15s."),
                "telemetry" => new CovenantMeasurement(3, "count", HookMethod, HookNote),
                _ => throw new InvalidOperationException("covenant.fail_hook_unsupported:" + commitmentId),
            };
            replaced.Add(CovenantCommitmentResult.Fail(commitmentId, regressed));
        }

        return replaced;
    }

    private static CovenantCommitmentResult ClassifyLaunch(CovenantCommitment commitment, LaunchObservation launch)
    {
        var coldThreshold = Threshold(commitment, 0);
        var warmThreshold = Threshold(commitment, 1);
        var live = launch.ColdWindowShown && launch.WarmWindowShown;
        var status = live
            ? CovenantClassifier.ClassifyLaunch(launch.ColdSeconds, launch.WarmSeconds, coldThreshold, warmThreshold)
            : CovenantStatus.Fail;
        var note = live
            ? FormattableString.Invariant(
                $"Cold {launch.ColdSeconds:F2}s (threshold {coldThreshold:F2}s, shown={launch.ColdWindowShown}); warm {launch.WarmSeconds:F2}s (threshold {warmThreshold:F2}s, shown={launch.WarmWindowShown}). First of three launches on a warm file cache; not a cold start. {Measurements.InteractiveDefinition}")
            : FormattableString.Invariant(
                $"Window never shown (cold shown={launch.ColdWindowShown}, warm shown={launch.WarmWindowShown}); elapsed time alone cannot establish interactivity. Cold {launch.ColdSeconds:F2}s; warm {launch.WarmSeconds:F2}s. {Measurements.InteractiveDefinition}");
        var measurement = new CovenantMeasurement(launch.ColdSeconds, "seconds", "launch-to-window-shown", note);
        if (!live)
        {
            return CovenantCommitmentResult.Fail(commitment.Id, measurement);
        }

        return status == CovenantStatus.Pass
            ? CovenantCommitmentResult.Pass(
                commitment.Id, measurement,
                CovenantCommitmentResult.QualificationProvisional, "warm_file_cache_not_cold_start")
            : CovenantCommitmentResult.Fail(
                commitment.Id, measurement,
                CovenantCommitmentResult.QualificationProvisional, "warm_file_cache_not_cold_start");
    }

    private static double Threshold(CovenantCommitment commitment, int index)
    {
        if ((uint)index >= (uint)commitment.Thresholds.Count)
        {
            throw new InvalidDataException("covenant.manifest_thresholds_invalid:" + commitment.Id);
        }

        return commitment.Thresholds[index].AsDouble()
            ?? throw new InvalidDataException("covenant.manifest_threshold_type_invalid:" + commitment.Id);
    }
}
