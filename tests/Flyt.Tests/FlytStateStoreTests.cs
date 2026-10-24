using Flyt.Core;
using Flyt.Persistence;
using Microsoft.Data.Sqlite;

namespace Flyt.Tests;

public sealed class FlytStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flytv1-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAndTransition_PersistsMissionAndOrderedEvents()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Test persistence", _directory);

        await store.CreateMissionAsync(mission);
        var updated = await store.TransitionMissionAsync(
            mission.Id,
            MissionState.PlanApproval,
            "Ready for approval.");

        var loaded = await store.GetMissionAsync(mission.Id);
        var events = await store.GetEventsAsync(mission.Id);

        Assert.NotNull(loaded);
        Assert.Equal(MissionState.PlanApproval, loaded.State);
        Assert.Equal(updated.UpdatedAt, loaded.UpdatedAt);
        Assert.Collection(
            events,
            created => Assert.Equal("mission.created", created.EventType),
            transitioned =>
            {
                Assert.Equal("mission.transitioned", transitioned.EventType);
                Assert.Equal(MissionState.Briefing, transitioned.FromState);
                Assert.Equal(MissionState.PlanApproval, transitioned.ToState);
            });
    }

    [Fact]
    public async Task Transition_RejectsInvalidMoveWithoutAppendingEvent()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Test invalid transition", _directory);
        await store.CreateMissionAsync(mission);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TransitionMissionAsync(
                mission.Id,
                MissionState.Completed,
                "This must fail."));

        var events = await store.GetEventsAsync(mission.Id);
        Assert.Single(events);
    }

    [Fact]
    public async Task PlanApproval_RequiresPersistedPlanAndRecordsApproval()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Ship a tested slice", _directory);
        await store.CreateMissionAsync(mission);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ApproveMissionPlanAsync(mission.Id));

        var plan = MissionPlan.Create(
            mission.Id,
            ["Tests pass", "Receipt exists"],
            ["Build", "Review", "Verify"],
            "dotnet test");
        var saved = await store.SaveMissionPlanAsync(plan);
        var awaitingApproval = await store.GetMissionAsync(mission.Id);
        var approved = await store.ApproveMissionPlanAsync(mission.Id);

        Assert.Equal(1, saved.Version);
        Assert.Equal(MissionState.PlanApproval, awaitingApproval?.State);
        Assert.Equal(MissionState.Planning, approved.State);
        Assert.Equal(2, approved.Version);
        Assert.NotNull(await store.GetMissionPlanAsync(mission.Id));
    }

    [Fact]
    public async Task DeskRegistry_ReconcilesPaneIdsByStableName()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        var desk = new Desk(
            deskId,
            "daily-driver",
            _directory,
            DeskStatus.Running,
            [
                new DeskPane(
                    Guid.NewGuid(),
                    deskId,
                    "%1",
                    AgentProvider.Claude,
                    "builder",
                    _directory,
                    "claude",
                    DeskPaneStatus.Running,
                    now,
                    now),
            ],
            now,
            now,
            now);

        await store.SaveDeskAsync(desk);
        var reconciled = await store.UpdateDeskRuntimeAsync(
            desk.SessionName,
            DeskStatus.Running,
            new Dictionary<string, string> { ["builder"] = "%42" });

        var pane = Assert.Single(reconciled.Panes);
        Assert.Equal("%42", pane.PaneId);
        Assert.Equal(DeskPaneStatus.Running, pane.Status);
    }

    [Fact]
    public async Task Initialize_MigratesOriginalUnversionedDatabase()
    {
        var databasePath = Path.Combine(_directory, "legacy.db");
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE missions (
                    id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    workspace_path TEXT NOT NULL,
                    profile TEXT NOT NULL,
                    state TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE mission_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    mission_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    from_state TEXT NULL,
                    to_state TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    occurred_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new FlytStateStore(databasePath);
        await store.InitializeAsync();
        var mission = Mission.Create("Migrated", _directory);
        await store.CreateMissionAsync(mission);

        var loaded = await store.GetMissionAsync(mission.Id);
        Assert.NotNull(loaded);
        Assert.Equal(0, loaded.Version);
        Assert.Null(loaded.ResumeState);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
