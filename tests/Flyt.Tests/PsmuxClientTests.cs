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
        Assert.Equal(10, executor.Invocations.Count);
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
    public async Task ListSessions_SurfacesDiscoveryFailure()
    {
        var executor = new FakeCommandExecutor(failAtInvocation: 1);
        var client = new PsmuxClient(executor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListSessionsAsync());

        Assert.Contains("list-sessions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPanes_ParsesStableIdentityAndRuntimeState()
    {
        var executor = new FakeCommandExecutor
        {
            NextOutput = "%9\u001fbuilder\u001fclaude\u001f1\n",
        };
        var client = new PsmuxClient(executor);

        var panes = await client.ListPanesAsync("daily");

        var pane = Assert.Single(panes);
        Assert.Equal("%9", pane.PaneId);
        Assert.Equal("builder", pane.Title);
        Assert.Equal("claude", pane.CurrentCommand);
        Assert.True(pane.IsActive);
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

        public FakeCommandExecutor(int? failAtInvocation = null)
        {
            _failAtInvocation = failAtInvocation;
        }

        public List<CommandInvocation> Invocations { get; } = [];

        public string? NextOutput { get; init; }

        public Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            if (_failAtInvocation == Invocations.Count)
            {
                return Task.FromResult(new CommandResult(42, string.Empty, "synthetic failure"));
            }

            var command = invocation.Arguments[0];
            var output = NextOutput ?? (command is "new-session" or "split-window"
                ? $"%{_nextPaneId++}{Environment.NewLine}"
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
