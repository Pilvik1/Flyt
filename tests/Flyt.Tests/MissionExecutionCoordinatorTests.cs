using Flyt.Cli;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;

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
            new CommandResult(0, "tests passed", string.Empty));
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["src/Change.cs"]),
            executor);

        var outcome = await coordinator.RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));

        Assert.Equal(MissionState.Integrating, outcome.Mission.State);
        Assert.Equal(MissionAttemptStatus.Succeeded, outcome.Attempt.Status);
        Assert.NotNull(outcome.Verification);
        Assert.True(outcome.Verification.Passed);
        Assert.Equal(["src/Change.cs"], outcome.Verification.ChangedPaths);
        Assert.Equal("claude", executor.Invocations[0].FileName);
        Assert.Equal("dotnet", executor.Invocations[1].FileName);
        Assert.Equal(workspace.WorktreePath, executor.Invocations[1].WorkingDirectory);
    }

    [Fact]
    public async Task Run_BlocksWhenProviderProducesNoDiff()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, []),
            new QueueExecutor(new CommandResult(0, "provider complete", string.Empty)));

        var outcome = await coordinator.RunAsync(
            mission.Id,
            AgentProvider.Claude,
            TimeSpan.FromMinutes(1));

        Assert.Equal(MissionState.Blocked, outcome.Mission.State);
        Assert.Null(outcome.Verification);
    }

    [Fact]
    public async Task Run_BoundsProviderExecutionWithPersistedTimeout()
    {
        var (store, mission, workspace) = await CreateApprovedMissionAsync();
        var coordinator = new MissionExecutionCoordinator(
            store,
            new FakeWorkspaceManager(workspace, ["change.txt"]),
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
}
