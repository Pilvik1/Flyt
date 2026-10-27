using Flyt.Core;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class PsmuxClientTests
{
    [Fact]
    public async Task StartDesk_UsesReturnedPaneIdsAndFinishesWithAdaptiveLayout()
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
        Assert.True(executor.Invocations.Count >= 18);
        Assert.Contains("240", executor.Invocations[0].Arguments);
        Assert.Contains("60", executor.Invocations[0].Arguments);
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%1"));
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%2"));
        Assert.Contains(executor.Invocations, call => call.Arguments.Contains("%3"));
        var layout = executor.Invocations.Last(value => value.Arguments[0] == "select-layout");
        Assert.Equal("main-vertical", layout.Arguments[^1]);
        Assert.Contains(executor.Invocations, value => value.Arguments.Contains("pane-border-format"));
    }

    [Fact]
    public async Task ConfigureDeskControls_BindsAttentionActionsAndDirectPaneJumps()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(executor);

        await client.ConfigureDeskControlsAsync("daily");

        Assert.Contains(executor.Invocations, value =>
            value.Arguments.SequenceEqual([
                "bind-key", "A", "run-shell", "-b",
                "pwsh -NoProfile -Command \"flyt desk focus --name daily --next\"",
            ]));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("display-popup")
            && value.Arguments.Any(argument => argument.Contains("--action send", StringComparison.Ordinal)));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.SequenceEqual([
                "bind-key", "n", "display-popup", "-w", "82%", "-h", "72%", "-E",
                "pwsh -NoProfile -Command \"flyt desk action --name daily --pane-id '#{pane_id}' --action add-pane\"",
            ]));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.SequenceEqual([
                "bind-key", "N", "display-popup", "-w", "82%", "-h", "72%", "-E",
                "pwsh -NoProfile -Command \"flyt desk action --name daily --pane-id '#{pane_id}' --action add-pane\"",
            ]));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments[0] == "bind-key"
            && value.Arguments[1] == "x"
            && value.Arguments.Any(argument => argument.Contains("--action remove-pane", StringComparison.Ordinal)));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.SequenceEqual(["bind-key", "-n", "M-6", "select-pane", "-t", "daily:0.5"]));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.SequenceEqual(["bind-key", "-n", "M-0", "select-pane", "-t", "daily:0.9"]));
    }

    [Fact]
    public async Task UpdateDeskChrome_SetsSummaryAndAttentionBorder()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(executor);

        await client.UpdateDeskChromeAsync(
            "daily", "Flyt/main", "6 agents · 1 waiting", DeskPaneStatus.NeedsInput);

        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("status-left")
            && value.Arguments.Any(argument => argument.Contains(" FLYT ", StringComparison.Ordinal)));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("pane-active-border-style") && value.Arguments.Contains("fg=yellow,bold"));
    }

    [Fact]
    public async Task ConfigureRoleBorderFormat_MapsPaneIndexesToStableRoleColours()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(executor);

        await client.ConfigureRoleBorderFormatAsync(
            "daily",
            [
                new DeskPaneHandle("%7", AgentSessionSpec.Create(
                    AgentProvider.Claude, "builder", Environment.CurrentDirectory, "claude", role: "builder")),
                new DeskPaneHandle("%9", AgentSessionSpec.Create(
                    AgentProvider.Codex, "reviewer", Environment.CurrentDirectory, "codex", role: "reviewer")),
            ]);

        Assert.Equal(2, executor.Invocations.Count);
        var format = executor.Invocations[0].Arguments[^1];
        Assert.Contains("#{pane_id},%7", format, StringComparison.Ordinal);
        Assert.Contains("fg=blue", format, StringComparison.Ordinal);
        Assert.Contains("bg=blue", format, StringComparison.Ordinal);
        Assert.Contains("fg=magenta", format, StringComparison.Ordinal);
        Assert.Contains("bg=magenta", format, StringComparison.Ordinal);
        Assert.Contains("pane_active", format, StringComparison.Ordinal);
        Assert.Equal(
            ["set-option", "-t", "daily", "-g", "pane-border-status", "top"],
            executor.Invocations[1].Arguments);
    }

    [Fact]
    public async Task PresentationFallback_UsesAsciiAndRemovesRoleColoursAndDimming()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(
            executor,
            presentation: new PsmuxPresentationProfile(
                ColorEnabled: false,
                UnicodeEnabled: false,
                HighContrast: true));
        var session = Session(AgentProvider.Claude, "builder", "claude");

        await client.ConfigureDeskPresentationAsync("daily", default);
        await client.ConfigureRoleBorderFormatAsync("daily", [new DeskPaneHandle("%1", session)]);
        await client.SetPanePresentationAsync("%1", session, DeskPaneStatus.Working);

        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("window-style") && value.Arguments.Contains("dim=0"));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("pane-border-format")
            && !value.Arguments[^1].Contains("fg=", StringComparison.Ordinal));
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("pane-border-status")
            && value.Arguments[^1] == "top");
        Assert.Contains(executor.Invocations, value =>
            value.Arguments.Contains("select-pane")
            && value.Arguments[^1].Contains(" | * WORKING | ", StringComparison.Ordinal));
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

    [Fact]
    public async Task RemovePane_TargetsOnlyTheRequestedPane()
    {
        var executor = new FakeCommandExecutor();
        var client = new PsmuxClient(executor);

        await client.RemovePaneAsync("%7");

        var invocation = Assert.Single(executor.Invocations);
        Assert.Equal(["kill-pane", "-t", "%7"], invocation.Arguments);
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
