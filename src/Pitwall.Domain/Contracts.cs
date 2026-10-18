using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pitwall.Domain;

public static class SchemaJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public abstract record ExtensibleRecord
{
    public int SchemaVersion { get; init; } = 1;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

public enum FactType { Number, Boolean, Text, TextList }
public enum EvidenceOrigin { Observed, Inferred, UserReported, VendorDocumented }
public enum WorkOrderStatus { Intake, Diagnosing, AwaitingParts, AwaitingCustomer, Working, Testing, Ready, HandedOver, Abandoned }
public enum CapabilityState { NotPresent, Detected, ReadVerified, WriteVerified, WriteRevoked, Conflicted }
public enum CapabilityDirection { Read, Write, ReadWrite }
public enum ReportPhase { Intake, Handover }
public enum TestResult { Passed, Failed, Inconclusive, NotRun, Aborted }

public sealed record UnitMetadata(string? Original, string? Normalized, string? ConversionVersion);
public sealed record FactSource(string Provider, string Version, string ProvenanceReference);
public sealed record TimeWindow(DateTimeOffset Start, DateTimeOffset End);
public sealed record ObservedFact : ExtensibleRecord
{
    public required string Key { get; init; }
    public required FactType Type { get; init; }
    public required Observation<JsonElement> Reading { get; init; }
    public required string DeviceIdentity { get; init; }
    public required UnitMetadata Units { get; init; }
    public required FactSource Source { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public TimeWindow? Interval { get; init; }
    public ImmutableArray<Guid> EvidenceIds { get; init; } = [];
}

public sealed record MachineSnapshot : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required string LocalMachineKey { get; init; }
    public required string OsBuild { get; init; }
    public required DeviceIdentity Identity { get; init; }
    public ImmutableArray<string> ComponentIdentities { get; init; } = [];
    public ImmutableArray<PlatformCapability> Capabilities { get; init; } = [];
    public ImmutableDictionary<string, FactQuality> ProbeCoverage { get; init; } = ImmutableDictionary<string, FactQuality>.Empty;
    public ImmutableArray<ObservedFact> Facts { get; init; } = [];
}

public sealed record EvidenceItem : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required EvidenceOrigin Origin { get; init; }
    public Guid? SnapshotId { get; init; }
    public Guid? CaptureId { get; init; }
    public string? EventReference { get; init; }
    public required TimeWindow Window { get; init; }
    public string? ArtifactSha256 { get; init; }
    public required string AnalysisVersion { get; init; }
    public required string ProvenanceReference { get; init; }
}

public sealed record Customer : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    public string? Contact { get; init; }
    public string? Company { get; init; }
    public required string Language { get; init; }
    public string? Notes { get; init; }
    public bool IsImplicit { get; init; }
}

public sealed record Device : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required Guid CustomerId { get; init; }
    public required DeviceIdentity Identity { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsImplicit { get; init; }
}

public sealed record WorkOrder : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required string JobNumber { get; init; }
    public required Guid CustomerId { get; init; }
    public required Guid DeviceId { get; init; }
    public string? Complaint { get; init; }
    public string? Technician { get; init; }
    public ImmutableArray<string> AuthorizedScope { get; init; } = [];
    public required WorkOrderStatus Status { get; init; }
    public TimeSpan? BillableTime { get; init; }
    public ImmutableArray<string> Parts { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public bool IsImplicit { get; init; }
}

public sealed record Session : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required Guid WorkOrderId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Technician { get; init; }
}

public sealed record PlatformCapability : ExtensibleRecord
{
    public required string Vendor { get; init; }
    public required string ModelTuple { get; init; }
    public string? BiosRange { get; init; }
    public required string Capability { get; init; }
    public required CapabilityDirection Direction { get; init; }
    public required CapabilityState State { get; init; }
    public string? CatalogueReference { get; init; }
}
public sealed record TestRun : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required string TestVersion { get; init; }
    public required string Subsystem { get; init; }
    public TimeSpan? Duration { get; init; }
    public ImmutableArray<string> Coverage { get; init; } = [];
    public required TestResult Result { get; init; }
    public ImmutableArray<Guid> EvidenceIds { get; init; } = [];
    public string? Technician { get; init; }
    public ImmutableDictionary<string, FactQuality> StopConditionOutcomes { get; init; } = ImmutableDictionary<string, FactQuality>.Empty;
}
public sealed record ConditionReport : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required Guid WorkOrderId { get; init; }
    public required ReportPhase Phase { get; init; }
    public ImmutableArray<Guid> TestRunIds { get; init; } = [];
    public string? SignatureReference { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? DiffReference { get; init; }
}
public sealed record ConsentArtifact : ExtensibleRecord
{
    public required Guid Id { get; init; }
    public required Guid WorkOrderId { get; init; }
    public required string Type { get; init; }
    public required string Language { get; init; }
    public required string TemplateVersion { get; init; }
    public required string RenderedSha256 { get; init; }
    public string? SignatureReference { get; init; }
    public ImmutableArray<string> Scope { get; init; } = [];
}
