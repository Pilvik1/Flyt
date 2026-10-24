using System.Globalization;
using Flyt.Core;
using Microsoft.Data.Sqlite;

namespace Flyt.Persistence;

public sealed class FlytStateStore
{
    private readonly string _connectionString;

    public FlytStateStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS missions (
                id TEXT PRIMARY KEY,
                goal TEXT NOT NULL,
                workspace_path TEXT NOT NULL,
                profile TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mission_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                mission_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                from_state TEXT NULL,
                to_state TEXT NOT NULL,
                detail TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                FOREIGN KEY (mission_id) REFERENCES missions(id)
            );

            CREATE INDEX IF NOT EXISTS ix_mission_events_mission_sequence
                ON mission_events(mission_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateMissionAsync(
        Mission mission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mission);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insertMission = connection.CreateCommand())
        {
            insertMission.Transaction = (SqliteTransaction)transaction;
            insertMission.CommandText = """
                INSERT INTO missions (
                    id, goal, workspace_path, profile, state, created_at, updated_at
                ) VALUES (
                    $id, $goal, $workspacePath, $profile, $state, $createdAt, $updatedAt
                );
                """;
            AddMissionParameters(insertMission, mission);
            await insertMission.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertEventAsync(
            connection,
            (SqliteTransaction)transaction,
            mission.Id,
            "mission.created",
            fromState: null,
            mission.State,
            "Mission created.",
            mission.CreatedAt,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Mission?> GetMissionAsync(
        Guid missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, goal, workspace_path, profile, state, created_at, updated_at
            FROM missions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", missionId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMission(reader)
            : null;
    }

    public async Task<IReadOnlyList<Mission>> ListMissionsAsync(
        CancellationToken cancellationToken = default)
    {
        var missions = new List<Mission>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, goal, workspace_path, profile, state, created_at, updated_at
            FROM missions
            ORDER BY updated_at DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            missions.Add(ReadMission(reader));
        }

        return missions;
    }

    public async Task<Mission> TransitionMissionAsync(
        Guid missionId,
        MissionState nextState,
        string detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;

        var current = await GetMissionAsync(
            connection,
            sqliteTransaction,
            missionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {missionId:D} was not found.");

        var updated = current.TransitionTo(nextState);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = sqliteTransaction;
            update.CommandText = """
                UPDATE missions
                SET state = $state, updated_at = $updatedAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$state", updated.State.ToString());
            update.Parameters.AddWithValue("$updatedAt", FormatDate(updated.UpdatedAt));
            update.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertEventAsync(
            connection,
            sqliteTransaction,
            missionId,
            "mission.transitioned",
            current.State,
            updated.State,
            detail.Trim(),
            updated.UpdatedAt,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<IReadOnlyList<MissionEvent>> GetEventsAsync(
        Guid missionId,
        CancellationToken cancellationToken = default)
    {
        var events = new List<MissionEvent>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, mission_id, event_type, from_state, to_state, detail, occurred_at
            FROM mission_events
            WHERE mission_id = $missionId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$missionId", missionId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new MissionEvent(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : Enum.Parse<MissionState>(reader.GetString(3)),
                Enum.Parse<MissionState>(reader.GetString(4)),
                reader.GetString(5),
                ParseDate(reader.GetString(6))));
        }

        return events;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<Mission?> GetMissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, goal, workspace_path, profile, state, created_at, updated_at
            FROM missions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", missionId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMission(reader)
            : null;
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid missionId,
        string eventType,
        MissionState? fromState,
        MissionState toState,
        string detail,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mission_events (
                mission_id, event_type, from_state, to_state, detail, occurred_at
            ) VALUES (
                $missionId, $eventType, $fromState, $toState, $detail, $occurredAt
            );
            """;
        command.Parameters.AddWithValue("$missionId", missionId.ToString("D"));
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$fromState", fromState?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$toState", toState.ToString());
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$occurredAt", FormatDate(occurredAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddMissionParameters(SqliteCommand command, Mission mission)
    {
        command.Parameters.AddWithValue("$id", mission.Id.ToString("D"));
        command.Parameters.AddWithValue("$goal", mission.Goal);
        command.Parameters.AddWithValue("$workspacePath", mission.WorkspacePath);
        command.Parameters.AddWithValue("$profile", mission.Profile);
        command.Parameters.AddWithValue("$state", mission.State.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatDate(mission.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(mission.UpdatedAt));
    }

    private static Mission ReadMission(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        Enum.Parse<MissionState>(reader.GetString(4)),
        ParseDate(reader.GetString(5)),
        ParseDate(reader.GetString(6)));

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

