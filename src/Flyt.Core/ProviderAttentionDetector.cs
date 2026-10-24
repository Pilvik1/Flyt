namespace Flyt.Core;

public static class ProviderAttentionDetector
{
    private static readonly string[] NeedsInputPatterns =
    [
        "do you want to proceed",
        "would you like to continue",
        "press enter to continue",
        "press enter to confirm",
        "requires approval",
        "permission required",
        "allow this command",
        "approve this action",
        "[y/n]",
        "(y/n)",
    ];

    private static readonly string[] WorkingPatterns =
    [
        "esc to interrupt",
        "ctrl+c to interrupt",
        "working…",
        "thinking…",
    ];

    public static DeskPaneStatus? Detect(AgentProvider provider, string recentOutput)
    {
        if (provider == AgentProvider.Shell || string.IsNullOrWhiteSpace(recentOutput))
        {
            return null;
        }

        // Only inspect the current prompt area. Searching the full scrollback makes an
        // old approval prompt permanently sticky after the operator has answered it.
        var tail = string.Join(
            '\n',
            recentOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(6));
        if (tail.Length > 1_500)
        {
            tail = tail[^1_500..];
        }
        if (NeedsInputPatterns.Any(pattern => tail.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return DeskPaneStatus.NeedsInput;
        }

        if (WorkingPatterns.Any(pattern => tail.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return DeskPaneStatus.Working;
        }

        return null;
    }
}
