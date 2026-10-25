using Flyt.Cli;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;
using Microsoft.Data.Sqlite;

namespace Flyt.Tests;

public sealed class MissionExecutionCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flyt-mission-runner-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Run_ProducesEvidenceAndStopsAtIntegrationApproval()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var executor = new QueueExecutor(
            new CommandResult(0, "provider complete", string.Empty),
            new CommandResult(0, MissionReviewParser.PassMarker, string.Empty),
            new CommandResult(0, "tests passed", string.Empty));
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["src/Change.cs"]),
            new AllowAllSafetyScanner(),
            executor);

        var outcome = await coordinator.RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));

        Assert.Equal(MissionState.Integrating, outcome.Mission.State);
        Assert.Equal(MissionAttemptStatus.Succeeded, outcome.Attempt.Status);
        Assert.NotNull(outcome.Review);
        Assert.Equal(MissionReviewVerdict.Pass, outcome.Review.Verdict);
        Assert.NotNull(outcome.Verification);
        Assert.True(outcome.Verification.Passed);
        Assert.Equal(["src/Change.cs"], outcome.Verification.ChangedPaths);
        Assert.Equal("claude", executor.Invocations[0].FileName);
        Assert.Equal("codex", executor.Invocations[1].FileName);
        Assert.Contains("read-only", executor.Invocations[1].Arguments);
        Assert.Equal("dotnet", executor.Invocations[2].FileName);
        Assert.Equal(workspace.WorktreePath, executor.Invocations[2].WorkingDirectory);
    }

    [Fact]
    public async Task Run_BlocksWhenProviderProducesNoDiff()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, []),
            new AllowAllSafetyScanner(),
            new QueueExecutor(new CommandResult(0, "provider complete", string.Empty)));

        var outcome = await coordinator.RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));

        Assert.Equal(MissionState.Blocked, outcome.Mission.State);
        Assert.Null(outcome.Verification);
    }

    [Fact]
    public async Task Run_BlocksWhenIndependentReviewDoesNotPass()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["change.txt"]),
            new AllowAllSafetyScanner(),
            new QueueExecutor(
                new CommandResult(0, "provider complete", string.Empty),
                new CommandResult(0, MissionReviewParser.BlockMarker, string.Empty)));

        var outcome = await coordinator.RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));

        Assert.Equal(MissionState.Blocked, outcome.Mission.State);
        Assert.NotNull(outcome.Review);
        Assert.Equal(MissionReviewVerdict.Block, outcome.Review.Verdict);
        Assert.Null(outcome.Verification);
    }

    [Fact]
    public async Task Recover_BlocksInterruptedExecutionWithoutRepeatingProviderMutation()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var approvedPlan = await store.RequireApprovedMissionPlanAsync(mission.Id);
        var attempt = await store.StartMissionAttemptAsync(
            mission.Id,
            AgentProvider.Claude,
            approvedPlan.Plan.Version,
            approvedPlan.Plan.ComputeFingerprint());
        await store.SaveMissionWorkspaceAsync(workspace);
        await store.TransitionMissionAsync(
            mission.Id,
            MissionState.Executing,
            "Simulated interrupted execution.");
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["partial.txt"]),
            new AllowAllSafetyScanner(),
            new QueueExecutor());

        var outcome = await coordinator.RecoverAsync(mission.Id);

        Assert.Equal(MissionState.Blocked, outcome.Mission.State);
        Assert.Equal(attempt.Id, outcome.Attempt.Id);
        Assert.Equal(MissionAttemptStatus.Interrupted, outcome.Attempt.Status);
    }

    [Fact]
    public async Task Run_RetryUsesPriorReviewFeedbackAndFreshVerification()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        const string feedback = "Critical regression found. FLYT_REVIEW_BLOCK";
        var firstExecutor = new QueueExecutor(
            new CommandResult(0, "provider complete", string.Empty),
            new CommandResult(0, feedback, string.Empty));
        var manager = new FakeWorkspaceManager(workspace, ["change.txt"]);
        var first = await new MissionExecutionCoordinator(
            store,
            manager,
            new AllowAllSafetyScanner(),
            firstExecutor).RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));
        Assert.Equal(MissionState.Blocked, first.Mission.State);
        await store.TransitionMissionAsync(
            mission.Id,
            MissionState.Planning,
            "Operator approved retry.");
        var retryExecutor = new QueueExecutor(
            new CommandResult(0, "fix complete", string.Empty),
            new CommandResult(0, MissionReviewParser.PassMarker, string.Empty),
            new CommandResult(0, "tests passed", string.Empty));

        var retry = await new MissionExecutionCoordinator(
            store,
            manager,
            new AllowAllSafetyScanner(),
            retryExecutor).RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));

        Assert.Equal(MissionState.Integrating, retry.Mission.State);
        Assert.Equal(2, retry.Attempt.Number);
        Assert.Contains(retryExecutor.Invocations[0].Arguments, argument =>
            argument.Contains("Critical regression found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_BoundsProviderExecutionWithPersistedTimeout()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["change.txt"]),
            new AllowAllSafetyScanner(),
            new CancellingExecutor());

        var outcome = await coordinator.RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(MissionState.Blocked, outcome.Mission.State);
        Assert.Equal(MissionAttemptStatus.TimedOut, outcome.Attempt.Status);
        Assert.Null(outcome.Verification);
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

    private async Task<(FlytStateStore Store, Mission Mission, MissionWorkspace Workspace)>
        CreateApprovedMissionAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Implement the queue", _directory);
        await store.CreateMissionAsync(mission);
        await store.SaveMissionPlanAsync(MissionPlan.Create(
            mission.Id,
            ["Tests pass", "Evidence exists"],
            ["Implement", "Verify"],
            "dotnet test"));
        var approved = await store.ApproveMissionPlanAsync(mission.Id);
        var now = DateTimeOffset.UtcNow;
        return (
            store,
            approved,
            new MissionWorkspace(
                mission.Id,
                _directory,
                _directory,
                "flyt/mission-test/builder",
                "base123",
                "head123",
                now,
                now));
    }

    private sealed class FakeWorkspaceManager(
        MissionWorkspace workspace,
        IReadOnlyList<string> changedPaths) : IMissionWorkspaceManager
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
            Task.FromResult(new string('c', 64));
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

    private sealed class CancellingExecutor : ICommandExecutor
    {
        public async Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<int> RunInteractiveAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AllowAllSafetyScanner : IMissionSafetyScanner
    {
        public Task<IReadOnlyList<MissionSafetyFinding>> ScanAsync(
            MissionWorkspace workspace,
            IReadOnlyList<string> changedPaths,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MissionSafetyFinding>>([]);
    }
}
