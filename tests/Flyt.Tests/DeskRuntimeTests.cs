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
        Path.GetTempPath(), "flytv1-runtime-tests", Guid.NewGuid().ToString("N"));

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
