using Flyt.Core;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class PsmuxClientTests
{
    [Fact]
    public async Task StartDesk_UsesReturnedPaneIdsAndFinishesWithTiledLayout()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(executor);
        var sessions = new[]
        {
            Session(AgentProvider.Claude, "builder", "claude --resume"),
            Session(AgentProvider.Codex, "reviewer", "codex"),
            Session(AgentProvider.Shell, "ops", "pwsh"),
        };

        var started = await client.StartDeskAsync("my desk", sessions);

        Assert.Equal("my-desk", started.SessionName);
        Assert.Equal(["%1", "%2", "%3"], started.Panes.Select(pane => pane.PaneId));
        Assert.Equal(18, executor.Invocations.Count);
        Assert.Contains("240", executor.Invocations[0].Arguments);
        Assert.Contains("60", executor.Invocations[0].Arguments);
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%1"));
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%2"));
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%3"));
        Assert.Equal("select-layout", executor.Invocations[^1].Arguments[0]);
    }

    [Fact]
    public async Task StartDesk_RollsBackPartialSessionWhenPaneConfigurationFails()
    {
        var executor = new FakeCommandExecutor(failAtInvocation: 5);
        var client = new PsmuxClient(executor);
        var sessions = new[]
        {
            Session(AgentProvider.Claude, "builder", "claude"),
            Session(AgentProvider.Codex, "reviewer", "codex"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartDeskAsync("partial", sessions));

        Assert.Contains("rolled back", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("kill-session", executor.Invocations[^1].Arguments[0]);
        Assert.Contains("partial", executor.Invocations[^1].Arguments);
    }

    [Fact]
    public async Task StartDesk_RejectsReusedPaneIdAndRollsBack()
    {
        var executor = new FakeCommandExecutor(reusePaneId: true);
        var client = new PsmuxClient(executor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.StartDeskAsync(
                "duplicate-pane",
                [Session(AgentProvider.Shell, "one", "pwsh"), Session(AgentProvider.Shell, "two", "pwsh")]));

        Assert.Contains("rolled back", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("kill-session", executor.Invocations[^1].Arguments[0]);
    }

    [Fact]
    public async Task ListSessions_SurfacesDiscoveryFailure()
    {
        var executor = new FakeCommandExecutor(failAtInvocation: 1);
        var client = new PsmuxClient(executor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListSessionsAsync());

        Assert.Contains("list-sessions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListSessions_TreatsStoppedServerAsEmpty()
    {
        var executor = new FakeCommandExecutor
        {
            NextResult = new CommandResult(1, string.Empty, "psmux: no server running on test socket"),
        };
        var client = new PsmuxClient(executor);

        var sessions = await client.ListSessionsAsync();

        Assert.Empty(sessions);
    }

    [Fact]
    public async Task ListPanes_ParsesStableIdentityAndRuntimeState()
    {
        var executor = new FakeCommandExecutor
        {
            NextOutput = "%9\u001fbuilder | 🟡 NEEDS INPUT | builder | Implement\u001fclaude\u001f1"
                + "\u001f123\u001f0\u001f\u001fblocked\u001f0\n",
        };
        var client = new PsmuxClient(executor);

        var panes = await client.ListPanesAsync("daily");

        var pane = Assert.Single(panes);
        Assert.Equal("%9", pane.PaneId);
        Assert.Equal("builder | 🟡 NEEDS INPUT | builder | Implement", pane.Title);
        Assert.Equal("builder", pane.Name);
        Assert.Equal("claude", pane.CurrentCommand);
        Assert.True(pane.IsActive);
        Assert.Equal(123, pane.ProcessId);
        Assert.Equal("blocked", pane.ProgramState);
    }

    [Fact]
    public async Task SendText_UsesBase64PasteBoundaryAndOptionalSubmit()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(executor);

        await client.SendTextAsync("%7", "line one\nline; two", submit: true);

        Assert.Equal(2, executor.Invocations.Count);
        var paste = executor.Invocations[0];
        Assert.Equal("send-paste", paste.Arguments[0]);
        Assert.Equal("line one\nline; two", System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(paste.Arguments[^1])));
        Assert.Equal("send-keys", executor.Invocations[1].Arguments[0]);
    }

    [Theory]
    [InlineData("working", false, null, false, DeskPaneStatus.Working)]
    [InlineData("blocked", false, null, false, DeskPaneStatus.NeedsInput)]
    [InlineData("done", false, null, false, DeskPaneStatus.Done)]
    [InlineData("done", true, null, false, DeskPaneStatus.Idle)]
    [InlineData("error", false, null, false, DeskPaneStatus.Failed)]
    [InlineData("", false, 42, true, DeskPaneStatus.Failed)]
    [InlineData("", false, 0, true, DeskPaneStatus.Exited)]
    public void StatusMapper_MapsStructuredAndProcessStates(
        string programState,
        bool seen,
        int? exitCode,
        bool dead,
        DeskPaneStatus expected)
    {
        var snapshot = new PsmuxPaneSnapshot(
            "%1", "builder", "builder", "claude", false, 123, dead, exitCode, programState, seen);

        Assert.Equal(expected, PsmuxStatusMapper.Map(snapshot));
    }

    private static AgentSessionSpec Session(
        AgentProvider provider,
        string name,
        string launchCommand) =>
        AgentSessionSpec.Create(provider, name, Environment.CurrentDirectory, launchCommand);

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        private int _nextPaneId = 1;
        private readonly int? _failAtInvocation;
        private readonly bool _reusePaneId;

        public FakeCommandExecutor(int? failAtInvocation = null, bool reusePaneId = false)
        {
            _failAtInvocation = failAtInvocation;
            _reusePaneId = reusePaneId;
        }

        public List<CommandInvocation> Invocations { get; } = [];

        public string? NextOutput { get; init; }

        public CommandResult? NextResult { get; init; }

        public Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            if (_failAtInvocation == Invocations.Count)
            {
                return Task.FromResult(new CommandResult(42, string.Empty, "synthetic failure"));
            }

            if (NextResult is not null)
            {
                return Task.FromResult(NextResult);
            }

            var command = invocation.Arguments[0];
            var output = NextOutput ?? (command is "new-session" or "split-window"
                ? $"%{(_reusePaneId ? 1 : _nextPaneId++)}{Environment.NewLine}"
                : string.Empty);
            return Task.FromResult(new CommandResult(0, output, string.Empty));
        }

        public Task<int> RunInteractiveAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(0);
        }
    }
}
