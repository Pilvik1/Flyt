using Flyt.Core;

namespace Flyt.Terminal;

public static class PsmuxLayoutPlanner
{
    public const int MaximumSessions = 6;

    public static IReadOnlyList<CommandInvocation> BuildPreview(
        string executable,
        string sessionName,
        IReadOnlyList<AgentSessionSpec> sessions)
    {
        Validate(sessionName, sessions);
        var normalizedName = NormalizeSessionName(sessionName);
        var commands = new List<CommandInvocation>();

        commands.Add(new CommandInvocation(
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
            ]));
        commands.Add(TitleCommand(executable, "$pane0", PaneTitle(sessions[0], DeskPaneStatus.Starting)));
        commands.Add(SendCommand(executable, "$pane0", sessions[0].LaunchCommand));

        for (var index = 1; index < sessions.Count; index++)
        {
            commands.Add(new CommandInvocation(
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
                ]));
            commands.Add(TitleCommand(
                executable,
                $"$pane{index}",
                PaneTitle(sessions[index], DeskPaneStatus.Starting)));
            commands.Add(SendCommand(executable, $"$pane{index}", sessions[index].LaunchCommand));
            commands.Add(new CommandInvocation(
                executable,
                ["select-layout", "-t", $"{normalizedName}:0", "tiled"]));
        }

        commands.Add(new CommandInvocation(
            executable,
            ["select-layout", "-t", $"{normalizedName}:0", "tiled"]));
        return commands;
    }

    public static string NormalizeSessionName(string sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        var normalized = new string(
            sessionName.Trim()
                .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-')
                .ToArray());

        return normalized.Length > 48 ? normalized[..48] : normalized;
    }

    internal static void Validate(string sessionName, IReadOnlyList<AgentSessionSpec> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentNullException.ThrowIfNull(sessions);

        if (sessions.Count is < 1 or > MaximumSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessions),
                $"Desk mode requires between 1 and {MaximumSessions} sessions.");
        }

        var duplicate = sessions
            .GroupBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Session name '{duplicate.Key}' is duplicated.", nameof(sessions));
        }
    }

    internal static CommandInvocation TitleCommand(string executable, string paneId, string title) =>
        new(executable, ["select-pane", "-t", paneId, "-T", title]);

    public static string PaneTitle(AgentSessionSpec session, DeskPaneStatus status) =>
        $"{session.Name} | {DeskPalette.Glyph(status)} {StateLabel(status)} | {session.Role} | {session.Task}";

    public static string StableNameFromTitle(string title) =>
        title.Split('|', 2, StringSplitOptions.TrimEntries)[0];

    private static string StateLabel(DeskPaneStatus status) => status == DeskPaneStatus.NeedsInput
        ? "NEEDS INPUT"
        : status.ToString().ToUpperInvariant();

    internal static CommandInvocation SendCommand(string executable, string paneId, string command) =>
        new(executable, ["send-keys", "-t", paneId, "--", command, "Enter"]);
}
