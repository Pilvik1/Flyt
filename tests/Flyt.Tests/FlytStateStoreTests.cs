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
            "full-review",
            ["test"],
            2,
            12_000,
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
                    "builder",
                    "Implement",
                    "blue",
                    ["write"],
                    "session-1",
                    DeskPaneStatus.Running,
                    DeskStateSource.Registered,
                    ProcessId: null,
                    ExitCode: null,
                    StateChangedAt: now,
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
        Assert.Equal("builder", pane.Role);
        Assert.Equal("blue", pane.Color);
        Assert.Equal("session-1", pane.ProviderSessionId);
        Assert.Equal("full-review", reconciled.TemplateName);
        Assert.Equal(["test"], reconciled.Tags);
    }

    [Fact]
    public async Task FailureReceipt_PersistsDiagnosticEvidence()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        var paneId = Guid.NewGuid();
        var desk = new Desk(
            deskId,
            "failures",
            _directory,
            null,
            [],
            2,
            12_000,
            DeskStatus.Running,
            [new DeskPane(
                paneId,
                deskId,
                "%1",
                AgentProvider.Codex,
                "reviewer",
                _directory,
                "codex",
                "reviewer",
                "Review",
                "magenta",
                ["review"],
                null,
                DeskPaneStatus.Failed,
                DeskStateSource.Process,
                123,
                42,
                now,
                now,
                now)],
            now,
            now,
            now);
        await store.SaveDeskAsync(desk);
        await store.AddFailureReceiptAsync(new DeskFailureReceipt(
            Guid.NewGuid(), deskId, paneId, "reviewer", AgentProvider.Codex,
            _directory, "codex", 42, "Synthetic failure", "last output", 1024, now));
        await store.SaveDeskAsync(desk with { UpdatedAt = now.AddMinutes(1) });

        var receipt = Assert.Single(await store.ListFailureReceiptsAsync("failures"));
        Assert.Equal(42, receipt.ExitCode);
        Assert.Equal("last output", receipt.RecentOutput);
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

    [Fact]
    public async Task DeskHistoryAndQueue_PersistSearchAndTransitions()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "history.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        await store.SaveDeskAsync(new Desk(
            deskId,
            "history-desk",
            _directory,
            "full-review",
            ["history"],
            1,
            1024,
            DeskStatus.Running,
            [new DeskPane(
                Guid.NewGuid(), deskId, "%1", AgentProvider.Codex, "verifier", _directory,
                "codex", "verifier", "Verify", "green", ["expensive"], null,
                DeskPaneStatus.Idle, DeskStateSource.Registered, null, null, now, now, now)],
            now,
            now,
            now));

        var queued = await store.EnqueueWorkItemAsync(
            "history-desk",
            "verifier",
            "Run the full test suite",
            expensive: true);
        var dispatched = await store.TransitionWorkItemAsync(queued.Id, DeskWorkItemStatus.Dispatched);
        var completed = await store.TransitionWorkItemAsync(
            queued.Id,
            DeskWorkItemStatus.Completed,
            "All tests passed.");

        var items = await store.ListWorkItemsAsync("history-desk");
        var events = await store.ListDeskEventsAsync("history-desk", "completed");

        Assert.Equal(DeskWorkItemStatus.Dispatched, dispatched.Status);
        Assert.NotNull(dispatched.DispatchedAt);
        Assert.Equal(DeskWorkItemStatus.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("All tests passed.", Assert.Single(items).Outcome);
        Assert.Contains(events, value => value.EventType == "work.completed");
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
