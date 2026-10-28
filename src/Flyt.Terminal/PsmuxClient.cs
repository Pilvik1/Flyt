using Flyt.Core;

namespace Flyt.Terminal;

public sealed class PsmuxClient(
    ICommandExecutor executor,
    string executable = "psmux",
    PsmuxPresentationProfile? presentation = null)
{
    private readonly ICommandExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly PsmuxPresentationProfile _presentation = presentation
        ?? new PsmuxPresentationProfile(ColorEnabled: true, UnicodeEnabled: true, HighContrast: false);

    public async Task<CommandResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _executor.RunAsync(
                new CommandInvocation(executable, ["-V"]),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CommandResult(127, string.Empty, exception.Message);
        }
    }

    public async Task<DeskStartResult> StartDeskAsync(
        string sessionName,
        IReadOnlyList<AgentSessionSpec> sessions,
        CancellationToken cancellationToken = default)
    {
        PsmuxLayoutPlanner.Validate(sessionName, sessions);
        var normalizedName = PsmuxLayoutPlanner.NormalizeSessionName(sessionName);

        var panes = new List<DeskPaneHandle>();
        var sessionCreated = false;
        try
        {
            var firstResult = await RunRequiredAsync(
                new CommandInvocation(
                    executable,
                    [
                        "new-session",
                        "-d",
                        "-P",
                        "-F",
                        "#{pane_id}",
                        "-s",
                        normalizedName,
                        "-x",
                        "240",
                        "-y",
                        "60",
                        "-c",
                        sessions[0].WorkingDirectory,
                    ]),
                cancellationToken).ConfigureAwait(false);
            sessionCreated = true;
            var firstPane = RequirePaneId(firstResult.StandardOutput);
            await ConfigureDeskPresentationAsync(normalizedName, cancellationToken).ConfigureAwait(false);
            panes.Add(new DeskPaneHandle(firstPane, sessions[0]));
            await ConfigurePaneAsync(firstPane, sessions[0], cancellationToken).ConfigureAwait(false);

            for (var index = 1; index < sessions.Count; index++)
            {
                var splitResult = await RunRequiredAsync(
                    new CommandInvocation(
                        executable,
                        [
                            "split-window",
                            "-d",
                            "-P",
                            "-F",
                            "#{pane_id}",
                            "-t",
                            $"{normalizedName}:0",
                            "-c",
                            sessions[index].WorkingDirectory,
                        ]),
                    cancellationToken).ConfigureAwait(false);
                var paneId = RequirePaneId(splitResult.StandardOutput);
                if (panes.Any(value => string.Equals(value.PaneId, paneId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"psmux reused pane id '{paneId}' while creating '{sessions[index].Name}'. "
                        + "The detached canvas is too small or psmux failed to split the window.");
                }

                panes.Add(new DeskPaneHandle(paneId, sessions[index]));
                await ConfigurePaneAsync(paneId, sessions[index], cancellationToken).ConfigureAwait(false);
                await RunRequiredAsync(
                    PsmuxLayoutPlanner.LayoutCommand(executable, normalizedName, sessions.Count),
                    cancellationToken).ConfigureAwait(false);
            }

            await RunRequiredAsync(
                PsmuxLayoutPlanner.LayoutCommand(executable, normalizedName, sessions.Count),
                cancellationToken).ConfigureAwait(false);
            await ConfigureRoleBorderFormatAsync(normalizedName, panes, cancellationToken).ConfigureAwait(false);

            return new DeskStartResult(normalizedName, panes);
        }
        catch (Exception startException)
        {
            if (!sessionCreated)
            {
                throw;
            }

            CommandResult cleanup;
            try
            {
                cleanup = await _executor.RunAsync(
                    new CommandInvocation(executable, ["kill-session", "-t", normalizedName]),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    $"Desk startup failed and cleanup of '{normalizedName}' also failed.",
                    startException,
                    cleanupException);
            }

            if (!cleanup.IsSuccess)
            {
                throw new AggregateException(
                    $"Desk startup failed and cleanup of '{normalizedName}' exited with {cleanup.ExitCode}: "
                    + cleanup.StandardError.Trim(),
                    startException);
            }

            throw new InvalidOperationException(
                $"Desk startup failed; partial psmux session '{normalizedName}' was rolled back: "
                + startException.Message,
                startException);
        }
    }

    public async Task<IReadOnlyList<string>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _executor.RunAsync(
            new CommandInvocation(executable, ["list-sessions", "-F", "#{session_name}"]),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var error = $"{result.StandardError}\n{result.StandardOutput}";
            if (error.Contains("no server running", StringComparison.OrdinalIgnoreCase)
                || error.Contains("no sessions", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            throw new InvalidOperationException(
                $"Command failed ({result.ExitCode}): {executable} list-sessions{Environment.NewLine}"
                + result.StandardError.Trim());
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<PsmuxPaneSnapshot>> ListPanesAsync(
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        var result = await RunRequiredAsync(
            new CommandInvocation(
                executable,
                [
                    "list-panes",
                    "-t",
                    PsmuxLayoutPlanner.NormalizeSessionName(sessionName),
                    "-F",
                    "#{pane_id}\u001f#{pane_title}\u001f#{pane_current_command}\u001f#{pane_active}"
                    + "\u001f#{pane_pid}\u001f#{pane_dead}\u001f#{pane_dead_status}"
                    + "\u001f#{pane_ps_state}\u001f#{pane_ps_seen}",
                ]),
            cancellationToken).ConfigureAwait(false);

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParsePaneSnapshot)
            .ToArray();
    }

    public async Task FocusAsync(string paneId, CancellationToken cancellationToken = default)
    {
        await RunRequiredAsync(
            new CommandInvocation(executable, ["select-pane", "-t", paneId]),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyLayoutAsync(
        string sessionName,
        int paneCount,
        CancellationToken cancellationToken = default)
    {
        await RunRequiredAsync(
            PsmuxLayoutPlanner.LayoutCommand(
                executable,
                PsmuxLayoutPlanner.NormalizeSessionName(sessionName),
                paneCount),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigureDeskControlsAsync(
        string sessionName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = PsmuxLayoutPlanner.NormalizeSessionName(sessionName);
        var bindings = new (string[] Prefix, string[] Command)[]
        {
            (["A"], ["run-shell", "-b", PowerShellCommand($"flyt desk focus --name {normalizedName} --next")]),
            (["Z"], ["resize-pane", "-Z"]),
            (["n"], PopupAction(normalizedName, "add-pane")),
            (["N"], PopupAction(normalizedName, "add-pane")),
            (["X"], PopupAction(normalizedName, "remove-pane")),
            (["x"], PopupAction(normalizedName, "remove-pane")),
            (["S"], PopupAction(normalizedName, "send")),
            (["R"], PopupAction(normalizedName, "restart")),
            (["H"], PopupAction(normalizedName, "handoff")),
            (["Q"], PopupAction(normalizedName, "queue")),
            (["L"], PopupAction(normalizedName, "logs", keepOpen: true)),
            (["O"], PopupAction(normalizedName, "menu", keepOpen: true)),
        };

        foreach (var (prefix, command) in bindings)
        {
            await RunRequiredAsync(
                new CommandInvocation(executable, ["bind-key", .. prefix, .. command]),
                cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < PsmuxLayoutPlanner.MaximumSessions; index++)
        {
            var shortcut = index == 9 ? "0" : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await RunRequiredAsync(
                new CommandInvocation(
                    executable,
                    ["bind-key", "-n", $"M-{shortcut}", "select-pane", "-t", $"{normalizedName}:0.{index}"]),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ConfigureRoleBorderFormatAsync(
        string sessionName,
        IReadOnlyList<DeskPaneHandle> panes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panes);
        if (panes.Count is < 1 or > PsmuxLayoutPlanner.MaximumSessions)
        {
            throw new ArgumentOutOfRangeException(nameof(panes), "Flyt supports one to ten pane styles.");
        }

        // psmux 3.4's attached-client renderer only expands pane_index and
        // pane_title in pane-border-format. Keep this format deliberately
        // short and renderer-safe; role colour lives in each pane title.
        var format = " #{pane_title} ";
        await RunRequiredAsync(
            new CommandInvocation(
                executable,
                [
                    "set-option", "-t", PsmuxLayoutPlanner.NormalizeSessionName(sessionName),
                    "-g", "pane-border-format", format,
                ]),
            cancellationToken).ConfigureAwait(false);

        // Keep this last. psmux recalculates the pane content area when the
        // label row is enabled; reasserting it after the final format makes
        // configured titles visible even when a restored session or user
        // config previously disabled pane headers.
        await RunRequiredAsync(
            new CommandInvocation(
                executable,
                [
                    "set-option", "-t", PsmuxLayoutPlanner.NormalizeSessionName(sessionName),
                    "-g", "pane-border-status", "top",
                ]),
            cancellationToken).ConfigureAwait(false);
    }

    public string PaneTitle(AgentSessionSpec session, DeskPaneStatus status) =>
        PsmuxLayoutPlanner.PaneTitle(
            session,
            status,
            _presentation.UnicodeEnabled,
            _presentation.ColorEnabled);

    public async Task UpdateDeskChromeAsync(
        string sessionName,
        string workspaceLabel,
        string summary,
        DeskPaneStatus activeStatus,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = PsmuxLayoutPlanner.NormalizeSessionName(sessionName);
        var stateColor = !_presentation.ColorEnabled || _presentation.HighContrast
            ? "white"
            : activeStatus switch
            {
                DeskPaneStatus.NeedsInput => "yellow",
                DeskPaneStatus.Failed => "red",
                DeskPaneStatus.Done => "green",
                _ => "white",
            };
        var options = new[]
        {
            ("status-left", _presentation.ColorEnabled && !_presentation.HighContrast
                ? $"#[fg=cyan,bold] FLYT #[default] · {PresentationText(workspaceLabel)} "
                : $"#[bold] FLYT #[default] | {PresentationText(workspaceLabel)} "),
            ("status-right", $" {PresentationText(summary)} {(_presentation.UnicodeEnabled ? "·" : "|")} %H:%M "),
            ("pane-active-border-style", _presentation.ColorEnabled ? $"fg={stateColor},bold" : "bold"),
        };

        foreach (var (name, value) in options)
        {
            await RunRequiredAsync(
                new CommandInvocation(executable, ["set-option", "-t", normalizedName, "-g", name, value]),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendTextAsync(
        string paneId,
        string text,
        bool submit = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        await RunRequiredAsync(
            new CommandInvocation(executable, ["send-paste", "-t", paneId, payload]),
            cancellationToken).ConfigureAwait(false);
        if (submit)
        {
            await RunRequiredAsync(
                new CommandInvocation(executable, ["send-keys", "-t", paneId, "Enter"]),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetPanePresentationAsync(
        string paneId,
        AgentSessionSpec session,
        DeskPaneStatus status,
        CancellationToken cancellationToken = default)
    {
        await RunRequiredAsync(
            PsmuxLayoutPlanner.TitleCommand(
                executable,
                paneId,
                PaneTitle(session, status)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartPaneAsync(
        string paneId,
        AgentSessionSpec session,
        CancellationToken cancellationToken = default)
    {
        await RunRequiredAsync(
            new CommandInvocation(
                executable,
                ["respawn-pane", "-k", "-t", paneId, "-c", session.WorkingDirectory]),
            cancellationToken).ConfigureAwait(false);
        await ConfigurePaneAsync(paneId, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeskPaneHandle> AddPaneAsync(
        string sessionName,
        AgentSessionSpec session,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = PsmuxLayoutPlanner.NormalizeSessionName(sessionName);
        var result = await RunRequiredAsync(
            new CommandInvocation(
                executable,
                [
                    "split-window", "-d", "-P", "-F", "#{pane_id}",
                    "-t", $"{normalizedName}:0", "-c", session.WorkingDirectory,
                ]),
            cancellationToken).ConfigureAwait(false);
        var paneId = RequirePaneId(result.StandardOutput);
        await ConfigurePaneAsync(paneId, session, cancellationToken).ConfigureAwait(false);
        var paneCount = (await ListPanesAsync(normalizedName, cancellationToken).ConfigureAwait(false)).Count;
        await RunRequiredAsync(
            PsmuxLayoutPlanner.LayoutCommand(executable, normalizedName, paneCount),
            cancellationToken).ConfigureAwait(false);
        return new DeskPaneHandle(paneId, session);
    }

    public async Task RemovePaneAsync(
        string paneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paneId);
        await RunRequiredAsync(
            new CommandInvocation(executable, ["kill-pane", "-t", paneId]),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CapturePaneAsync(
        string paneId,
        int lines,
        CancellationToken cancellationToken = default)
    {
        if (lines is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(lines), "Lines must be between 1 and 10000.");
        }

        var result = await RunRequiredAsync(
            new CommandInvocation(executable, ["capture-pane", "-p", "-S", $"-{lines}", "-t", paneId]),
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    public Task<int> AttachAsync(string sessionName, CancellationToken cancellationToken = default) =>
        _executor.RunInteractiveAsync(
            new CommandInvocation(
                executable,
                ["attach-session", "-t", PsmuxLayoutPlanner.NormalizeSessionName(sessionName)]),
            cancellationToken);

    public async Task StopAsync(string sessionName, CancellationToken cancellationToken = default)
    {
        await RunRequiredAsync(
            new CommandInvocation(
                executable,
                ["kill-session", "-t", PsmuxLayoutPlanner.NormalizeSessionName(sessionName)]),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfigurePaneAsync(
        string paneId,
        AgentSessionSpec session,
        CancellationToken cancellationToken)
    {
        await RunRequiredAsync(
            PsmuxLayoutPlanner.TitleCommand(
                executable,
                paneId,
                PaneTitle(session, DeskPaneStatus.Starting)),
            cancellationToken).ConfigureAwait(false);
        await RunRequiredAsync(
            PsmuxLayoutPlanner.SendCommand(
                executable,
                paneId,
                session.LaunchCommand),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigureDeskPresentationAsync(
        string sessionName,
        CancellationToken cancellationToken)
    {
        var colorEnabled = _presentation.ColorEnabled;
        var highContrast = _presentation.HighContrast;
        var options = new[]
        {
            ("program-status", "on"),
            ("remain-on-exit", "failed"),
            ("pane-border-status", "top"),
            ("pane-border-format", " #{pane_title} "),
            ("pane-border-indicators", "both"),
            ("pane-border-lines", "heavy"),
            ("pane-border-style", colorEnabled ? "fg=brightblack" : "default"),
            ("pane-active-border-style", colorEnabled ? "fg=white,bold" : "bold"),
            ("window-style", highContrast ? "dim=0" : "dim=12"),
            ("window-active-style", "dim=0"),
            ("status", "1"),
            ("status-style", colorEnabled ? "bg=black,fg=white" : "default"),
            ("status-left-style", colorEnabled && !highContrast ? "fg=cyan,bold" : "bold"),
            ("status-right-style", colorEnabled && !highContrast ? "fg=brightblack" : "default"),
            ("popup-border-style", colorEnabled && !highContrast ? "fg=cyan,bold" : "bold"),
            ("popup-border-lines", "rounded"),
            ("status-interval", "1"),
        };

        foreach (var (name, value) in options)
        {
            await RunRequiredAsync(
                new CommandInvocation(executable, ["set-option", "-t", sessionName, "-g", name, value]),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string[] PopupAction(string sessionName, string action, bool keepOpen = false)
    {
        var keep = keepOpen ? "-K" : "-E";
        return
        [
            "display-popup", "-w", "82%", "-h", "72%", keep,
            PowerShellCommand($"flyt desk action --name {sessionName} --pane-id '#{{pane_id}}' --action {action}"),
        ];
    }

    private static string PowerShellCommand(string command) =>
        $"pwsh -NoProfile -Command \"{command.Replace("\"", "`\"", StringComparison.Ordinal)}\"";

    private static string SanitizeFormat(string value) => value
        .Replace("#", "", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private string PresentationText(string value)
    {
        var sanitized = SanitizeFormat(value);
        return _presentation.UnicodeEnabled
            ? sanitized
            : sanitized.Replace("·", "|", StringComparison.Ordinal);
    }

    private async Task<CommandResult> RunRequiredAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await _executor.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Command failed ({result.ExitCode}): {invocation}{Environment.NewLine}{result.StandardError.Trim()}");
        }

        return result;
    }

    private static string RequirePaneId(string output)
    {
        var paneId = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(value => value.StartsWith('%'));

        return paneId ?? throw new InvalidOperationException(
            $"psmux did not return a pane id. Output: {output.Trim()}");
    }

    private static PsmuxPaneSnapshot ParsePaneSnapshot(string value)
    {
        var parts = value.Split('\u001f');
        if (parts.Length != 9 || string.IsNullOrWhiteSpace(parts[0]))
        {
            throw new InvalidOperationException($"psmux returned an invalid pane record: {value}");
        }

        return new PsmuxPaneSnapshot(
            parts[0].Trim(),
            parts[1].Trim(),
            PsmuxLayoutPlanner.StableNameFromTitle(parts[1].Trim()),
            parts[2].Trim(),
            parts[3].Trim() == "1",
            TryParseInt(parts[4]),
            parts[5].Trim() == "1",
            TryParseInt(parts[6]),
            parts[7].Trim(),
            parts[8].Trim() == "1");
    }

    private static int? TryParseInt(string value) =>
        int.TryParse(value.Trim(), out var parsed) ? parsed : null;
}

public sealed record DeskPaneHandle(string PaneId, AgentSessionSpec Session);

public sealed record DeskStartResult(string SessionName, IReadOnlyList<DeskPaneHandle> Panes);

public sealed record PsmuxPaneSnapshot(
    string PaneId,
    string Title,
    string Name,
    string CurrentCommand,
    bool IsActive,
    int? ProcessId,
    bool IsDead,
    int? ExitCode,
    string ProgramState,
    bool IsSeen);

public static class PsmuxStatusMapper
{
    public static DeskPaneStatus Map(PsmuxPaneSnapshot pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        if (pane.IsDead)
        {
            return pane.ExitCode is null or 0 ? DeskPaneStatus.Exited : DeskPaneStatus.Failed;
        }

        return pane.ProgramState.ToLowerInvariant() switch
        {
            "working" => DeskPaneStatus.Working,
            "blocked" => DeskPaneStatus.NeedsInput,
            "done" when pane.IsSeen => DeskPaneStatus.Idle,
            "done" => DeskPaneStatus.Done,
            "error" => DeskPaneStatus.Failed,
            "idle" => DeskPaneStatus.Idle,
            _ => DeskPaneStatus.Running,
        };
    }
}
