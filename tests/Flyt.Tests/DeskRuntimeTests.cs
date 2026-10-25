using Flyt.Cli;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Microsoft.Data.Sqlite;

namespace Flyt.Tests;

public sealed class DeskRuntimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "flyt-runtime-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StarterTemplate_RoundTripsIntoTypedSessions()
    {
        var path = await DeskTemplateLoader.WriteStarterAsync(_directory);
        var (template, loadedPath) = await DeskTemplateLoader.LoadAsync("default", _directory);
        var sessions = DeskTemplateLoader.BuildSessions(
            template,
            _directory,
            ProviderRegistry.CreateDefault());

        Assert.Equal(path, loadedPath);
        Assert.Equal("full-review", template.Name);
        Assert.Equal(5, sessions.Count);
        Assert.Equal("magenta", sessions.Single(value => value.Name == "reviewer").Color);
    }

    [Fact]
    public async Task Reconcile_UsesProviderAttentionSignalAndPersistsSource()
    {
        var store = await CreateDeskAsync("daily");
        var executor = new RuntimeExecutor
        {
            SessionName = "daily",
            PaneOutput = "%7\u001fbuilder | 🔵 RUNNING | builder | Implement\u001fclaude\u001f1"
                + "\u001f123\u001f0\u001f\u001f\u001f0\n",
            CaptureOutput = "Permission required: allow this command [y/n]",
        };

        var result = await DeskRuntime.ReconcileAsync(
            new PsmuxClient(executor), store, applyStyles: true, captureFailures: true, default);

        var pane = Assert.Single(Assert.Single(result.Desks).Panes);
        Assert.Equal(DeskPaneStatus.NeedsInput, pane.Status);
        Assert.Equal(DeskStateSource.Provider, pane.StatusSource);
        Assert.Contains(executor.Invocations, value => value.Arguments[0] == "select-pane");
    }

    [Fact]
    public async Task Reconcile_CapturesFailureReceiptOnFirstDeadTransition()
    {
        var store = await CreateDeskAsync("failure");
        var executor = new RuntimeExecutor
        {
            SessionName = "failure",
            PaneOutput = "%7\u001fbuilder\u001fclaude\u001f0"
                + "\u001f123\u001f1\u001f42\u001f\u001f0\n",
            CaptureOutput = "fatal: synthetic crash",
        };

        await DeskRuntime.ReconcileAsync(
            new PsmuxClient(executor), store, applyStyles: false, captureFailures: true, default);

        var receipt = Assert.Single(await store.ListFailureReceiptsAsync("failure"));
        Assert.Equal(42, receipt.ExitCode);
        Assert.Contains("synthetic crash", receipt.RecentOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueDispatcher_EnforcesExpensiveConcurrencyAndAdvancesInOrder()
    {
        Directory.CreateDirectory(_directory);
        var store = new FlytStateStore(Path.Combine(_directory, "queue.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        var desk = new Desk(
            deskId, "queue", _directory, "test", [], 1, 12_000, DeskStatus.Running,
            [
                CreatePane(deskId, "%1", "verifier-a", now),
                CreatePane(deskId, "%2", "verifier-b", now),
            ],
            now, now, now);
        desk = await store.SaveDeskAsync(desk);
        var first = await store.EnqueueWorkItemAsync("queue", "verifier-a", "Run suite A", expensive: true);
        var second = await store.EnqueueWorkItemAsync("queue", "verifier-b", "Run suite B", expensive: true);
        var executor = new RuntimeExecutor();
        var psmux = new PsmuxClient(executor);

        var initial = new DeskRuntimeSnapshot([desk], [], new Dictionary<string, PaneResourceSnapshot>(), []);
        var firstPass = await DeskQueueDispatcher.DispatchAsync(psmux, store, initial, "queue", default);
        var afterFirst = await store.ListWorkItemsAsync("queue");

        Assert.Equal(1, firstPass.Dispatched);
        Assert.Equal(1, firstPass.Remaining);
        Assert.Equal(DeskWorkItemStatus.Dispatched, afterFirst.Single(value => value.Id == first.Id).Status);
        Assert.Equal(DeskWorkItemStatus.Queued, afterFirst.Single(value => value.Id == second.Id).Status);

        await Task.Delay(5);
        desk = await store.UpdateDeskRuntimeAsync(
            "queue",
            DeskStatus.Running,
            [
                new DeskPaneRuntime("verifier-a", "%1", DeskPaneStatus.Done, DeskStateSource.ProgramStatus, 1, null),
                new DeskPaneRuntime("verifier-b", "%2", DeskPaneStatus.Idle, DeskStateSource.ProgramStatus, 2, null),
            ]);
        var secondPass = await DeskQueueDispatcher.DispatchAsync(
            psmux,
            store,
            new DeskRuntimeSnapshot([desk], [], new Dictionary<string, PaneResourceSnapshot>(), []),
            "queue",
            default);
        var afterSecond = await store.ListWorkItemsAsync("queue");

        Assert.Equal(1, secondPass.Completed);
        Assert.Equal(1, secondPass.Dispatched);
        Assert.Equal(DeskWorkItemStatus.Completed, afterSecond.Single(value => value.Id == first.Id).Status);
        Assert.Equal(DeskWorkItemStatus.Dispatched, afterSecond.Single(value => value.Id == second.Id).Status);
        Assert.Equal(2, executor.Invocations.Count(value => value.Arguments[0] == "send-paste"));
    }

    [Fact]
    public async Task QueueDispatcher_HoldsExpensiveWorkWhenMemoryGateIsClosed()
    {
        var store = await CreateDeskAsync("memory-gate");
        var desk = await store.GetDeskAsync("memory-gate");
        Assert.NotNull(desk);
        await store.EnqueueWorkItemAsync("memory-gate", "builder", "Run tests", expensive: true);
        var executor = new RuntimeExecutor();
        var resources = new Dictionary<string, PaneResourceSnapshot>
        {
            [DeskRuntime.ResourceKey("memory-gate", "builder")] =
                new PaneResourceSnapshot(123, 13_000L * 1024 * 1024),
        };

        var result = await DeskQueueDispatcher.DispatchAsync(
            new PsmuxClient(executor),
            store,
            new DeskRuntimeSnapshot([desk!], [], resources, []),
            "memory-gate",
            default);

        Assert.Equal(0, result.Dispatched);
        Assert.Equal(1, result.Remaining);
        Assert.DoesNotContain(executor.Invocations, value => value.Arguments[0] == "send-paste");
    }

    private DeskPane CreatePane(Guid deskId, string paneId, string name, DateTimeOffset now) => new(
        Guid.NewGuid(), deskId, paneId, AgentProvider.Codex, name, _directory,
        "codex", "verifier", "Verify", "green", ["expensive"], null,
        DeskPaneStatus.Idle, DeskStateSource.Registered, null, null, now, now, now);

    private async Task<FlytStateStore> CreateDeskAsync(string name)
    {
        Directory.CreateDirectory(_directory);
        var store = new FlytStateStore(Path.Combine(_directory, $"{name}.db"));
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        await store.SaveDeskAsync(new Desk(
            deskId, name, _directory, "test", [], 2, 12_000, DeskStatus.Running,
            [new DeskPane(
                Guid.NewGuid(), deskId, "%1", AgentProvider.Claude, "builder", _directory,
                "claude", "builder", "Implement", "blue", ["write"], null,
                DeskPaneStatus.Running, DeskStateSource.Registered, null, null, now, now, now)],
            now, now, now));
        return store;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RuntimeExecutor : ICommandExecutor
    {
        public string SessionName { get; init; } = "daily";

        public string PaneOutput { get; init; } = string.Empty;

        public string CaptureOutput { get; init; } = string.Empty;

        public List<CommandInvocation> Invocations { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            var output = invocation.Arguments[0] switch
            {
                "list-sessions" => $"{SessionName}\n",
                "list-panes" => PaneOutput,
                "capture-pane" => CaptureOutput,
                _ => string.Empty,
            };
            return Task.FromResult(new CommandResult(0, output, string.Empty));
        }

        public Task<int> RunInteractiveAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
