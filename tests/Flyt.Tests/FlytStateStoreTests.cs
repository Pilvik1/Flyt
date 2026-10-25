using Flyt.Core;
using Flyt.Persistence;
using Microsoft.Data.Sqlite;

namespace Flyt.Tests;

public sealed class FlytStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flyt-tests",
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

        var executionPlan = await store.RequireApprovedMissionPlanAsync(mission.Id);
        Assert.Equal(saved.Version, executionPlan.Approval.PlanVersion);
        Assert.Equal(saved.ComputeFingerprint(), executionPlan.Approval.PlanFingerprint);
        Assert.Equal(saved.ComputeFingerprint(), executionPlan.Plan.ComputeFingerprint());
    }

    [Fact]
    public async Task ExecutionApproval_RejectsUnapprovedAndWrongStateMissions()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Approval boundary", _directory);
        await store.CreateMissionAsync(mission);

        var wrongState = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RequireApprovedMissionPlanAsync(mission.Id));
        Assert.Contains("Planning state", wrongState.Message, StringComparison.Ordinal);

        var plan = MissionPlan.Create(
            mission.Id,
            ["Tests pass"],
            ["Build", "Verify"],
            "dotnet test");
        await store.SaveMissionPlanAsync(plan);

        var unapproved = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RequireApprovedMissionPlanAsync(mission.Id));
        Assert.Contains("Planning state", unapproved.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionApproval_RejectsPlanContentThatNoLongerMatchesApproval()
    {
        var databasePath = Path.Combine(_directory, "state.db");
        var store = new FlytStateStore(databasePath);
        await store.InitializeAsync();
        var mission = Mission.Create("Approval fingerprint", _directory);
        await store.CreateMissionAsync(mission);
        await store.SaveMissionPlanAsync(MissionPlan.Create(
            mission.Id,
            ["Tests pass"],
            ["Build", "Verify"],
            "dotnet test"));
        await store.ApproveMissionPlanAsync(mission.Id);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE mission_plans
                SET verification_command = 'dotnet test --no-restore'
                WHERE mission_id = $missionId;
                """;
            command.Parameters.AddWithValue("$missionId", mission.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RequireApprovedMissionPlanAsync(mission.Id));
        Assert.Contains("does not match its approval", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissionRuntime_PersistsWorkspaceAttemptAndRedactedVerification()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Runtime persistence", _directory);
        await store.CreateMissionAsync(mission);
        var now = DateTimeOffset.UtcNow;
        var workspace = new MissionWorkspace(
            mission.Id,
            _directory,
            Path.Combine(_directory, "worktree"),
            "flyt/mission-test/builder",
            "base123",
            "head123",
            now,
            now);

        var savedWorkspace = await store.SaveMissionWorkspaceAsync(workspace);
        var attempt = await store.StartMissionAttemptAsync(
            mission.Id,
            AgentProvider.Claude,
            1,
            new string('a', 64));
        var completed = await store.CompleteMissionAttemptAsync(
            attempt.Id,
            MissionAttemptStatus.Succeeded,
            0,
            "token=secret-value\nDone",
            string.Empty,
            new MissionProviderMetadata("codex-app-server-v2", "codex 1.0", "thread-1", "turn-1"));
        var receipt = new MissionVerificationReceipt(
            Guid.NewGuid(),
            mission.Id,
            attempt.Id,
            "dotnet test",
            0,
            Passed: true,
            workspace.BaseCommit,
            workspace.HeadCommit,
            new string('b', 64),
            ["src/Change.cs"],
            "api_key=secret-value\nPassed",
            string.Empty,
            now,
            DateTimeOffset.UtcNow);
        await store.SaveMissionVerificationReceiptAsync(receipt);
        var review = new MissionReviewReceipt(
            Guid.NewGuid(),
            mission.Id,
            attempt.Id,
            AgentProvider.Codex,
            MissionReviewVerdict.Pass,
            0,
            $"token=secret-value\n{MissionReviewParser.PassMarker}",
            string.Empty,
            now,
            DateTimeOffset.UtcNow);
        await store.SaveMissionReviewReceiptAsync(review);

        var loadedWorkspace = await store.GetMissionWorkspaceAsync(mission.Id);
        var latestAttempt = await store.GetLatestMissionAttemptAsync(mission.Id);
        var latestReceipt = await store.GetLatestMissionVerificationReceiptAsync(mission.Id);
        var latestReview = await store.GetLatestMissionReviewReceiptAsync(mission.Id);

        Assert.Equal(savedWorkspace, loadedWorkspace);
        Assert.Equal(MissionAttemptStatus.Succeeded, completed.Status);
        Assert.Equal(completed, latestAttempt);
        Assert.Contains(SensitiveDataRedactor.Replacement, completed.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", completed.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("codex-app-server-v2", completed.ProviderProtocol);
        Assert.Equal("thread-1", completed.ProviderSessionId);
        Assert.Equal("turn-1", completed.ProviderTurnId);
        Assert.NotNull(latestReceipt);
        Assert.Equal(["src/Change.cs"], latestReceipt.ChangedPaths);
        Assert.Contains(SensitiveDataRedactor.Replacement, latestReceipt.StandardOutput, StringComparison.Ordinal);
        Assert.NotNull(latestReview);
        Assert.Equal(MissionReviewVerdict.Pass, latestReview.Verdict);
        Assert.DoesNotContain("secret-value", latestReview.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PilotReceipts_PersistRedactedEvidenceAndProduceReportInput()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "pilot.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var receipt = new MissionPilotReceipt(
            Guid.NewGuid(),
            MissionId: null,
            "docs",
            MissionPilotMode.Baseline,
            Passed: true,
            EvidenceComplete: true,
            FailureInjected: false,
            InterventionCount: 1,
            ["token=secret-value"],
            AttemptCount: 0,
            ReviewBlockCount: 0,
            RecoveryCount: 0,
            DurationMinutes: 15,
            "Windows 11",
            "Authorization: Bearer abcdefghijklmnop",
            now.AddMinutes(-15),
            now,
            now);

        await store.SaveMissionPilotReceiptAsync(receipt);
        var loaded = Assert.Single(await store.ListMissionPilotReceiptsAsync());

        Assert.Equal(receipt.Id, loaded.Id);
        Assert.Contains(SensitiveDataRedactor.Replacement, Assert.Single(loaded.InterventionReasons));
        Assert.DoesNotContain("secret-value", Assert.Single(loaded.InterventionReasons), StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.Replacement, loaded.ProviderMetadata);
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
            now,
            "main-vertical",
            "builder");

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
        Assert.Equal("main-vertical", reconciled.LayoutName);
        Assert.Equal("builder", reconciled.FocusedPaneName);
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
            _directory, "codex --token=secret-value", 42, "Synthetic failure",
            "Authorization: Bearer abcdefghijklmnop", 1024, now));
        await store.SaveDeskAsync(desk with { UpdatedAt = now.AddMinutes(1) });

        var receipt = Assert.Single(await store.ListFailureReceiptsAsync("failures"));
        Assert.Equal(42, receipt.ExitCode);
        Assert.Equal("codex --token=[REDACTED]", receipt.LaunchCommand);
        Assert.Equal("Authorization: Bearer [REDACTED]", receipt.RecentOutput);
    }

    [Fact]
    public async Task FailureReceipt_RetainsOnlyLatestTwentyPerDesk()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "retention.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        var paneId = Guid.NewGuid();
        var desk = new Desk(
            deskId,
            "retention",
            _directory,
            null,
            [],
            1,
            12_000,
            DeskStatus.Running,
            [new DeskPane(
                paneId, deskId, "%1", AgentProvider.Shell, "ops", _directory,
                "pwsh", "ops", "Test", "amber", [], null,
                DeskPaneStatus.Failed, DeskStateSource.Process, null, 1,
                now, now, now)],
            now,
            now,
            now);
        await store.SaveDeskAsync(desk);

        for (var index = 0; index < 25; index++)
        {
            await store.AddFailureReceiptAsync(new DeskFailureReceipt(
                Guid.NewGuid(), deskId, paneId, "ops", AgentProvider.Shell,
                _directory, "pwsh", 1, $"failure-{index}", $"output-{index}",
                null, now.AddMinutes(index)));
        }

        var receipts = await store.ListFailureReceiptsAsync("retention", 100);

        Assert.Equal(20, receipts.Count);
        Assert.Equal("failure-24", receipts[0].Reason);
        Assert.Equal("failure-5", receipts[^1].Reason);
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

    [Fact]
    public async Task DeskPaneLifecycle_PersistsAdditionAndGuardedRemoval()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "pane-lifecycle.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        DeskPane Pane(string paneId, string name) => new(
            Guid.NewGuid(), deskId, paneId, AgentProvider.Codex, name, _directory,
            "codex", "reviewer", "Review", "magenta", [], null,
            DeskPaneStatus.Running, DeskStateSource.Registered, null, null, now, now, now);
        await store.SaveDeskAsync(new Desk(
            deskId,
            "dynamic",
            _directory,
            null,
            [],
            2,
            12_000,
            DeskStatus.Running,
            [Pane("%1", "reviewer")],
            now,
            now,
            now));

        var added = await store.AddDeskPaneAsync(
            "dynamic", Pane("%2", "reviewer-2"), "even-horizontal");
        var queued = await store.EnqueueWorkItemAsync(
            "dynamic", "reviewer-2", "Check the diff", expensive: false);
        var removed = await store.RemoveDeskPaneAsync(
            "dynamic", "reviewer-2", "tiled");

        Assert.Equal(2, added.Panes.Count);
        Assert.Equal("even-horizontal", added.LayoutName);
        Assert.Equal("reviewer", Assert.Single(removed.Panes).Name);
        Assert.Equal("tiled", removed.LayoutName);
        var work = Assert.Single(await store.ListWorkItemsAsync("dynamic"));
        Assert.Equal(queued.Id, work.Id);
        Assert.Equal(DeskWorkItemStatus.Cancelled, work.Status);
        Assert.Equal("Pane removed by operator.", work.Outcome);
        var events = await store.ListDeskEventsAsync("dynamic", "pane.", 100);
        Assert.Contains(events, value => value.EventType == "pane.added");
        Assert.Contains(events, value => value.EventType == "pane.removed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RemoveDeskPaneAsync("dynamic", "reviewer", "tiled"));
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
