using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using PCAnalyzer.Core;

namespace PCAnalyzer.Storage;

/// <summary>Durable local history. Each replacement is atomic; measurement metadata remains intact.</summary>
public sealed class SQLiteHistoryStore : IHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public SQLiteHistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    // Microsoft.Data.Sqlite async APIs execute native I/O synchronously. Offload the entire
    // operation, including serialization and busy waits, so callers never block the WPF dispatcher.
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => InitializeCoreAsync(cancellationToken), cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 1)
                    throw new InvalidDataException("O histórico foi criado por uma versão mais recente do aplicativo.");
            }
            await using (var journal = connection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=WAL;";
                var mode = await journal.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(Convert.ToString(mode, CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("O armazenamento não conseguiu ativar o diário WAL.");
            }
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var schema = connection.CreateCommand();
            schema.Transaction = (SqliteTransaction)transaction;
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY NOT NULL,
                    label TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    started_unix_ms INTEGER NOT NULL,
                    ended_at TEXT,
                    sample_count INTEGER NOT NULL CHECK(sample_count >= 0),
                    workload TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    saved_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started_unix_ms DESC, id);
                CREATE TABLE IF NOT EXISTS changes (
                    id TEXT PRIMARY KEY NOT NULL,
                    created_at TEXT NOT NULL,
                    created_unix_ms INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    state TEXT NOT NULL,
                    payload_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_changes_created ON changes(created_unix_ms DESC, id);
                PRAGMA user_version=1;
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initializationGate.Release(); }
    }

    public Task SaveSessionAsync(DiagnosticSession session, CancellationToken cancellationToken = default) =>
        Task.Run(() => SaveSessionCoreAsync(session, cancellationToken), cancellationToken);

    private async Task SaveSessionCoreAsync(DiagnosticSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Serialize before opening a transaction; a serialization failure cannot damage existing history.
        var payload = JsonSerializer.Serialize(session, JsonOptions);
        await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO sessions(id,label,started_at,started_unix_ms,ended_at,sample_count,workload,payload_json,saved_at)
            VALUES($id,$label,$start,$start_ms,$end,$count,$workload,$payload,$saved)
            ON CONFLICT(id) DO UPDATE SET
                label=excluded.label, started_at=excluded.started_at, started_unix_ms=excluded.started_unix_ms,
                ended_at=excluded.ended_at, sample_count=excluded.sample_count, workload=excluded.workload,
                payload_json=excluded.payload_json, saved_at=excluded.saved_at;
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$label", session.Label);
        command.Parameters.AddWithValue("$start", Timestamp(session.StartedAt));
        command.Parameters.AddWithValue("$start_ms", session.StartedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", (object?)session.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$count", session.Samples.Count);
        command.Parameters.AddWithValue("$workload", session.Context.Workload);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$saved", Timestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<DiagnosticSession?> LoadSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadSessionCoreAsync(id, cancellationToken), cancellationToken);

    private async Task<DiagnosticSession?> LoadSessionCoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var json = await LoadPayloadAsync("SELECT payload_json FROM sessions WHERE id=$id;", id, cancellationToken).ConfigureAwait(false);
        return json is null ? null : Deserialize<DiagnosticSession>(json);
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        Task.Run(() => ListSessionsCoreAsync(limit, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<SessionSummary>> ListSessionsCoreAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];
        await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,label,started_at,ended_at,sample_count,workload FROM sessions ORDER BY started_unix_ms DESC,id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Min(limit, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<SessionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)), reader.GetInt32(4), reader.GetString(5)));
        return sessions;
    }

    public Task SaveChangeAsync(ChangeRecord change, CancellationToken cancellationToken = default) =>
        Task.Run(() => SaveChangeCoreAsync(change, cancellationToken), cancellationToken);

    private async Task SaveChangeCoreAsync(ChangeRecord change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var payload = JsonSerializer.Serialize(change, JsonOptions);
        await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO changes(id,created_at,created_unix_ms,updated_at,state,payload_json)
            VALUES($id,$created,$created_ms,$updated,$state,$payload)
            ON CONFLICT(id) DO UPDATE SET updated_at=excluded.updated_at,state=excluded.state,payload_json=excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$id", change.Id.ToString("D"));
        command.Parameters.AddWithValue("$created", Timestamp(change.CreatedAt));
        command.Parameters.AddWithValue("$created_ms", change.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updated", Timestamp(change.UpdatedAt));
        command.Parameters.AddWithValue("$state", change.State.ToString());
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // FULL synchronous + WAL commits the recovery record before the caller changes the operating system.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ChangeRecord?> LoadChangeAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadChangeCoreAsync(id, cancellationToken), cancellationToken);

    private async Task<ChangeRecord?> LoadChangeCoreAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var json = await LoadPayloadAsync("SELECT payload_json FROM changes WHERE id=$id;", id, cancellationToken).ConfigureAwait(false);
        return json is null ? null : Deserialize<ChangeRecord>(json);
    }

    public Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ListChangesCoreAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<ChangeRecord>> ListChangesCoreAsync(CancellationToken cancellationToken = default)
    {
        await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Bound the initial UI query even when a machine has years of history.
        command.CommandText = "SELECT payload_json FROM changes ORDER BY created_unix_ms DESC,id LIMIT 1000;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var changes = new List<ChangeRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            changes.Add(Deserialize<ChangeRecord>(reader.GetString(0)));
        return changes;
    }

    private async Task<string?> LoadPayloadAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configure = connection.CreateCommand();
            configure.CommandText = "PRAGMA busy_timeout=15000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            await configure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Um registro local está vazio ou inválido.");
    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
}
