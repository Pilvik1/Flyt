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
            AgentSessionSpec.Create(AgentProvider.Claude, "builder", Environment.CurrentDirectory),
            AgentSessionSpec.Create(AgentProvider.Codex, "reviewer", Environment.CurrentDirectory),
            AgentSessionSpec.Create(AgentProvider.Shell, "ops", Environment.CurrentDirectory),
        };

        var sessionName = await client.StartDeskAsync("my desk", sessions);

        Assert.Equal("my-desk", sessionName);
        Assert.Equal(10, executor.Invocations.Count);
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%1"));
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%2"));
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%3"));
        Assert.Equal("select-layout", executor.Invocations[^1].Arguments[0]);
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        private int _nextPaneId = 1;

        public List<CommandInvocation> Invocations { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            var command = invocation.Arguments[0];
            var output = command is "new-session" or "split-window"
                ? $"%{_nextPaneId++}{Environment.NewLine}"
                : string.Empty;
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

