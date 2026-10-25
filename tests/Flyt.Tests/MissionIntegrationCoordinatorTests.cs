using Flyt.Cli;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;
using Microsoft.Data.Sqlite;

namespace Flyt.Tests;

public sealed class MissionIntegrationCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flyt-integration-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Prepare_RevalidatesEvidenceThenCreatesDraftPrWithoutMerging()
    {
        var (store, mission, workspace, fingerprint) = await CreateVerifiedMissionAsync();
        var executor = new QueueExecutor(
            new CommandResult(0, string.Empty, string.Empty),
            new CommandResult(0, string.Empty, string.Empty),
            new CommandResult(0, "commit123\n", string.Empty),
            new CommandResult(0, string.Empty, string.Empty),
            new CommandResult(0, "https://github.com/Pilvik1/Flyt/pull/99\n", string.Empty));
        var coordinator = new MissionIntegrationCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["src/Change.cs"], fingerprint),
            new AllowAllSafetyScanner(),
            executor);

        var result = await coordinator.PrepareAsync(
            mission.Id,
            "feat: complete mission",
            push: true,
            createDraftPullRequest: true,
            "main");

        Assert.Equal(MissionState.Completed, result.Mission.State);
        Assert.Equal("commit123", result.Commit);
        Assert.True(result.Pushed);
        Assert.EndsWith("/99", result.PullRequestUrl, StringComparison.Ordinal);
        Assert.Contains(executor.Invocations, invocation => invocation.Arguments.Contains("push"));
        Assert.Contains(executor.Invocations, invocation => invocation.FileName == "gh");
        Assert.DoesNotContain(executor.Invocations, invocation => invocation.Arguments.Contains("merge"));
    }

    [Fact]
    public async Task Prepare_RejectsDiffChangedAfterVerification()
    {
        var (store, mission, workspace, _) = await CreateVerifiedMissionAsync();
        var coordinator = new MissionIntegrationCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["src/Change.cs"], new string('x', 64)),
            new AllowAllSafetyScanner(),
            new QueueExecutor());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareAsync(
            mission.Id,
            "feat: unsafe stale diff",
            push: false,
            createDraftPullRequest: false,
            "main"));

        Assert.Contains("changed after verification", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task<(FlytStateStore Store, Mission Mission, MissionWorkspace Workspace, string Fingerprint)>
        CreateVerifiedMissionAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Prepare verified work", _directory);
        await store.CreateMissionAsync(mission);
        var plan = await store.SaveMissionPlanAsync(MissionPlan.Create(
            mission.Id,
            ["Tests pass"],
            ["Build", "Review", "Verify"],
            "dotnet test"));
        mission = await store.ApproveMissionPlanAsync(mission.Id);
        var now = DateTimeOffset.UtcNow;
        var workspace = await store.SaveMissionWorkspaceAsync(new MissionWorkspace(
            mission.Id,
            _directory,
            _directory,
            "flyt/mission-test/builder",
            "base123",
            "head123",
            now,
            now));
        var attempt = await store.StartMissionAttemptAsync(
            mission.Id,
            AgentProvider.Claude,
            plan.Version,
            plan.ComputeFingerprint());
        mission = await store.TransitionMissionAsync(mission.Id, MissionState.Executing, "Builder started.");
        await store.CompleteMissionAttemptAsync(
            attempt.Id,
            MissionAttemptStatus.Succeeded,
            0,
            string.Empty,
            string.Empty);
        mission = await store.TransitionMissionAsync(mission.Id, MissionState.Reviewing, "Review started.");
        mission = await store.TransitionMissionAsync(mission.Id, MissionState.Verifying, "Review passed.");
        mission = await store.TransitionMissionAsync(mission.Id, MissionState.Integrating, "Verification passed.");
        var fingerprint = new string('d', 64);
        await store.SaveMissionVerificationReceiptAsync(new MissionVerificationReceipt(
            Guid.NewGuid(),
            mission.Id,
            attempt.Id,
            plan.VerificationCommand,
            0,
            Passed: true,
            workspace.BaseCommit,
            workspace.HeadCommit,
            fingerprint,
            ["src/Change.cs"],
            "passed",
            string.Empty,
            now,
            DateTimeOffset.UtcNow));
        return (store, mission, workspace, fingerprint);
    }

    private sealed class FakeWorkspaceManager(
        MissionWorkspace workspace,
        IReadOnlyList<string> changedPaths,
        string fingerprint) : IMissionWorkspaceManager
    {
        public Task<MissionWorkspace> EnsureBuilderWorktreeAsync(
            Mission mission,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(workspace);

        public Task<IReadOnlyList<string>> GetChangedPathsAsync(
            MissionWorkspace missionWorkspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(changedPaths);

        public Task<string> GetHeadCommitAsync(
            MissionWorkspace missionWorkspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(workspace.HeadCommit);

        public Task<string> ComputeDiffFingerprintAsync(
            MissionWorkspace missionWorkspace,
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(fingerprint);
    }

    private sealed class AllowAllSafetyScanner : IMissionSafetyScanner
    {
        public Task<IReadOnlyList<MissionSafetyFinding>> ScanAsync(
            MissionWorkspace workspace,
            IReadOnlyList<string> changedPaths,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MissionSafetyFinding>>([]);
    }

    private sealed class QueueExecutor(params CommandResult[] results) : ICommandExecutor
    {
        private readonly Queue<CommandResult> _results = new(results);

        public List<CommandInvocation> Invocations { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(_results.Dequeue());
        }

        public Task<int> RunInteractiveAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
