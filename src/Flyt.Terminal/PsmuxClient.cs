using Flyt.Core;

namespace Flyt.Terminal;

public sealed class PsmuxClient(
    ICommandExecutor executor,
    string executable = "psmux")
{
    private readonly ICommandExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));

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
            }

            await RunRequiredAsync(
                new CommandInvocation(
                    executable,
                    ["select-layout", "-t", $"{normalizedName}:0", "tiled"]),
                cancellationToken).ConfigureAwait(false);

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
                $"Desk startup failed; partial psmux session '{normalizedName}' was rolled back.",
                startException);
        }
    }

    public async Task<IReadOnlyList<string>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunRequiredAsync(
            new CommandInvocation(executable, ["list-sessions", "-F", "#{session_name}"]),
            cancellationToken).ConfigureAwait(false);

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
                PsmuxLayoutPlanner.PaneTitle(session, status)),
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
        await RunRequiredAsync(
            new CommandInvocation(executable, ["select-layout", "-t", $"{normalizedName}:0", "tiled"]),
            cancellationToken).ConfigureAwait(false);
        return new DeskPaneHandle(paneId, session);
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
                PsmuxLayoutPlanner.PaneTitle(session, DeskPaneStatus.Starting)),
            cancellationToken).ConfigureAwait(false);
        await RunRequiredAsync(
            PsmuxLayoutPlanner.SendCommand(
                executable,
                paneId,
                session.LaunchCommand),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfigureDeskPresentationAsync(
        string sessionName,
        CancellationToken cancellationToken)
    {
        var options = new[]
        {
            ("program-status", "on"),
            ("remain-on-exit", "failed"),
            ("pane-border-status", "top"),
            ("pane-border-format", " #{pane_index} #{pane_title} "),
            ("pane-border-indicators", "both"),
            ("status-interval", "1"),
        };

        foreach (var (name, value) in options)
        {
            await RunRequiredAsync(
                new CommandInvocation(executable, ["set-option", "-t", sessionName, "-g", name, value]),
                cancellationToken).ConfigureAwait(false);
        }
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
