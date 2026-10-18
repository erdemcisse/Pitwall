using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pitwall.Domain;
using Pitwall.Storage;

namespace Pitwall.Tests;

public sealed class SchemaTests
{
    [Fact]
    public async Task Partial_snapshot_roundtrip_preserves_unknowns_and_real_zero_false_empty()
    {
        var store = new SnapshotStore(Path.Combine(Fixtures.Sandbox(), "metadata.db"));
        await store.InitializeAsync();
        var snapshot = Fixtures.Snapshot();
        var personal = await store.StartPersonalSessionAsync(snapshot, "de");
        var restored = await store.LoadSnapshotAsync(snapshot.Id);
        Assert.Equal(JsonSerializer.Serialize(snapshot, SchemaJson.Options), JsonSerializer.Serialize(restored, SchemaJson.Options));
        Assert.Equal(7, Enum.GetValues<FactQuality>().Length);
        Assert.Equal(Enum.GetValues<FactQuality>().Order(), restored.Facts.Select(fact => fact.Reading.Quality).Distinct().Order());
        foreach (var fact in restored.Facts.Take(4))
        {
            Assert.False(fact.Reading.HasPayload);
            fact.Reading.Match<int>((_, _) => throw new InvalidOperationException("Unknown acquired a value."), quality => { Assert.NotEqual(FactQuality.Valid, quality); return 1; });
        }
        Assert.Equal(0, restored.Facts.Single(fact => fact.Key == "realZero").Reading.Match((q, p) => { Assert.Equal(FactQuality.Valid, q); return p.GetInt32(); }, _ => -1));
        Assert.False(restored.Facts.Single(fact => fact.Key == "realFalse").Reading.Match((q, p) => { Assert.Equal(FactQuality.Valid, q); return p.GetBoolean(); }, _ => true));
        Assert.Equal("", restored.Facts.Single(fact => fact.Key == "realEmpty").Reading.Match((q, p) => { Assert.Equal(FactQuality.Valid, q); return p.GetString(); }, _ => "missing"));
        Assert.True(personal.Customer.IsImplicit && personal.Device.IsImplicit && personal.WorkOrder.IsImplicit);
        Assert.Equal(personal.Customer.Id, personal.Device.CustomerId);
        Assert.Equal(personal.Device.Id, personal.WorkOrder.DeviceId);
        Assert.Equal(personal.WorkOrder.Id, personal.Session.WorkOrderId);
        var evidence = new EvidenceItem { Id = snapshot.Facts[0].EvidenceIds[0], Origin = EvidenceOrigin.Observed, SnapshotId = snapshot.Id,
            Window = new(snapshot.CapturedAt, snapshot.CapturedAt), AnalysisVersion = "fixture-v1", ProvenanceReference = "fixture:partial-snapshot" };
        await store.SaveEvidenceAsync(personal.Session.Id, evidence);
        Assert.Equal(evidence, await store.LoadEvidenceAsync(evidence.Id));
    }

    [Fact]
    public void Observation_has_no_unsafe_public_payload_api()
    {
        var type = typeof(Observation<int>);
        Assert.Null(type.GetProperty("Value"));
        Assert.Empty(type.GetFields());
        Assert.Empty(type.GetConstructors());
        Assert.DoesNotContain(type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly), method => method.Name is "op_Implicit" or "op_Explicit" || method.ReturnType == typeof(int));
        var match = type.GetMethod("Match")!;
        Assert.All(match.GetParameters(), parameter => Assert.Equal(typeof(FactQuality), parameter.ParameterType.GetGenericArguments()[0]));
        Assert.Throws<ArgumentException>(() => Observation<int>.Present(FactQuality.Unavailable, 0));
        Assert.Throws<ArgumentException>(() => Observation<int>.Missing(FactQuality.Valid));
    }

    [Theory]
    [InlineData("{\"hasPayload\":false}")]
    [InlineData("{\"quality\":\"NotAQuality\",\"hasPayload\":false}")]
    [InlineData("{\"quality\":0,\"hasPayload\":false}")]
    [InlineData("{\"quality\":\"0\",\"hasPayload\":true,\"payload\":0}")]
    [InlineData("{\"quality\":\"Valid\",\"hasPayload\":true}")]
    [InlineData("{\"quality\":\"Unavailable\",\"hasPayload\":true,\"payload\":0}")]
    [InlineData("{\"quality\":\"Unavailable\",\"hasPayload\":false,\"payload\":null}")]
    public void Malformed_observations_are_rejected(string json) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Observation<int>>(json, SchemaJson.Options));

    [Fact]
    public async Task Windows_reinstall_resolves_one_device_and_keeps_visit_history()
    {
        var store = new SnapshotStore(Path.Combine(Fixtures.Sandbox(), "metadata.db"));
        await store.InitializeAsync();
        var before = Fixtures.Snapshot();
        var first = await store.StartPersonalSessionAsync(before, "en");
        var after = before with { Id = Guid.NewGuid(), OsBuild = "synthetic-reinstalled", LocalMachineKey = "installation-after" };
        var second = await store.StartPersonalSessionAsync(after, "en");
        Assert.Equal(first.Device.Id, second.Device.Id);
        Assert.Equal(first.Customer.Id, second.Customer.Id);
        Assert.NotEqual(first.WorkOrder.Id, second.WorkOrder.Id);
        Assert.Single(await store.ListDevicesAsync());
        Assert.Equal(before.LocalMachineKey, (await store.LoadSnapshotAsync(before.Id)).LocalMachineKey);
        Assert.Equal(after.LocalMachineKey, (await store.LoadSnapshotAsync(after.Id)).LocalMachineKey);
    }

    [Fact]
    public async Task Partial_and_conflicting_identity_require_explicit_review_without_creating_rows()
    {
        var store = new SnapshotStore(Path.Combine(Fixtures.Sandbox(), "metadata.db"));
        await store.InitializeAsync();
        var fixture = Fixtures.Snapshot();
        var existing = await store.StartPersonalSessionAsync(fixture, "en");
        var partial = fixture with { Id = Guid.NewGuid(), Identity = new(Fixtures.Identity.SmbiosSystemUuid, null, null, []) };
        var error = await Assert.ThrowsAsync<IdentityReviewRequiredException>(() => store.StartPersonalSessionAsync(partial, "en"));
        Assert.Equal(IdentityMatchKind.PartialMatch, error.Resolution.Kind);
        Assert.Equal(0.45, error.Resolution.Candidates[0].Confidence);
        Assert.Single(await store.ListDevicesAsync());
        var conflict = DeviceResolver.Resolve(Fixtures.Identity with { BoardSerial = "OTHER-BOARD" }, [existing.Device]);
        Assert.Equal(IdentityMatchKind.PartialMatch, conflict.Kind);
        Assert.Contains("boardSerial", conflict.Candidates[0].Conflicts);
        var ambiguous = DeviceResolver.Resolve(Fixtures.Identity, [existing.Device, existing.Device with { Id = Guid.NewGuid() }]);
        Assert.Equal(IdentityMatchKind.Ambiguous, ambiguous.Kind);
        Assert.Null(ambiguous.DeviceId);
        Assert.Equal(IdentityMatchKind.NoMatch, DeviceResolver.Resolve(new(null, "To be filled by O.E.M.", "000000", []), [existing.Device]).Kind);
    }

    [Fact]
    public async Task Foreign_keys_reject_orphans_and_migration_preserves_partial_snapshot_and_creates_backup()
    {
        var path = Path.Combine(Fixtures.Sandbox(), "metadata.db");
        await using (var fixture = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await fixture.OpenAsync();
            await using var command = fixture.CreateCommand();
            command.CommandText = SnapshotStore.Migration1 + "PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync();
        }
        var store = new SnapshotStore(path);
        var snapshot = Fixtures.Snapshot();
        await store.StartPersonalSessionAsync(snapshot, "en");
        await store.InitializeAsync();
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.backup"));
        Assert.Equal(JsonSerializer.Serialize(snapshot, SchemaJson.Options), JsonSerializer.Serialize(await store.LoadSnapshotAsync(snapshot.Id), SchemaJson.Options));
        var error = await Assert.ThrowsAsync<SqliteException>(() => store.SaveSnapshotAsync(Guid.NewGuid(), snapshot with { Id = Guid.NewGuid() }));
        Assert.Equal(19, error.SqliteErrorCode);
        await store.InitializeAsync();
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.backup"));
    }

    [Fact]
    public void Forward_extension_fields_survive_serialization()
    {
        const string json = "{\"id\":\"46b8ac4b-e8e0-4f77-bf86-3a4e33cd0e4c\",\"language\":\"en\",\"futureNote\":{\"state\":\"unknown\"}}";
        var customer = JsonSerializer.Deserialize<Customer>(json, SchemaJson.Options)!;
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(customer, SchemaJson.Options));
        Assert.Equal("unknown", result.RootElement.GetProperty("futureNote").GetProperty("state").GetString());
    }
}
