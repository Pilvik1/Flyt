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

    public async Task<string> StartDeskAsync(
        string sessionName,
        IReadOnlyList<AgentSessionSpec> sessions,
        CancellationToken cancellationToken = default)
    {
        PsmuxLayoutPlanner.Validate(sessionName, sessions);
        var normalizedName = PsmuxLayoutPlanner.NormalizeSessionName(sessionName);

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
                    "-c",
                    sessions[0].WorkingDirectory,
                ]),
            cancellationToken).ConfigureAwait(false);
        var firstPane = RequirePaneId(firstResult.StandardOutput);
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
            await ConfigurePaneAsync(paneId, sessions[index], cancellationToken).ConfigureAwait(false);
        }

        await RunRequiredAsync(
            new CommandInvocation(
                executable,
                ["select-layout", "-t", $"{normalizedName}:0", "tiled"]),
            cancellationToken).ConfigureAwait(false);

        return normalizedName;
    }

    public async Task<IReadOnlyList<string>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _executor.RunAsync(
            new CommandInvocation(executable, ["list-sessions", "-F", "#{session_name}"]),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return [];
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            PsmuxLayoutPlanner.TitleCommand(executable, paneId, session.Name),
            cancellationToken).ConfigureAwait(false);
        await RunRequiredAsync(
            PsmuxLayoutPlanner.SendCommand(
                executable,
                paneId,
                PsmuxLayoutPlanner.ProviderCommand(session.Provider)),
            cancellationToken).ConfigureAwait(false);
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
}

