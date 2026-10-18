using System.Diagnostics;
using System.Text.Json;
using Pitwall.CovenantHarness;

namespace Pitwall.Tests;

public sealed class CovenantTests
{
    private static string ManifestPath =>
        Path.Combine(Fixtures.RepositoryRoot, "tools", "Pitwall.CovenantHarness", "covenant-manifest.json");

    private static CovenantManifest LoadManifest() => ManifestLoader.Load(ManifestPath);

    private static double NumericThreshold(string commitmentId, int index)
    {
        var commitment = LoadManifest().Commitments.Single(c => c.Id == commitmentId);
        return commitment.Thresholds[index].AsDouble()
            ?? throw new InvalidOperationException("test manifest threshold is not numeric:" + commitmentId);
    }

    [Fact]
    public void Manifest_tracks_all_ten_adr007_rows_and_marks_chart_target_internal()
    {
        var manifest = LoadManifest();
        Assert.Equal(2, manifest.ManifestVersion);
        Assert.Equal("docs/06-ARCHITECTURE-DELTA.md", manifest.SourceDocument);
        Assert.Equal("ADR-007 §2 and §2.1", manifest.SourceSection);
        Assert.Equal(10, manifest.Commitments.Count);

        var byId = manifest.Commitments.ToDictionary(static commitment => commitment.Id, StringComparer.Ordinal);
        Assert.Equal(
            new[]
            {
                "background-processes", "windows-services", "autostart-entries", "idle-working-set",
                "idle-cpu", "sensor-cpu", "launch-time", "chart-capture", "uninstall-residue", "telemetry",
            },
            manifest.Commitments.Select(static commitment => commitment.Id).ToArray());

        foreach (var commitment in manifest.Commitments)
        {
            Assert.Equal(
                commitment.Id == "chart-capture" ? "ADR-007 §2.1" : "ADR-007 §2",
                commitment.AdrSection);
            Assert.False(string.IsNullOrWhiteSpace(commitment.Name));
            Assert.False(string.IsNullOrWhiteSpace(commitment.ThresholdDisplay));
            Assert.NotEmpty(commitment.Thresholds);
        }

        Assert.Equal("0", byId["background-processes"].ThresholdDisplay);
        Assert.Equal("0", byId["windows-services"].ThresholdDisplay);
        Assert.Equal(
            "0 by default; opt-in only, visible in our own startup inventory",
            byId["autostart-entries"].ThresholdDisplay);
        Assert.Equal("≤ 250 MB", byId["idle-working-set"].ThresholdDisplay);
        Assert.Equal("≤ 0.5 %", byId["idle-cpu"].ThresholdDisplay);
        Assert.Equal("≤ 1 % total CPU", byId["sensor-cpu"].ThresholdDisplay);
        Assert.Equal("≤ 6 s (warm ≤ 3 s)", byId["launch-time"].ThresholdDisplay);
        Assert.Equal(
            "Internal target only: 60 fps sustained; capture overhead < 2 %; not a public commitment",
            byId["chart-capture"].ThresholdDisplay);
        Assert.Equal(
            "Nothing self-reinstalling. No UEFI-seeded installer. No orphan service.",
            byId["uninstall-residue"].ThresholdDisplay);
        Assert.Equal("Off, with no remote switch to enable it", byId["telemetry"].ThresholdDisplay);
    }

    [Fact]
    public void Manifest_threshold_values_match_adr007_numbers()
    {
        var manifest = LoadManifest();
        var byId = manifest.Commitments.ToDictionary(static commitment => commitment.Id, StringComparer.Ordinal);

        AssertSingleNumeric(byId["background-processes"], 0, "count", "equal");
        AssertSingleNumeric(byId["windows-services"], 0, "count", "equal");
        AssertSingleNumeric(byId["autostart-entries"], 0, "count", "equal");
        AssertSingleNumeric(byId["idle-working-set"], 250, "MB", "lessOrEqual");
        AssertSingleNumeric(byId["idle-cpu"], 0.5, "percent", "lessOrEqual");
        AssertSingleNumeric(byId["sensor-cpu"], 1, "percent", "lessOrEqual");
        AssertSingleNumeric(byId["uninstall-residue"], 0, "residue-items", "equal");

        var launch = byId["launch-time"];
        Assert.Equal(2, launch.Thresholds.Count);
        Assert.Equal(6, launch.Thresholds[0].AsDouble());
        Assert.Equal("seconds", launch.Thresholds[0].Unit);
        Assert.Equal("lessOrEqual", launch.Thresholds[0].Direction);
        Assert.Equal(3, launch.Thresholds[1].AsDouble());
        Assert.Equal("seconds", launch.Thresholds[1].Unit);
        Assert.Equal("lessOrEqual", launch.Thresholds[1].Direction);

        var chart = byId["chart-capture"];
        Assert.Equal(2, chart.Thresholds.Count);
        Assert.Equal(60, chart.Thresholds[0].AsDouble());
        Assert.Equal("fps", chart.Thresholds[0].Unit);
        Assert.Equal("greaterOrEqual", chart.Thresholds[0].Direction);
        Assert.Equal(2, chart.Thresholds[1].AsDouble());
        Assert.Equal("percent", chart.Thresholds[1].Unit);
        Assert.Equal("lessThan", chart.Thresholds[1].Direction);

        var telemetry = byId["telemetry"];
        Assert.Single(telemetry.Thresholds);
        Assert.Equal("off", telemetry.Thresholds[0].AsString());
        Assert.Equal("state", telemetry.Thresholds[0].Unit);
        Assert.Equal("equal", telemetry.Thresholds[0].Direction);

        static void AssertSingleNumeric(CovenantCommitment commitment, double value, string unit, string direction)
        {
            Assert.Single(commitment.Thresholds);
            Assert.Equal(value, commitment.Thresholds[0].AsDouble());
            Assert.Equal(unit, commitment.Thresholds[0].Unit);
            Assert.Equal(direction, commitment.Thresholds[0].Direction);
        }
    }

    [Fact]
    public void Manifest_missing_row_fails_naming_the_id()
    {
        var manifest = LoadManifest();
        var dropped = manifest with
        {
            Commitments = manifest.Commitments.Where(static c => c.Id != "uninstall-residue").ToArray(),
        };
        var exception = Assert.Throws<InvalidDataException>(() => ManifestLoader.Validate(dropped));
        Assert.Contains("covenant.manifest_missing_commitment:uninstall-residue", exception.Message);
    }

    [Fact]
    public void Manifest_wrong_direction_is_refused_at_load()
    {
        var manifest = WithDirection(LoadManifest(), "idle-working-set", 0, "greaterOrEqual");
        var exception = Assert.Throws<InvalidDataException>(() => ManifestLoader.Validate(manifest));
        Assert.Contains("covenant.manifest_direction_invalid:idle-working-set", exception.Message);
    }

    [Fact]
    public void Manifest_wrong_threshold_type_is_refused_at_load()
    {
        var manifest = WithValue(LoadManifest(), "idle-working-set", 0, "\"plenty\"");
        var exception = Assert.Throws<InvalidDataException>(() => ManifestLoader.Validate(manifest));
        Assert.Contains("covenant.manifest_threshold_type_invalid:idle-working-set", exception.Message);
    }

    [Fact]
    public void Report_requires_every_manifest_commitment()
    {
        var manifest = LoadManifest();
        var full = HealthyResults(manifest, includeFail: false);
        var report = CovenantReport.Create(manifest, DateTimeOffset.UtcNow, full);
        Assert.Equal(10, report.Results.Count);
        Assert.False(report.HasFail);

        var omitted = full.Where(static result => result.CommitmentId != "telemetry").ToArray();
        Assert.Throws<ArgumentException>(() => CovenantReport.Create(manifest, DateTimeOffset.UtcNow, omitted));

        var extra = full.Append(CovenantCommitmentResult.Pass(
            "invented-commitment",
            new CovenantMeasurement(0, "count", "test", null))).ToArray();
        Assert.Throws<ArgumentException>(() => CovenantReport.Create(manifest, DateTimeOffset.UtcNow, extra));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NotMeasurable_requires_nonempty_blockedby(string blockedBy)
    {
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("sensor-cpu", CovenantStatus.NotYetMeasurable, null, blockedBy));
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("sensor-cpu", CovenantStatus.NotYetMeasurable, null, null));
    }

    [Fact]
    public void NotMeasurable_rejects_a_measurement()
    {
        var measurement = new CovenantMeasurement(0, "count", "test", null);
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("sensor-cpu", CovenantStatus.NotYetMeasurable, measurement, "blocked"));
    }

    [Fact]
    public void NotMeasurable_rejects_a_qualification()
    {
        Assert.Throws<ArgumentException>(() => new CovenantCommitmentResult(
            "sensor-cpu", CovenantStatus.NotYetMeasurable, null, "blocked",
            CovenantCommitmentResult.QualificationProvisional, "some_reason"));
    }

    [Fact]
    public void Pass_and_fail_require_a_measurement_and_reject_blockedby()
    {
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("idle-working-set", CovenantStatus.Pass, null, null));
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("idle-working-set", CovenantStatus.Fail, null, null));
        var measurement = new CovenantMeasurement(1, "MB", "test", null);
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("idle-working-set", CovenantStatus.Pass, measurement, "blocked"));
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("idle-working-set", CovenantStatus.Fail, measurement, "blocked"));
    }

    [Fact]
    public void Measured_results_require_an_explicit_qualification()
    {
        var measurement = new CovenantMeasurement(1, "MB", "test", null);
        Assert.Throws<ArgumentException>(() =>
            new CovenantCommitmentResult("idle-working-set", CovenantStatus.Pass, measurement, null));
        Assert.Throws<ArgumentException>(() => CovenantCommitmentResult.Pass(
            "idle-working-set", measurement, "maybe", null));
        Assert.Throws<ArgumentException>(() => CovenantCommitmentResult.Pass(
            "idle-working-set", measurement, CovenantCommitmentResult.QualificationProvisional, null));
        Assert.Throws<ArgumentException>(() => CovenantCommitmentResult.Pass(
            "idle-working-set", measurement, CovenantCommitmentResult.QualificationProvisional, "   "));
        Assert.Throws<ArgumentException>(() => CovenantCommitmentResult.Fail(
            "idle-working-set", measurement, CovenantCommitmentResult.QualificationFinal, "unneeded"));
    }

    [Fact]
    public void Measurement_rejects_missing_method_and_nonfinite_values()
    {
        Assert.Throws<ArgumentException>(() => new CovenantMeasurement(1, "", "method", null));
        Assert.Throws<ArgumentException>(() => new CovenantMeasurement(1, "MB", "", null));
        Assert.Throws<ArgumentException>(() => new CovenantMeasurement(double.NaN, "MB", "method", null));
        Assert.Throws<ArgumentException>(() => new CovenantMeasurement(double.PositiveInfinity, "percent", "method", null));
    }

    [Fact]
    public async Task Measurement_rejects_zero_settle_delay()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Measurements.MeasureRunningAppAsync(
            "test-target.exe",
            1,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            CancellationToken.None));
    }

    [Fact]
    public async Task Child_stream_cleanup_is_bounded_and_cancellable()
    {
        using var cancellation = new CancellationTokenSource();
        using var stdoutReader = new StreamReader(Stream.Null);
        using var stderrReader = new StreamReader(Stream.Null);
        var stdout = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        await Measurements.DrainStreamsAsync(stdoutReader, stderrReader, stdout.Task, stderr.Task, cancellation);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"cleanup took {stopwatch.Elapsed}");
        Assert.True(cancellation.IsCancellationRequested);
        stdout.SetResult(string.Empty);
        stderr.SetResult(string.Empty);
    }

    [Fact]
    public async Task Child_stream_drain_discards_large_output_with_a_fixed_buffer_and_cancels()
    {
        const int outputLength = 2_000_000;
        using var cancellation = new CancellationTokenSource();
        var stdout = new BulkTextReader(outputLength);
        var stderr = new BulkTextReader(outputLength);

        await Task.WhenAll(
            Measurements.DrainStreamAsync(stdout, cancellation.Token),
            Measurements.DrainStreamAsync(stderr, cancellation.Token));

        Assert.Equal(outputLength, stdout.TotalRead);
        Assert.Equal(outputLength, stderr.TotalRead);
        Assert.True(stdout.MaximumRequestedLength <= 4096);
        Assert.True(stderr.MaximumRequestedLength <= 4096);

        using var blockedCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Measurements.DrainStreamAsync(new BlockingTextReader(), blockedCancellation.Token));
    }

    private sealed class BulkTextReader(int length) : TextReader
    {
        private int remaining = length;

        public int TotalRead { get; private set; }

        public int MaximumRequestedLength { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumRequestedLength = Math.Max(MaximumRequestedLength, buffer.Length);
            if (remaining == 0)
            {
                return ValueTask.FromResult(0);
            }

            var read = Math.Min(remaining, buffer.Length);
            buffer.Span[..read].Fill('x');
            remaining -= read;
            TotalRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class BlockingTextReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Theory]
    [InlineData(250.0, CovenantStatus.Pass)]
    [InlineData(0.0, CovenantStatus.Pass)]
    [InlineData(250.0001, CovenantStatus.Fail)]
    [InlineData(400.0, CovenantStatus.Fail)]
    public void Classifier_memory_boundary(double megabytes, CovenantStatus expected) =>
        Assert.Equal(
            expected,
            CovenantClassifier.ClassifyMemoryMegabytes(megabytes, NumericThreshold("idle-working-set", 0)));

    [Fact]
    public void Classifier_memory_follows_the_manifest_value()
    {
        Assert.Equal(CovenantStatus.Fail, CovenantClassifier.ClassifyMemoryMegabytes(44.56, 5.0));
        Assert.Equal(CovenantStatus.Pass, CovenantClassifier.ClassifyMemoryMegabytes(5.0, 5.0));
    }

    [Theory]
    [InlineData(0.5, CovenantStatus.Pass)]
    [InlineData(0.0, CovenantStatus.Pass)]
    [InlineData(0.500001, CovenantStatus.Fail)]
    [InlineData(5.0, CovenantStatus.Fail)]
    public void Classifier_idle_cpu_boundary(double percent, CovenantStatus expected) =>
        Assert.Equal(
            expected,
            CovenantClassifier.ClassifyIdleCpuPercent(percent, NumericThreshold("idle-cpu", 0)));

    [Fact]
    public void Classifier_idle_cpu_follows_the_manifest_value()
    {
        Assert.Equal(CovenantStatus.Fail, CovenantClassifier.ClassifyIdleCpuPercent(0.195, 0.0001));
        Assert.Equal(CovenantStatus.Pass, CovenantClassifier.ClassifyIdleCpuPercent(0.0001, 0.0001));
    }

    [Theory]
    [InlineData(1.0, CovenantStatus.Pass)]
    [InlineData(1.0001, CovenantStatus.Fail)]
    public void Classifier_sensor_boundary(double percent, CovenantStatus expected) =>
        Assert.Equal(
            expected,
            CovenantClassifier.ClassifySensorCpuPercent(percent, NumericThreshold("sensor-cpu", 0)));

    [Theory]
    [InlineData(6.0, 3.0, CovenantStatus.Pass)]
    [InlineData(0.5, 0.5, CovenantStatus.Pass)]
    [InlineData(6.0001, 2.0, CovenantStatus.Fail)]
    [InlineData(5.0, 3.0001, CovenantStatus.Fail)]
    [InlineData(30.0, 15.0, CovenantStatus.Fail)]
    public void Classifier_launch_boundary(double cold, double warm, CovenantStatus expected) =>
        Assert.Equal(
            expected,
            CovenantClassifier.ClassifyLaunch(cold, warm, NumericThreshold("launch-time", 0), NumericThreshold("launch-time", 1)));

    [Theory]
    [InlineData(60.0, 1.99, CovenantStatus.Pass)]
    [InlineData(120.0, 0.0, CovenantStatus.Pass)]
    [InlineData(60.0, 2.0, CovenantStatus.Fail)]
    [InlineData(60.0, 2.0001, CovenantStatus.Fail)]
    [InlineData(59.9, 1.0, CovenantStatus.Fail)]
    public void Classifier_chart_requires_sixty_fps_and_strictly_below_two_percent(
        double fps, double overhead, CovenantStatus expected) =>
        Assert.Equal(
            expected,
            CovenantClassifier.ClassifyChart(fps, overhead, NumericThreshold("chart-capture", 0), NumericThreshold("chart-capture", 1)));

    [Theory]
    [InlineData(0, CovenantStatus.Pass)]
    [InlineData(1, CovenantStatus.Fail)]
    [InlineData(2, CovenantStatus.Fail)]
    public void Classifier_counts(int count, CovenantStatus expected)
    {
        Assert.Equal(expected, CovenantClassifier.ClassifyBackgroundProcesses(count, NumericThreshold("background-processes", 0)));
        Assert.Equal(expected, CovenantClassifier.ClassifyServices(count, NumericThreshold("windows-services", 0)));
        Assert.Equal(expected, CovenantClassifier.ClassifyAutostartEntries(count, NumericThreshold("autostart-entries", 0)));
    }

    [Theory]
    [InlineData(0, CovenantStatus.Pass)]
    [InlineData(1, CovenantStatus.Fail)]
    [InlineData(3, CovenantStatus.Fail)]
    public void Classifier_telemetry_state(int references, CovenantStatus expected)
    {
        var state = LoadManifest().Commitments.Single(static c => c.Id == "telemetry").Thresholds[0].AsString();
        Assert.Equal("off", state);
        Assert.Equal(expected, CovenantClassifier.ClassifyTelemetryState(references, state));
    }

    [Fact]
    public void Classifier_telemetry_rejects_a_non_off_state()
    {
        Assert.Throws<ArgumentException>(() => CovenantClassifier.ClassifyTelemetryState(0, "on"));
        Assert.Throws<ArgumentException>(() => CovenantClassifier.ClassifyTelemetryState(0, null));
    }

    [Fact]
    public void Classifier_absent_or_errored_never_becomes_pass()
    {
        Assert.Throws<ArgumentException>(() => CovenantClassifier.ClassifyMemoryMegabytes(double.NaN, NumericThreshold("idle-working-set", 0)));
        Assert.Throws<ArgumentException>(() => CovenantClassifier.ClassifyIdleCpuPercent(double.PositiveInfinity, NumericThreshold("idle-cpu", 0)));
        Assert.Throws<ArgumentException>(() => CovenantClassifier.ClassifyChart(60, double.NaN, NumericThreshold("chart-capture", 0), NumericThreshold("chart-capture", 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantClassifier.ClassifyBackgroundProcesses(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantClassifier.ClassifyBackgroundProcesses(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CovenantClassifier.ClassifyBackgroundProcesses(0, 0.5));
    }

    [Fact]
    public void BuildResults_tightened_manifest_fails()
    {
        var manifest = WithValue(LoadManifest(), "idle-working-set", 0, "5");
        manifest = WithValue(manifest, "idle-cpu", 0, "0.0001");
        var results = CovenantBuilder.BuildResults(
            manifest, Launch(), Running(privateMb: 44.56, cpu: 0.195), 0, 0, 0, 3, 5);
        var memory = results.Single(static r => r.CommitmentId == "idle-working-set");
        Assert.Equal(CovenantStatus.Fail, memory.Status);
        Assert.Equal(44.56, memory.Measurement!.NumericValue);
        Assert.Contains("44.6 MB", Assert.IsType<string>(memory.Measurement!.Note), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResults_missing_row_fails_naming_the_id()
    {
        var loaded = LoadManifest();
        var manifest = loaded with
        {
            Commitments = loaded.Commitments.Where(static c => c.Id != "uninstall-residue").ToArray(),
        };
        var exception = Assert.Throws<InvalidDataException>(() => CovenantBuilder.BuildResults(
            manifest, Launch(), Running(), 0, 0, 0, 3, 5));
        Assert.Contains("covenant.manifest_missing_commitment:uninstall-residue", exception.Message);
    }

    [Fact]
    public void BuildResults_extra_row_is_refused()
    {
        using var document = JsonDocument.Parse("0");
        var invented = new CovenantCommitment
        {
            Id = "invented-commitment",
            Name = "A commitment the harness has never heard of",
            ThresholdDisplay = "0",
            Thresholds = [new CovenantThreshold { Value = document.RootElement.Clone(), Unit = "count", Direction = "equal" }],
            AdrSection = "ADR-007 §2",
        };
        var loaded = LoadManifest();
        var manifest = loaded with
        {
            Commitments = loaded.Commitments.Append(invented).ToArray(),
        };
        var exception = Assert.Throws<InvalidDataException>(() => CovenantBuilder.BuildResults(
            manifest, Launch(), Running(), 0, 0, 0, 3, 5));
        Assert.Contains("covenant.unknown_commitment:invented-commitment", exception.Message);
    }

    [Fact]
    public void BuildResults_launch_without_a_shown_window_fails()
    {
        var passing = CovenantBuilder.BuildResults(
            LoadManifest(), Launch(), Running(), 0, 0, 0, 3, 5);
        Assert.Equal(CovenantStatus.Pass, passing.Single(static r => r.CommitmentId == "launch-time").Status);

        var coldNeverShown = CovenantBuilder.BuildResults(
            LoadManifest(), Launch(coldShown: false), Running(), 0, 0, 0, 3, 5);
        var cold = coldNeverShown.Single(static r => r.CommitmentId == "launch-time");
        Assert.Equal(CovenantStatus.Fail, cold.Status);
        Assert.Contains("never shown", Assert.IsType<string>(cold.Measurement!.Note), StringComparison.Ordinal);

        var warmNeverShown = CovenantBuilder.BuildResults(
            LoadManifest(), Launch(warmShown: false), Running(), 0, 0, 0, 3, 5);
        Assert.Equal(CovenantStatus.Fail, warmNeverShown.Single(static r => r.CommitmentId == "launch-time").Status);
    }

    [Fact]
    public void BuildResults_idle_cpu_is_not_measurable()
    {
        var results = CovenantBuilder.BuildResults(
            LoadManifest(), Launch(), Running(cpu: 0.02), 0, 0, 0, 10, 5);
        var cpu = results.Single(static r => r.CommitmentId == "idle-cpu");
        Assert.Equal(CovenantStatus.NotYetMeasurable, cpu.Status);
        Assert.Null(cpu.Measurement);
        Assert.Contains("5-minute", Assert.IsType<string>(cpu.BlockedBy), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildResults_marks_every_conditional_pass()
    {
        var results = CovenantBuilder.BuildResults(
            LoadManifest(), Launch(), Running(), 0, 0, 0, 10, 5);
        var byId = results.ToDictionary(static r => r.CommitmentId, StringComparer.Ordinal);

        AssertProvisional(byId["idle-working-set"], "empty_window_baseline");
        AssertProvisional(byId["windows-services"], "trivially_zero_no_installer");
        AssertProvisional(byId["telemetry"], "static_check_only");
        AssertProvisional(byId["launch-time"], "warm_file_cache_not_cold_start");

        Assert.Equal(CovenantCommitmentResult.QualificationFinal, byId["background-processes"].Qualification);
        Assert.Null(byId["background-processes"].QualificationReason);
        Assert.Equal(CovenantCommitmentResult.QualificationFinal, byId["autostart-entries"].Qualification);
        Assert.Null(byId["autostart-entries"].QualificationReason);

        foreach (var id in new[] { "idle-cpu", "sensor-cpu", "chart-capture", "uninstall-residue" })
        {
            Assert.Equal(CovenantStatus.NotYetMeasurable, byId[id].Status);
            Assert.False(string.IsNullOrWhiteSpace(byId[id].BlockedBy));
        }

        static void AssertProvisional(CovenantCommitmentResult result, string reason)
        {
            Assert.Equal(CovenantStatus.Pass, result.Status);
            Assert.Equal(CovenantCommitmentResult.QualificationProvisional, result.Qualification);
            Assert.Equal(reason, result.QualificationReason);
        }
    }

    [Fact]
    public void Fail_hook_regresses_a_measurable_row_and_refuses_idle_cpu()
    {
        var results = CovenantBuilder.BuildResults(
            LoadManifest(), Launch(), Running(), 0, 0, 0, 10, 5);
        var regressed = CovenantBuilder.ApplyFailHook(results, "idle-working-set");
        var memory = regressed.Single(static r => r.CommitmentId == "idle-working-set");
        Assert.Equal(CovenantStatus.Fail, memory.Status);
        Assert.Equal(CovenantStatus.Pass, regressed.Single(static r => r.CommitmentId == "launch-time").Status);

        Assert.Throws<InvalidOperationException>(() => CovenantBuilder.ApplyFailHook(results, "idle-cpu"));
    }

    [Fact]
    public void Report_hasfail_reflects_any_fail()
    {
        var manifest = LoadManifest();
        Assert.False(CovenantReport.Create(manifest, DateTimeOffset.UtcNow, HealthyResults(manifest, includeFail: false)).HasFail);
        Assert.True(CovenantReport.Create(manifest, DateTimeOffset.UtcNow, HealthyResults(manifest, includeFail: true)).HasFail);
    }

    private static AppObservation Running(double privateMb = 21.0, double cpu = 0.05) =>
        new(4242, privateMb, 75.0, cpu, 3, 0, 0);

    private static LaunchObservation Launch(bool coldShown = true, bool warmShown = true) =>
        new(0.33, 0.22, coldShown, warmShown, 111, 222);

    private static CovenantManifest WithValue(CovenantManifest manifest, string id, int thresholdIndex, string jsonValue)
    {
        using var document = JsonDocument.Parse(jsonValue);
        var value = document.RootElement.Clone();
        return manifest with
        {
            Commitments = manifest.Commitments.Select(commitment => commitment.Id == id
                ? commitment with
                {
                    Thresholds = commitment.Thresholds.Select((threshold, index) => index == thresholdIndex
                        ? threshold with { Value = value }
                        : threshold).ToArray(),
                }
                : commitment).ToArray(),
        };
    }

    private static CovenantManifest WithDirection(CovenantManifest manifest, string id, int thresholdIndex, string direction) =>
        manifest with
        {
            Commitments = manifest.Commitments.Select(commitment => commitment.Id == id
                ? commitment with
                {
                    Thresholds = commitment.Thresholds.Select((threshold, index) => index == thresholdIndex
                        ? threshold with { Direction = direction }
                        : threshold).ToArray(),
                }
                : commitment).ToArray(),
        };

    private static CovenantCommitmentResult[] HealthyResults(CovenantManifest manifest, bool includeFail)
    {
        var results = new List<CovenantCommitmentResult>();
        foreach (var commitment in manifest.Commitments)
        {
            if (commitment.Id is "sensor-cpu" or "chart-capture" or "uninstall-residue" or "idle-cpu")
            {
                var blocker = commitment.Id == "idle-cpu"
                    ? "test-blocker: five-minute average needs a 300s sample"
                    : "test-blocker:" + commitment.Id;
                results.Add(CovenantCommitmentResult.NotMeasurable(commitment.Id, blocker));
                continue;
            }

            var measurement = new CovenantMeasurement(0, "count", "test", null);
            if (includeFail && commitment.Id == "idle-working-set")
            {
                results.Add(CovenantCommitmentResult.Fail(commitment.Id, new CovenantMeasurement(400, "MB", "test", null)));
            }
            else if (commitment.Id == "idle-working-set")
            {
                results.Add(CovenantCommitmentResult.Pass(commitment.Id, new CovenantMeasurement(100, "MB", "test", null)));
            }
            else
            {
                results.Add(CovenantCommitmentResult.Pass(commitment.Id, measurement));
            }
        }

        return results.ToArray();
    }
}
