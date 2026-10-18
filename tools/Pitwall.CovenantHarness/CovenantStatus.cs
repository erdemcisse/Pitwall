namespace Pitwall.CovenantHarness;

public enum CovenantStatus
{
    Pass = 0,
    Fail = 1,
    NotYetMeasurable = 2,
}

public sealed record CovenantMeasurement
{
    public double NumericValue { get; init; }

    public string Unit { get; init; }

    public string Method { get; init; }

    public string? Note { get; init; }

    public CovenantMeasurement(double numericValue, string unit, string method, string? note = null)
    {
        if (!double.IsFinite(numericValue))
        {
            throw new ArgumentException("covenant.measurement_non_finite", nameof(numericValue));
        }

        if (string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException("covenant.measurement_unit_required", nameof(unit));
        }

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("covenant.measurement_method_required", nameof(method));
        }

        NumericValue = numericValue;
        Unit = unit;
        Method = method;
        Note = note;
    }
}

public sealed record CovenantCommitmentResult
{
    public const string QualificationFinal = "final";

    public const string QualificationProvisional = "provisional";

    public string CommitmentId { get; init; }

    public CovenantStatus Status { get; init; }

    public CovenantMeasurement? Measurement { get; init; }

    public string? BlockedBy { get; init; }

    public string? Qualification { get; init; }

    public string? QualificationReason { get; init; }

    public CovenantCommitmentResult(
        string commitmentId,
        CovenantStatus status,
        CovenantMeasurement? measurement,
        string? blockedBy,
        string? qualification = null,
        string? qualificationReason = null)
    {
        if (string.IsNullOrWhiteSpace(commitmentId))
        {
            throw new ArgumentException("covenant.commitment_id_required", nameof(commitmentId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status == CovenantStatus.NotYetMeasurable)
        {
            if (string.IsNullOrWhiteSpace(blockedBy))
            {
                throw new ArgumentException("covenant.blockedby_required", nameof(blockedBy));
            }

            if (measurement is not null)
            {
                throw new ArgumentException("covenant.notmeasurable_no_measurement", nameof(measurement));
            }

            if (qualification is not null || qualificationReason is not null)
            {
                throw new ArgumentException("covenant.notmeasurable_no_qualification", nameof(qualification));
            }
        }
        else
        {
            if (measurement is null)
            {
                throw new ArgumentException("covenant.measurement_required", nameof(measurement));
            }

            if (!string.IsNullOrWhiteSpace(blockedBy))
            {
                throw new ArgumentException("covenant.passfail_no_blockedby", nameof(blockedBy));
            }

            if (qualification != QualificationFinal && qualification != QualificationProvisional)
            {
                throw new ArgumentException("covenant.qualification_required", nameof(qualification));
            }

            if (string.Equals(qualification, QualificationProvisional, StringComparison.Ordinal)
                != !string.IsNullOrWhiteSpace(qualificationReason))
            {
                throw new ArgumentException("covenant.qualification_reason_required", nameof(qualificationReason));
            }
        }

        CommitmentId = commitmentId;
        Status = status;
        Measurement = measurement;
        BlockedBy = blockedBy;
        Qualification = qualification;
        QualificationReason = qualificationReason;
    }

    public static CovenantCommitmentResult Pass(
        string commitmentId,
        CovenantMeasurement measurement,
        string qualification = QualificationFinal,
        string? qualificationReason = null) =>
        new(commitmentId, CovenantStatus.Pass, measurement, null, qualification, qualificationReason);

    public static CovenantCommitmentResult Fail(
        string commitmentId,
        CovenantMeasurement measurement,
        string qualification = QualificationFinal,
        string? qualificationReason = null) =>
        new(commitmentId, CovenantStatus.Fail, measurement, null, qualification, qualificationReason);

    public static CovenantCommitmentResult NotMeasurable(string commitmentId, string blockedBy) =>
        new(commitmentId, CovenantStatus.NotYetMeasurable, null, blockedBy);
}

public sealed record CovenantReport
{
    public string ManifestVersion { get; init; }

    public DateTimeOffset MeasuredAt { get; init; }

    public IReadOnlyDictionary<string, CovenantCommitmentResult> Results { get; init; }

    public bool HasFail => Results.Values.Any(static result => result.Status == CovenantStatus.Fail);

    public CovenantReport(
        string manifestVersion,
        DateTimeOffset measuredAt,
        IReadOnlyDictionary<string, CovenantCommitmentResult> results)
    {
        if (string.IsNullOrWhiteSpace(manifestVersion))
        {
            throw new ArgumentException("covenant.manifest_version_required", nameof(manifestVersion));
        }

        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            throw new ArgumentException("covenant.results_required", nameof(results));
        }

        Results = results;
        ManifestVersion = manifestVersion;
        MeasuredAt = measuredAt;
    }

    public static CovenantReport Create(
        CovenantManifest manifest,
        DateTimeOffset measuredAt,
        IEnumerable<CovenantCommitmentResult> results)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(results);

        var byId = results.ToDictionary(static result => result.CommitmentId, StringComparer.Ordinal);
        var manifestIds = manifest.Commitments.Select(static commitment => commitment.Id).ToHashSet(StringComparer.Ordinal);

        var missing = manifestIds.Except(byId.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                "covenant.result_omits_commitments: " + string.Join(",", missing),
                nameof(results));
        }

        var extra = byId.Keys.Except(manifestIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length != 0)
        {
            throw new ArgumentException(
                "covenant.result_unknown_commitments: " + string.Join(",", extra),
                nameof(results));
        }

        return new CovenantReport(
            manifest.ManifestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            measuredAt,
            byId);
    }
}
