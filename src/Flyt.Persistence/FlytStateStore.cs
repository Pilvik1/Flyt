using System.Globalization;
using System.Text.Json;
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
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

        var version = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version < 1)
        {
            await ApplyMigrationAsync(
                connection,
                1,
                """
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
                """,
                cancellationToken).ConfigureAwait(false);
        }

        if (version < 2)
        {
            await ApplyMigrationAsync(
                connection,
                2,
                """
                ALTER TABLE missions ADD COLUMN resume_state TEXT NULL;
                ALTER TABLE missions ADD COLUMN version INTEGER NOT NULL DEFAULT 0;

                CREATE TABLE mission_plans (
                    mission_id TEXT PRIMARY KEY,
                    acceptance_criteria_json TEXT NOT NULL,
                    steps_json TEXT NOT NULL,
                    verification_command TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                );

                CREATE TABLE mission_approvals (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    mission_id TEXT NOT NULL,
                    plan_version INTEGER NOT NULL,
                    decision TEXT NOT NULL,
                    decided_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                );

                CREATE TABLE desks (
                    id TEXT PRIMARY KEY,
                    session_name TEXT NOT NULL UNIQUE,
                    workspace_path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_reconciled_at TEXT NULL
                );

                CREATE TABLE desk_panes (
                    id TEXT PRIMARY KEY,
                    desk_id TEXT NOT NULL,
                    pane_id TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    name TEXT NOT NULL,
                    working_directory TEXT NOT NULL,
                    launch_command TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (desk_id) REFERENCES desks(id),
                    UNIQUE (desk_id, name)
                );

                CREATE INDEX ix_desk_panes_desk_id ON desk_panes(desk_id);
                """,
                cancellationToken).ConfigureAwait(false);
        }
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
                    id, goal, workspace_path, profile, state, resume_state, version, created_at, updated_at
                ) VALUES (
                    $id, $goal, $workspacePath, $profile, $state, $resumeState, $version, $createdAt, $updatedAt
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
            SELECT id, goal, workspace_path, profile, state, resume_state, version, created_at, updated_at
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
            SELECT id, goal, workspace_path, profile, state, resume_state, version, created_at, updated_at
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
                SET state = $state,
                    resume_state = $resumeState,
                    version = $nextVersion,
                    updated_at = $updatedAt
                WHERE id = $id AND version = $expectedVersion;
                """;
            update.Parameters.AddWithValue("$state", updated.State.ToString());
            update.Parameters.AddWithValue(
                "$resumeState",
                updated.ResumeState?.ToString() ?? (object)DBNull.Value);
            update.Parameters.AddWithValue("$nextVersion", updated.Version);
            update.Parameters.AddWithValue("$updatedAt", FormatDate(updated.UpdatedAt));
            update.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
            update.Parameters.AddWithValue("$expectedVersion", current.Version);
            var rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows != 1)
            {
                throw new InvalidOperationException(
                    $"Mission {missionId:D} changed concurrently. Reload it and retry.");
            }
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

    public async Task<MissionPlan?> GetMissionPlanAsync(
        Guid missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetMissionPlanAsync(connection, transaction: null, missionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MissionPlan> SaveMissionPlanAsync(
        MissionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var mission = await GetMissionAsync(connection, sqliteTransaction, plan.MissionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {plan.MissionId:D} was not found.");

        if (mission.State is not (MissionState.Briefing or MissionState.PlanApproval))
        {
            throw new InvalidOperationException(
                $"Mission plan cannot be changed while the mission is {mission.State}.");
        }

        var existing = await GetMissionPlanAsync(connection, sqliteTransaction, plan.MissionId, cancellationToken)
            .ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow;
        var saved = plan with
        {
            Version = (existing?.Version ?? 0) + 1,
            CreatedAt = existing?.CreatedAt ?? timestamp,
            UpdatedAt = timestamp,
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = sqliteTransaction;
            command.CommandText = """
                INSERT INTO mission_plans (
                    mission_id,
                    acceptance_criteria_json,
                    steps_json,
                    verification_command,
                    version,
                    created_at,
                    updated_at
                ) VALUES (
                    $missionId,
                    $criteria,
                    $steps,
                    $verificationCommand,
                    $version,
                    $createdAt,
                    $updatedAt
                )
                ON CONFLICT(mission_id) DO UPDATE SET
                    acceptance_criteria_json = excluded.acceptance_criteria_json,
                    steps_json = excluded.steps_json,
                    verification_command = excluded.verification_command,
                    version = excluded.version,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$missionId", saved.MissionId.ToString("D"));
            command.Parameters.AddWithValue("$criteria", JsonSerializer.Serialize(saved.AcceptanceCriteria));
            command.Parameters.AddWithValue("$steps", JsonSerializer.Serialize(saved.Steps));
            command.Parameters.AddWithValue("$verificationCommand", saved.VerificationCommand);
            command.Parameters.AddWithValue("$version", saved.Version);
            command.Parameters.AddWithValue("$createdAt", FormatDate(saved.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", FormatDate(saved.UpdatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertEventAsync(
            connection,
            sqliteTransaction,
            mission.Id,
            "mission.plan_saved",
            mission.State,
            mission.State,
            $"Mission plan version {saved.Version} persisted.",
            saved.UpdatedAt,
            cancellationToken).ConfigureAwait(false);

        if (mission.State == MissionState.Briefing)
        {
            await TransitionMissionAsync(
                connection,
                sqliteTransaction,
                mission,
                MissionState.PlanApproval,
                "Plan persisted; waiting for operator approval.",
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<Mission> ApproveMissionPlanAsync(
        Guid missionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var mission = await GetMissionAsync(connection, sqliteTransaction, missionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {missionId:D} was not found.");
        var plan = await GetMissionPlanAsync(connection, sqliteTransaction, missionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "A persisted mission plan is required before approval.");

        var updated = await TransitionMissionAsync(
            connection,
            sqliteTransaction,
            mission,
            MissionState.Planning,
            $"Operator approved mission plan version {plan.Version}.",
            cancellationToken).ConfigureAwait(false);

        await using (var approval = connection.CreateCommand())
        {
            approval.Transaction = sqliteTransaction;
            approval.CommandText = """
                INSERT INTO mission_approvals (mission_id, plan_version, decision, decided_at)
                VALUES ($missionId, $planVersion, 'approved', $decidedAt);
                """;
            approval.Parameters.AddWithValue("$missionId", missionId.ToString("D"));
            approval.Parameters.AddWithValue("$planVersion", plan.Version);
            approval.Parameters.AddWithValue("$decidedAt", FormatDate(updated.UpdatedAt));
            await approval.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

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

    public async Task<Desk> SaveDeskAsync(
        Desk desk,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desk);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var existing = await GetDeskAsync(connection, sqliteTransaction, desk.SessionName, cancellationToken)
            .ConfigureAwait(false);
        var deskId = existing?.Id ?? desk.Id;
        var timestamp = DateTimeOffset.UtcNow;
        var saved = desk with
        {
            Id = deskId,
            CreatedAt = existing?.CreatedAt ?? desk.CreatedAt,
            UpdatedAt = timestamp,
            Panes = desk.Panes.Select(pane => pane with
            {
                DeskId = deskId,
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
            }).ToArray(),
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = sqliteTransaction;
            command.CommandText = """
                INSERT INTO desks (
                    id, session_name, workspace_path, status, created_at, updated_at, last_reconciled_at
                ) VALUES (
                    $id, $sessionName, $workspacePath, $status, $createdAt, $updatedAt, $lastReconciledAt
                )
                ON CONFLICT(session_name) DO UPDATE SET
                    workspace_path = excluded.workspace_path,
                    status = excluded.status,
                    updated_at = excluded.updated_at,
                    last_reconciled_at = excluded.last_reconciled_at;
                """;
            AddDeskParameters(command, saved);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deletePanes = connection.CreateCommand())
        {
            deletePanes.Transaction = sqliteTransaction;
            deletePanes.CommandText = "DELETE FROM desk_panes WHERE desk_id = $deskId;";
            deletePanes.Parameters.AddWithValue("$deskId", deskId.ToString("D"));
            await deletePanes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var pane in saved.Panes)
        {
            await InsertDeskPaneAsync(connection, sqliteTransaction, pane, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return saved;
    }

    public async Task<Desk?> GetDeskAsync(
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetDeskAsync(connection, transaction: null, sessionName, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Desk>> ListDesksAsync(
        CancellationToken cancellationToken = default)
    {
        var desks = new List<Desk>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_name, workspace_path, status, created_at, updated_at, last_reconciled_at
            FROM desks
            ORDER BY updated_at DESC;
            """;

        var rows = new List<Desk>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadDesk(reader, []));
            }
        }

        foreach (var desk in rows)
        {
            var panes = await GetDeskPanesAsync(connection, transaction: null, desk.Id, cancellationToken)
                .ConfigureAwait(false);
            desks.Add(desk with { Panes = panes });
        }

        return desks;
    }

    public async Task<Desk> UpdateDeskRuntimeAsync(
        string sessionName,
        DeskStatus status,
        IReadOnlyDictionary<string, string> paneIdsByName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentNullException.ThrowIfNull(paneIdsByName);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var desk = await GetDeskAsync(connection, sqliteTransaction, sessionName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Desk '{sessionName}' was not found in the Flyt registry.");
        var timestamp = DateTimeOffset.UtcNow;

        await using (var updateDesk = connection.CreateCommand())
        {
            updateDesk.Transaction = sqliteTransaction;
            updateDesk.CommandText = """
                UPDATE desks
                SET status = $status, updated_at = $updatedAt, last_reconciled_at = $reconciledAt
                WHERE id = $id;
                """;
            updateDesk.Parameters.AddWithValue("$status", status.ToString());
            updateDesk.Parameters.AddWithValue("$updatedAt", FormatDate(timestamp));
            updateDesk.Parameters.AddWithValue("$reconciledAt", FormatDate(timestamp));
            updateDesk.Parameters.AddWithValue("$id", desk.Id.ToString("D"));
            await updateDesk.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var defaultPaneStatus = status switch
        {
            DeskStatus.Running => DeskPaneStatus.Missing,
            DeskStatus.Missing => DeskPaneStatus.Missing,
            DeskStatus.Stopped => DeskPaneStatus.Stopped,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported desk status."),
        };
        await using (var markPanes = connection.CreateCommand())
        {
            markPanes.Transaction = sqliteTransaction;
            markPanes.CommandText = """
                UPDATE desk_panes
                SET status = $status, updated_at = $updatedAt
                WHERE desk_id = $deskId;
                """;
            markPanes.Parameters.AddWithValue("$status", defaultPaneStatus.ToString());
            markPanes.Parameters.AddWithValue("$updatedAt", FormatDate(timestamp));
            markPanes.Parameters.AddWithValue("$deskId", desk.Id.ToString("D"));
            await markPanes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (status == DeskStatus.Running)
        {
            foreach (var (name, paneId) in paneIdsByName)
            {
                await using var updatePane = connection.CreateCommand();
                updatePane.Transaction = sqliteTransaction;
                updatePane.CommandText = """
                    UPDATE desk_panes
                    SET pane_id = $paneId, status = $status, updated_at = $updatedAt
                    WHERE desk_id = $deskId AND name = $name COLLATE NOCASE;
                    """;
                updatePane.Parameters.AddWithValue("$paneId", paneId);
                updatePane.Parameters.AddWithValue("$status", DeskPaneStatus.Running.ToString());
                updatePane.Parameters.AddWithValue("$updatedAt", FormatDate(timestamp));
                updatePane.Parameters.AddWithValue("$deskId", desk.Id.ToString("D"));
                updatePane.Parameters.AddWithValue("$name", name);
                await updatePane.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetDeskAsync(sessionName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Desk disappeared after reconciliation.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
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
            SELECT id, goal, workspace_path, profile, state, resume_state, version, created_at, updated_at
            FROM missions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", missionId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMission(reader)
            : null;
    }

    private static async Task<MissionPlan?> GetMissionPlanAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT mission_id,
                   acceptance_criteria_json,
                   steps_json,
                   verification_command,
                   version,
                   created_at,
                   updated_at
            FROM mission_plans
            WHERE mission_id = $missionId;
            """;
        command.Parameters.AddWithValue("$missionId", missionId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new MissionPlan(
            Guid.Parse(reader.GetString(0)),
            JsonSerializer.Deserialize<string[]>(reader.GetString(1)) ?? [],
            JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [],
            reader.GetString(3),
            reader.GetInt32(4),
            ParseDate(reader.GetString(5)),
            ParseDate(reader.GetString(6)));
    }

    private static async Task<Desk?> GetDeskAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, session_name, workspace_path, status, created_at, updated_at, last_reconciled_at
            FROM desks
            WHERE session_name = $sessionName;
            """;
        command.Parameters.AddWithValue("$sessionName", sessionName);

        Desk? desk;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            desk = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadDesk(reader, [])
                : null;
        }

        if (desk is null)
        {
            return null;
        }

        var panes = await GetDeskPanesAsync(connection, transaction, desk.Id, cancellationToken)
            .ConfigureAwait(false);
        return desk with { Panes = panes };
    }

    private static async Task<IReadOnlyList<DeskPane>> GetDeskPanesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid deskId,
        CancellationToken cancellationToken)
    {
        var panes = new List<DeskPane>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,
                   desk_id,
                   pane_id,
                   provider,
                   name,
                   working_directory,
                   launch_command,
                   status,
                   created_at,
                   updated_at
            FROM desk_panes
            WHERE desk_id = $deskId
            ORDER BY created_at, name;
            """;
        command.Parameters.AddWithValue("$deskId", deskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            panes.Add(new DeskPane(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<AgentProvider>(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                Enum.Parse<DeskPaneStatus>(reader.GetString(7)),
                ParseDate(reader.GetString(8)),
                ParseDate(reader.GetString(9))));
        }

        return panes;
    }

    private static async Task InsertDeskPaneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeskPane pane,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO desk_panes (
                id,
                desk_id,
                pane_id,
                provider,
                name,
                working_directory,
                launch_command,
                status,
                created_at,
                updated_at
            ) VALUES (
                $id,
                $deskId,
                $paneId,
                $provider,
                $name,
                $workingDirectory,
                $launchCommand,
                $status,
                $createdAt,
                $updatedAt
            );
            """;
        command.Parameters.AddWithValue("$id", pane.Id.ToString("D"));
        command.Parameters.AddWithValue("$deskId", pane.DeskId.ToString("D"));
        command.Parameters.AddWithValue("$paneId", pane.PaneId);
        command.Parameters.AddWithValue("$provider", pane.Provider.ToString());
        command.Parameters.AddWithValue("$name", pane.Name);
        command.Parameters.AddWithValue("$workingDirectory", pane.WorkingDirectory);
        command.Parameters.AddWithValue("$launchCommand", pane.LaunchCommand);
        command.Parameters.AddWithValue("$status", pane.Status.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatDate(pane.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(pane.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddDeskParameters(SqliteCommand command, Desk desk)
    {
        command.Parameters.AddWithValue("$id", desk.Id.ToString("D"));
        command.Parameters.AddWithValue("$sessionName", desk.SessionName);
        command.Parameters.AddWithValue("$workspacePath", desk.WorkspacePath);
        command.Parameters.AddWithValue("$status", desk.Status.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatDate(desk.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(desk.UpdatedAt));
        command.Parameters.AddWithValue(
            "$lastReconciledAt",
            desk.LastReconciledAt is null ? DBNull.Value : FormatDate(desk.LastReconciledAt.Value));
    }

    private static Desk ReadDesk(SqliteDataReader reader, IReadOnlyList<DeskPane> panes) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<DeskStatus>(reader.GetString(3)),
        panes,
        ParseDate(reader.GetString(4)),
        ParseDate(reader.GetString(5)),
        reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)));

    private static async Task<Mission> TransitionMissionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Mission current,
        MissionState nextState,
        string detail,
        CancellationToken cancellationToken)
    {
        var updated = current.TransitionTo(nextState);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE missions
            SET state = $state,
                resume_state = $resumeState,
                version = $nextVersion,
                updated_at = $updatedAt
            WHERE id = $id AND version = $expectedVersion;
            """;
        update.Parameters.AddWithValue("$state", updated.State.ToString());
        update.Parameters.AddWithValue(
            "$resumeState",
            updated.ResumeState?.ToString() ?? (object)DBNull.Value);
        update.Parameters.AddWithValue("$nextVersion", updated.Version);
        update.Parameters.AddWithValue("$updatedAt", FormatDate(updated.UpdatedAt));
        update.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
        update.Parameters.AddWithValue("$expectedVersion", current.Version);
        var rows = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (rows != 1)
        {
            throw new InvalidOperationException(
                $"Mission {current.Id:D} changed concurrently. Reload it and retry.");
        }

        await InsertEventAsync(
            connection,
            transaction,
            current.Id,
            "mission.transitioned",
            current.State,
            updated.State,
            detail,
            updated.UpdatedAt,
            cancellationToken).ConfigureAwait(false);
        return updated;
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
        command.Parameters.AddWithValue(
            "$resumeState",
            mission.ResumeState?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$version", mission.Version);
        command.Parameters.AddWithValue("$createdAt", FormatDate(mission.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(mission.UpdatedAt));
    }

    private static Mission ReadMission(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        Enum.Parse<MissionState>(reader.GetString(4)),
        reader.IsDBNull(5) ? null : Enum.Parse<MissionState>(reader.GetString(5)),
        reader.GetInt32(6),
        ParseDate(reader.GetString(7)),
        ParseDate(reader.GetString(8)));

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"{sql}{Environment.NewLine}PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
