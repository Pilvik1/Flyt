namespace Flyt.Core;

public enum DeskStatus
{
    Running,
    Missing,
    Stopped,
}

public enum DeskPaneStatus
{
    Running,
    Missing,
    Stopped,
}

public sealed record Desk(
    Guid Id,
    string SessionName,
    string WorkspacePath,
    DeskStatus Status,
    IReadOnlyList<DeskPane> Panes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastReconciledAt);

public sealed record DeskPane(
    Guid Id,
    Guid DeskId,
    string PaneId,
    AgentProvider Provider,
    string Name,
    string WorkingDirectory,
    string LaunchCommand,
    DeskPaneStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
