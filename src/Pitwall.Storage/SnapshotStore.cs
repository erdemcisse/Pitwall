using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pitwall.Domain;

namespace Pitwall.Storage;

public sealed record PersonalSession(Customer Customer, Device Device, WorkOrder WorkOrder, Session Session);
public sealed class IdentityReviewRequiredException(IdentityResolution resolution) : InvalidOperationException("identity.review_required")
{
    public IdentityResolution Resolution { get; } = resolution;
}

public sealed class SnapshotStore
{
    public const int CurrentMigration = 2;
    private readonly string databasePath;
    public SnapshotStore(string databasePath)
    {
        if (!Path.IsPathFullyQualified(databasePath)) throw new ArgumentException("storage.absolute_path_required", nameof(databasePath));
        this.databasePath = Path.GetFullPath(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var version = await VersionAsync(connection, cancellationToken);
        if (version > CurrentMigration) throw new InvalidOperationException("storage.newer_schema");
        if (version == CurrentMigration) return;
        if (version > 0)
        {
            var backupPath = databasePath + $".v{version}.{Guid.NewGuid():N}.backup";
            await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
            await backup.OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            connection.BackupDatabase(backup);
        }
        using var transaction = connection.BeginTransaction();
        if (version < 1) await ExecuteAsync(connection, Migration1, cancellationToken, transaction);
        if (version < 2) await ExecuteAsync(connection, Migration2, cancellationToken, transaction);
        await ExecuteAsync(connection, $"PRAGMA user_version = {CurrentMigration};", cancellationToken, transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    internal const string Migration1 = """
        CREATE TABLE customers(id TEXT PRIMARY KEY, payload TEXT NOT NULL CHECK(json_valid(payload)));
        CREATE TABLE devices(id TEXT PRIMARY KEY, customer_id TEXT NOT NULL REFERENCES customers(id), payload TEXT NOT NULL CHECK(json_valid(payload)), UNIQUE(id, customer_id));
        CREATE TABLE work_orders(id TEXT PRIMARY KEY, customer_id TEXT NOT NULL REFERENCES customers(id), device_id TEXT NOT NULL, payload TEXT NOT NULL CHECK(json_valid(payload)), FOREIGN KEY(device_id,customer_id) REFERENCES devices(id,customer_id));
        CREATE TABLE sessions(id TEXT PRIMARY KEY, work_order_id TEXT NOT NULL REFERENCES work_orders(id), payload TEXT NOT NULL CHECK(json_valid(payload)));
        CREATE TABLE snapshots(id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id), payload TEXT NOT NULL CHECK(json_valid(payload)));
        CREATE TABLE evidence(id TEXT PRIMARY KEY, session_id TEXT NOT NULL REFERENCES sessions(id), snapshot_id TEXT REFERENCES snapshots(id), payload TEXT NOT NULL CHECK(json_valid(payload)));
        CREATE INDEX devices_customer ON devices(customer_id);
        CREATE INDEX work_orders_device ON work_orders(device_id);
        CREATE INDEX work_orders_customer ON work_orders(customer_id);
        CREATE INDEX sessions_work_order ON sessions(work_order_id);
        CREATE INDEX snapshots_session ON snapshots(session_id);
        CREATE INDEX evidence_session ON evidence(session_id);
        CREATE INDEX evidence_snapshot ON evidence(snapshot_id);
        """;
    internal const string Migration2 = """
        CREATE TABLE artifacts(sha256 TEXT PRIMARY KEY CHECK(length(sha256)=64), byte_length INTEGER NOT NULL CHECK(byte_length>=0), relative_path TEXT NOT NULL UNIQUE);
        """;

    public async Task<PersonalSession> StartPersonalSessionAsync(MachineSnapshot snapshot, string language, CancellationToken cancellationToken = default)
    {
        Validate(snapshot);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var devices = await ReadAllAsync<Device>(connection, "devices", cancellationToken, transaction);
        var resolution = DeviceResolver.Resolve(snapshot.Identity, devices);
        if (resolution.Kind is IdentityMatchKind.PartialMatch or IdentityMatchKind.Ambiguous)
            throw new IdentityReviewRequiredException(resolution);
        Device device;
        Customer customer;
        var now = snapshot.CapturedAt;
        if (resolution.DeviceId is Guid deviceId)
        {
            device = devices.Single(item => item.Id == deviceId);
            customer = await ReadAsync<Customer>(connection, "customers", device.CustomerId, cancellationToken, transaction);
        }
        else
        {
            customer = new Customer { Id = Guid.NewGuid(), Language = language, IsImplicit = true };
            device = new Device { Id = Guid.NewGuid(), CustomerId = customer.Id, Identity = snapshot.Identity, CreatedAt = now, IsImplicit = true };
            await InsertAsync(connection, "INSERT INTO customers VALUES($id,$payload)", customer.Id, customer, cancellationToken, transaction);
            await InsertAsync(connection, "INSERT INTO devices VALUES($id,$customer,$payload)", device.Id, device, cancellationToken, transaction, ("$customer", customer.Id));
        }
        var workOrder = new WorkOrder { Id = Guid.NewGuid(), JobNumber = Guid.NewGuid().ToString("N"), CustomerId = customer.Id,
            DeviceId = device.Id, Status = WorkOrderStatus.Intake, CreatedAt = now, IsImplicit = true };
        var session = new Session { Id = Guid.NewGuid(), WorkOrderId = workOrder.Id, StartedAt = now };
        await InsertAsync(connection, "INSERT INTO work_orders VALUES($id,$customer,$device,$payload)", workOrder.Id, workOrder, cancellationToken, transaction,
            ("$customer", customer.Id), ("$device", device.Id));
        await InsertAsync(connection, "INSERT INTO sessions VALUES($id,$order,$payload)", session.Id, session, cancellationToken, transaction, ("$order", workOrder.Id));
        await InsertAsync(connection, "INSERT INTO snapshots VALUES($id,$session,$payload)", snapshot.Id, snapshot, cancellationToken, transaction, ("$session", session.Id));
        await transaction.CommitAsync(cancellationToken);
        return new(customer, device, workOrder, session);
    }

    public async Task SaveSnapshotAsync(Guid sessionId, MachineSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Validate(snapshot);
        await using var connection = await OpenAsync(cancellationToken);
        await InsertAsync(connection, "INSERT INTO snapshots VALUES($id,$session,$payload)", snapshot.Id, snapshot, cancellationToken, null, ("$session", sessionId));
    }

    public async Task SaveEvidenceAsync(Guid sessionId, EvidenceItem evidence, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await InsertAsync(connection, "INSERT INTO evidence VALUES($id,$session,$snapshot,$payload)", evidence.Id, evidence, cancellationToken, null,
            ("$session", sessionId), ("$snapshot", evidence.SnapshotId));
    }

    public async Task EndSessionAsync(Session session, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        if (endedAt < session.StartedAt) throw new ArgumentOutOfRangeException(nameof(endedAt));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET payload=$payload WHERE id=$id AND work_order_id=$order";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session with { EndedAt = endedAt }, SchemaJson.Options));
        command.Parameters.AddWithValue("$id", session.Id.ToString());
        command.Parameters.AddWithValue("$order", session.WorkOrderId.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("storage.session_not_found");
    }

    public async Task<MachineSnapshot> LoadSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var snapshot = await ReadAsync<MachineSnapshot>(connection, "snapshots", id, cancellationToken);
        Validate(snapshot);
        return snapshot;
    }
    public async Task<EvidenceItem> LoadEvidenceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadAsync<EvidenceItem>(connection, "evidence", id, cancellationToken);
    }
    public async Task<Session> LoadSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadAsync<Session>(connection, "sessions", id, cancellationToken);
    }
    public async Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadAllAsync<Device>(connection, "devices", cancellationToken);
    }

    internal async Task RegisterArtifactAsync(string hash, long length, string relativePath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO artifacts VALUES($hash,$length,$path) ON CONFLICT(sha256) DO NOTHING";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$path", relativePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA temp_store=MEMORY;", cancellationToken);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task<int> VersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAsync<T>(SqliteConnection connection, string sql, Guid id, T value, CancellationToken cancellationToken,
        SqliteTransaction? transaction, params (string Name, Guid? Value)[] references)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, SchemaJson.Options));
        foreach (var (name, reference) in references) command.Parameters.AddWithValue(name, (object?)reference?.ToString() ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(SqliteConnection connection, string table, Guid id, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT payload FROM {table} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new KeyNotFoundException("storage.record_missing");
        return JsonSerializer.Deserialize<T>(payload, SchemaJson.Options) ?? throw new JsonException("storage.null_record");
    }
    private static async Task<List<T>> ReadAllAsync<T>(SqliteConnection connection, string table, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT payload FROM {table} ORDER BY id";
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), SchemaJson.Options) ?? throw new JsonException("storage.null_record"));
        return result;
    }

    private static void Validate(MachineSnapshot snapshot)
    {
        if (snapshot.SchemaVersion < 1) throw new ArgumentException("schema.version_invalid", nameof(snapshot));
        foreach (var fact in snapshot.Facts)
        {
            if (!Enum.IsDefined(fact.Type)) throw new ArgumentException("fact.type_invalid", nameof(snapshot));
            fact.Reading.Match((quality, payload) =>
            {
                if (quality == FactQuality.Invalid) return true;
                var valid = fact.Type switch
                {
                    FactType.Number => payload.ValueKind == JsonValueKind.Number,
                    FactType.Boolean => payload.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    FactType.Text => payload.ValueKind == JsonValueKind.String,
                    FactType.TextList => payload.ValueKind == JsonValueKind.Array && payload.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                    _ => false
                };
                if (!valid) throw new ArgumentException("fact.payload_type_mismatch", nameof(snapshot));
                return true;
            }, _ => true);
        }
    }
}
