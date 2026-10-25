using Flyt.Cli;
using Flyt.Core;
using Flyt.Providers;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class CodexAppServerProtocolTests
{
    [Fact]
    public async Task Routing_UsesAppServerByDefault()
    {
        var connection = SuccessfulConnection();
        var factory = new FakeConnectionFactory(connection);
        var runner = new RoutingMissionProviderRunner(
            new QueueExecutor(new CommandResult(0, "codex 1.0", string.Empty)),
            factory,
            codexTransport: "app-server");

        var result = await runner.RunAsync(AgentProvider.Codex, CreateRequest(readOnly: false));

        Assert.True(result.IsSuccess);
        Assert.Equal("codex-app-server-v2", result.Metadata.Protocol);
        Assert.Equal("codex 1.0", result.Metadata.Version);
        Assert.Equal(1, factory.StartCount);
    }

    [Fact]
    public async Task Routing_UsesExecOnlyWhenExplicitlySelected()
    {
        var executor = new QueueExecutor(new CommandResult(0, "done", string.Empty));
        var factory = new FakeConnectionFactory(SuccessfulConnection());
        var runner = new RoutingMissionProviderRunner(executor, factory, codexTransport: "exec");

        var result = await runner.RunAsync(AgentProvider.Codex, CreateRequest(readOnly: false));

        Assert.True(result.IsSuccess);
        Assert.Equal("codex-exec-json", result.Metadata.Protocol);
        Assert.Equal(0, factory.StartCount);
        Assert.Equal("codex", Assert.Single(executor.Invocations).FileName);
    }

    [Fact]
    public async Task Run_CompletesTypedLifecycleAndCapturesIds()
    {
        var connection = new FakeConnection(
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr-1\"}}}",
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\"}}}",
            "{\"method\":\"item/agentMessage/delta\",\"params\":{\"delta\":\"done\"}}",
            "{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");

        var result = await new CodexAppServerProtocol(connection).RunAsync(
            "Implement the change",
            Environment.CurrentDirectory,
            readOnly: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.StandardOutput);
        Assert.Equal("codex-app-server-v2", result.Metadata.Protocol);
        Assert.Equal("thr-1", result.Metadata.SessionId);
        Assert.Equal("turn-1", result.Metadata.TurnId);
        Assert.Contains(connection.Writes, value => value.Contains("\"method\":\"initialize\"", StringComparison.Ordinal));
        Assert.Contains(connection.Writes, value => value.Contains("\"sandbox\":\"workspace-write\"", StringComparison.Ordinal));
        Assert.True(connection.Stopped);
    }

    [Fact]
    public async Task Run_UsesReadOnlySandboxForReviewer()
    {
        var connection = SuccessfulConnection();

        var result = await new CodexAppServerProtocol(connection).RunAsync(
            "Review only",
            Environment.CurrentDirectory,
            readOnly: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(connection.Writes, value => value.Contains("\"sandbox\":\"read-only\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public async Task Run_RejectsNonCompletedTurnStatus(string status)
    {
        var connection = new FakeConnection(
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr-1\"}}}",
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\"}}}",
            $"{{\"method\":\"turn/completed\",\"params\":{{\"turn\":{{\"status\":\"{status}\"}}}}}}");

        var result = await new CodexAppServerProtocol(connection).RunAsync(
            "Do work",
            Environment.CurrentDirectory,
            readOnly: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains(status, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_FailsClosedOnInteractiveServerRequest()
    {
        var connection = new FakeConnection(
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr-1\"}}}",
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\"}}}",
            "{\"id\":44,\"method\":\"item/commandExecution/requestApproval\",\"params\":{}}");

        var result = await new CodexAppServerProtocol(connection).RunAsync(
            "Do work",
            Environment.CurrentDirectory,
            readOnly: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("fails closed", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(connection.Writes, value => value.Contains("\"id\":44", StringComparison.Ordinal)
                                                    && value.Contains("\"error\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RejectsMalformedProtocolOutput()
    {
        var connection = new FakeConnection("this is not json");

        var result = await new CodexAppServerProtocol(connection).RunAsync(
            "Do work",
            Environment.CurrentDirectory,
            readOnly: false,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("malformed JSON", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_InterruptsKnownTurnWhenCancelled()
    {
        var connection = new FakeConnection(
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr-1\"}}}",
            "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\"}}}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CodexAppServerProtocol(connection).RunAsync(
                "Do work",
                Environment.CurrentDirectory,
                readOnly: false,
                cancellation.Token));

        Assert.Contains(connection.Writes, value => value.Contains("\"method\":\"turn/interrupt\"", StringComparison.Ordinal));
        Assert.True(connection.Stopped);
    }

    private static FakeConnection SuccessfulConnection() => new(
        "{\"id\":1,\"result\":{}}",
        "{\"id\":2,\"result\":{\"thread\":{\"id\":\"thr-1\"}}}",
        "{\"id\":3,\"result\":{\"turn\":{\"id\":\"turn-1\"}}}",
        "{\"method\":\"turn/completed\",\"params\":{\"turn\":{\"status\":\"completed\"}}}");

    private static MissionProviderRequest CreateRequest(bool readOnly)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = Mission.Create("Implement the queue", Environment.CurrentDirectory);
        var plan = MissionPlan.Create(
            mission.Id,
            ["Tests pass"],
            ["Implement", "Verify"],
            "dotnet test");
        var workspace = new MissionWorkspace(
            mission.Id,
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            "flyt/test",
            "base",
            "head",
            now,
            now);
        return new MissionProviderRequest(mission, plan, workspace, readOnly);
    }

    private sealed class FakeConnectionFactory(FakeConnection connection) : ICodexAppServerConnectionFactory
    {
        public int StartCount { get; private set; }

        public Task<ICodexAppServerConnection> StartAsync(
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult<ICodexAppServerConnection>(connection);
        }
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

    private sealed class FakeConnection(params string[] lines) : ICodexAppServerConnection
    {
        private readonly Queue<string> _lines = new(lines);

        public List<string> Writes { get; } = [];

        public bool Stopped { get; private set; }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(line);
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            if (_lines.Count > 0)
            {
                return _lines.Dequeue();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public Task<string> GetStandardErrorAsync() => Task.FromResult(string.Empty);

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
