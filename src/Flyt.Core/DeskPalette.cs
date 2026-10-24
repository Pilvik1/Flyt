namespace Flyt.Core;

public static class DeskPalette
{
    public static string NormalizeRole(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }

    public static string ForRole(string role) => NormalizeRole(role) switch
    {
        "planner" => "cyan",
        "builder" => "blue",
        "researcher" => "purple",
        "reviewer" => "magenta",
        "verifier" => "green",
        "ops" or "operator" => "yellow",
        "shell" => "brightblack",
        _ => "white",
    };

    public static string ForState(DeskPaneStatus status) => status switch
    {
        DeskPaneStatus.Starting => "cyan",
        DeskPaneStatus.Running or DeskPaneStatus.Working => "blue",
        DeskPaneStatus.NeedsInput => "yellow",
        DeskPaneStatus.Done => "green",
        DeskPaneStatus.Failed => "red",
        DeskPaneStatus.Idle or DeskPaneStatus.Paused => "brightblack",
        DeskPaneStatus.Exited or DeskPaneStatus.Missing or DeskPaneStatus.Stopped => "brightblack",
        _ => "white",
    };

    public static string Glyph(DeskPaneStatus status) => status switch
    {
        DeskPaneStatus.Starting => "⚪",
        DeskPaneStatus.Running or DeskPaneStatus.Working => "🔵",
        DeskPaneStatus.NeedsInput => "🟡",
        DeskPaneStatus.Done => "🟢",
        DeskPaneStatus.Failed => "🔴",
        DeskPaneStatus.Paused => "⚪",
        DeskPaneStatus.Idle => "⚫",
        DeskPaneStatus.Exited or DeskPaneStatus.Missing or DeskPaneStatus.Stopped => "⚫",
        _ => "❔",
    };

    public static string NormalizeColor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '#' or '-')))
        {
            throw new ArgumentException($"Invalid terminal color '{value}'.", nameof(value));
        }

        return normalized;
    }
}
