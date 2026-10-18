using System.Collections.Immutable;
using System.Text.Json;

namespace Pitwall.Domain;

public enum ProbeErrorKind
{
    None,
    Timeout,
    Thrown,
    AccessDenied,
    SourceMissing,
    InvalidPayload,
}

public sealed record ProbeOutcome
{
    public string ProbeId { get; }
    public FactQuality Coverage { get; }
    public ImmutableArray<ObservedFact> Facts { get; }
    public TimeSpan Duration { get; }
    public FactSource Source { get; }
    public ProbeErrorKind ErrorKind { get; }
    public string? ErrorCode { get; }
    public string? Note { get; }

    private ProbeOutcome(
        string probeId,
        FactQuality coverage,
        ImmutableArray<ObservedFact> facts,
        TimeSpan duration,
        FactSource source,
        ProbeErrorKind errorKind,
        string? errorCode,
        string? note)
    {
        if (string.IsNullOrWhiteSpace(probeId)) throw new ArgumentException("probe.id_required", nameof(probeId));
        if (!Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(coverage));
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(errorKind)) throw new ArgumentOutOfRangeException(nameof(errorKind));

        var terminal = coverage is FactQuality.Unsupported or FactQuality.PermissionDenied or FactQuality.Unavailable;
        if (terminal)
        {
            foreach (var fact in facts)
            {
                if (fact is null || fact.Reading.HasPayload)
                    throw new ArgumentException("probe.terminal_forbids_payload", nameof(facts));
            }

            if (errorKind == ProbeErrorKind.None) throw new ArgumentException("probe.error_required", nameof(errorKind));
            if (string.IsNullOrWhiteSpace(errorCode)) throw new ArgumentException("probe.code_required", nameof(errorCode));
        }
        else if (coverage == FactQuality.Invalid)
        {
            if (errorKind == ProbeErrorKind.None) throw new ArgumentException("probe.error_required", nameof(errorKind));
            if (string.IsNullOrWhiteSpace(errorCode)) throw new ArgumentException("probe.code_required", nameof(errorCode));
        }
        else
        {
            if (facts.IsDefaultOrEmpty) throw new ArgumentException("probe.facts_required", nameof(facts));
            if (errorKind != ProbeErrorKind.None) throw new ArgumentException("probe.completed_no_error", nameof(errorKind));
            if (!string.IsNullOrWhiteSpace(errorCode)) throw new ArgumentException("probe.completed_no_code", nameof(errorCode));
        }

        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);
        }

        ProbeId = probeId;
        Coverage = coverage;
        Facts = facts.IsDefault ? ImmutableArray<ObservedFact>.Empty : facts;
        Duration = duration;
        Source = source;
        ErrorKind = errorKind;
        ErrorCode = errorCode;
        Note = note;
    }

    public static ProbeOutcome Completed(
        string probeId,
        ImmutableArray<ObservedFact> facts,
        TimeSpan duration,
        FactSource source,
        string? note = null) =>
        new(probeId, FactQuality.Valid, facts, duration, source, ProbeErrorKind.None, null, note);

    public static ProbeOutcome Partial(
        string probeId,
        ImmutableArray<ObservedFact> facts,
        TimeSpan duration,
        FactSource source,
        ProbeErrorKind errorKind,
        string errorCode,
        string? note = null) =>
        new(probeId, FactQuality.Invalid, facts, duration, source, errorKind, errorCode, note);

    public static ProbeOutcome Failed(
        string probeId,
        FactQuality coverage,
        TimeSpan duration,
        FactSource source,
        ProbeErrorKind errorKind,
        string errorCode,
        string? note = null) =>
        new(probeId, coverage, duration, source, errorKind, errorCode, note);

    public static ProbeOutcome Failed(
        string probeId,
        FactQuality coverage,
        ImmutableArray<ObservedFact> facts,
        TimeSpan duration,
        FactSource source,
        ProbeErrorKind errorKind,
        string errorCode,
        string? note = null)
    {
        if (coverage is not (FactQuality.Unsupported or FactQuality.PermissionDenied or FactQuality.Unavailable))
            throw new ArgumentException("probe.failed_coverage_invalid", nameof(coverage));
        return new ProbeOutcome(probeId, coverage, facts, duration, source, errorKind, errorCode, note);
    }

    private ProbeOutcome(
        string probeId,
        FactQuality coverage,
        TimeSpan duration,
        FactSource source,
        ProbeErrorKind errorKind,
        string errorCode,
        string? note)
        : this(probeId, coverage, ImmutableArray<ObservedFact>.Empty, duration, source, errorKind, errorCode, note)
    {
        if (coverage is not (FactQuality.Unsupported or FactQuality.PermissionDenied or FactQuality.Unavailable))
            throw new ArgumentException("probe.failed_coverage_invalid", nameof(coverage));
    }
}

public interface IInventoryProbe
{
    string ProbeId { get; }
    Task<ProbeOutcome> CollectAsync(CancellationToken cancellationToken);
}

public static class InventoryFact
{
    public static ObservedFact Text(
        string key, string device, string value, FactSource source, DateTimeOffset observedAt, UnitMetadata units) =>
        Build(key, device, FactType.Text, Observation<JsonElement>.Present(FactQuality.Valid, JsonSerializer.SerializeToElement(value)), source, observedAt, units);

    public static ObservedFact Number(
        string key, string device, long value, FactSource source, DateTimeOffset observedAt, UnitMetadata units) =>
        Build(key, device, FactType.Number, Observation<JsonElement>.Present(FactQuality.Valid, JsonSerializer.SerializeToElement(value)), source, observedAt, units);

    public static ObservedFact Real(
        string key, string device, double value, FactSource source, DateTimeOffset observedAt, UnitMetadata units)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("probe.non_finite", nameof(value));
        return Build(key, device, FactType.Number, Observation<JsonElement>.Present(FactQuality.Valid, JsonSerializer.SerializeToElement(value)), source, observedAt, units);
    }

    public static ObservedFact Boolean(
        string key, string device, bool value, FactSource source, DateTimeOffset observedAt, UnitMetadata units) =>
        Build(key, device, FactType.Boolean, Observation<JsonElement>.Present(FactQuality.Valid, JsonSerializer.SerializeToElement(value)), source, observedAt, units);

    public static ObservedFact Missing(
        string key, string device, FactType type, FactQuality quality, FactSource source, DateTimeOffset observedAt, UnitMetadata units)
    {
        if (quality is not (FactQuality.Unsupported or FactQuality.PermissionDenied or FactQuality.Unavailable or FactQuality.Invalid))
            throw new ArgumentException("probe.missing_quality_invalid", nameof(quality));
        return Build(key, device, type, Observation<JsonElement>.Missing(quality), source, observedAt, units);
    }

    private static ObservedFact Build(
        string key, string device, FactType type, Observation<JsonElement> reading,
        FactSource source, DateTimeOffset observedAt, UnitMetadata units)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("probe.key_required", nameof(key));
        if (string.IsNullOrWhiteSpace(device)) throw new ArgumentException("probe.device_required", nameof(device));
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(units);
        return new ObservedFact
        {
            Key = key,
            Type = type,
            Reading = reading,
            DeviceIdentity = device,
            Units = units,
            Source = source,
            ObservedAt = observedAt,
        };
    }
}
