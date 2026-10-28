using Flyt.Core;

namespace Flyt.Terminal;

public static class PsmuxLayoutPlanner
{
    public const int MaximumSessions = 10;
    // psmux 3.4's attached-client renderer suppresses (rather than truncates)
    // border labels wider than their pane. Keep titles inside Flyt's smallest
    // supported tiled pane, including the border-format padding.
    public const int MaximumPaneTitleLength = 48;

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
            commands.Add(LayoutCommand(executable, normalizedName, sessions.Count));
        }

        commands.Add(LayoutCommand(executable, normalizedName, sessions.Count));
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

    public static string PaneTitle(
        AgentSessionSpec session,
        DeskPaneStatus status,
        bool unicodeEnabled = true,
        bool colorEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        var repository = Compact(new DirectoryInfo(session.WorkingDirectory).Name, 12);
        var separator = unicodeEnabled ? " · " : " | ";
        var roleMarker = RoleColorGlyph(session.Color, unicodeEnabled, colorEnabled);
        var role = string.IsNullOrEmpty(roleMarker)
            ? session.Role.ToUpperInvariant()
            : $"{roleMarker} {session.Role.ToUpperInvariant()}";
        var prefix = $"{session.Name}{separator}{role}{separator}{StateGlyph(status, unicodeEnabled)} {CompactStateLabel(status)}";
        var suffix = $"{separator}{repository}";
        var available = MaximumPaneTitleLength - prefix.Length - suffix.Length - separator.Length;
        var title = available >= 4
            ? $"{prefix}{separator}{Compact(session.Task, available)}{suffix}"
            : $"{prefix}{suffix}";
        return Compact(title, MaximumPaneTitleLength);
    }

    public static string StableNameFromTitle(string title) =>
        title.Split(['·', '|'], 2, StringSplitOptions.TrimEntries)[0];

    public static string LayoutNameForCount(int paneCount) => paneCount switch
    {
        1 => "tiled",
        2 => "even-horizontal",
        3 => "main-vertical",
        4 => "tiled",
        >= 5 and <= MaximumSessions => "tiled",
        _ => throw new ArgumentOutOfRangeException(nameof(paneCount), "Flyt supports one to ten panes."),
    };

    internal static CommandInvocation LayoutCommand(string executable, string sessionName, int paneCount) =>
        new(executable, ["select-layout", "-t", $"{sessionName}:0", LayoutNameForCount(paneCount)]);

    private static string Compact(string value, int maximumLength) => value.Length <= maximumLength
        ? value
        : $"{value[..Math.Max(1, maximumLength - 1)].TrimEnd()}…";

    private static string RoleColorGlyph(string color, bool unicodeEnabled, bool colorEnabled)
    {
        if (!unicodeEnabled || !colorEnabled)
        {
            return string.Empty;
        }

        return DeskPalette.NormalizeColor(color) switch
        {
            "black" or "brightblack" => "⚫",
            "red" => "🔴",
            "orange" => "🟠",
            "yellow" => "🟡",
            "green" => "🟢",
            "cyan" => "🔷",
            "blue" => "🔵",
            "magenta" or "purple" => "🟣",
            "white" => "⚪",
            _ => "◆",
        };
    }

    private static string StateGlyph(DeskPaneStatus status, bool unicodeEnabled)
    {
        if (!unicodeEnabled)
        {
            return status switch
            {
                DeskPaneStatus.Working or DeskPaneStatus.Running => "*",
                DeskPaneStatus.NeedsInput => "!",
                DeskPaneStatus.Done => "+",
                DeskPaneStatus.Failed => "X",
                DeskPaneStatus.Starting => "~",
                _ => "-",
            };
        }

        return status switch
        {
            DeskPaneStatus.Working or DeskPaneStatus.Running => "●",
            DeskPaneStatus.NeedsInput => "!",
            DeskPaneStatus.Done => "✓",
            DeskPaneStatus.Failed => "×",
            DeskPaneStatus.Starting => "◌",
            _ => "○",
        };
    }

    private static string StateLabel(DeskPaneStatus status) => status == DeskPaneStatus.NeedsInput
        ? "NEEDS INPUT"
        : status.ToString().ToUpperInvariant();

    private static string CompactStateLabel(DeskPaneStatus status) => status switch
    {
        DeskPaneStatus.NeedsInput => "INPUT",
        DeskPaneStatus.Working => "WORK",
        DeskPaneStatus.Running => "RUN",
        DeskPaneStatus.Starting => "START",
        DeskPaneStatus.Paused => "PAUSE",
        _ => StateLabel(status),
    };

    internal static CommandInvocation SendCommand(string executable, string paneId, string command) =>
        new(executable, ["send-keys", "-t", paneId, "--", command, "Enter"]);
}
