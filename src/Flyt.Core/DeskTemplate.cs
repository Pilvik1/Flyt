namespace Flyt.Core;

public sealed record DeskTemplate(
    string Name,
    IReadOnlyList<DeskTemplatePane> Panes,
    IReadOnlyList<string>? Tags = null,
    int MaxConcurrentExpensiveTasks = 2,
    int MemoryWarningMegabytes = 12_000)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Panes.Count is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(Panes), "A Desk template requires one to ten panes.");
        }

        var duplicate = Panes.GroupBy(pane => pane.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Template pane '{duplicate.Key}' is duplicated.", nameof(Panes));
        }

        if (MaxConcurrentExpensiveTasks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentExpensiveTasks),
                "At least one expensive task must be allowed.");
        }

        if (MemoryWarningMegabytes < 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryWarningMegabytes),
                "The Desk memory warning must be at least 256 MB.");
        }

        foreach (var pane in Panes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pane.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(pane.Role);
            ArgumentException.ThrowIfNullOrWhiteSpace(pane.Task);
            if (pane.Color is not null)
            {
                _ = DeskPalette.NormalizeColor(pane.Color);
            }
        }
    }
}

public sealed record DeskTemplatePane(
    AgentProvider Provider,
    string Name,
    string Role,
    string Task,
    string? WorkingDirectory = null,
    string? Command = null,
    string? Color = null,
    IReadOnlyList<string>? Tags = null,
    string? ProviderSessionId = null);

public sealed record DeskHandoff(
    Guid Id,
    string DeskName,
    string FromPane,
    string ToPane,
    string Goal,
    string WorkspacePath,
    string? Branch,
    string? DiffPath,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Verification,
    IReadOnlyList<string> Blockers,
    string RequestedAction,
    DateTimeOffset CreatedAt);
