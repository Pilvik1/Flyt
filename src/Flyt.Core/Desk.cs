namespace Flyt.Core;

public enum DeskStatus
{
    Running,
    Missing,
    Stopped,
}

public enum DeskPaneStatus
{
    Starting,
    Running,
    Working,
    NeedsInput,
    Idle,
    Done,
    Failed,
    Paused,
    Exited,
    Unknown,
    Missing,
    Stopped,
}

public enum DeskStateSource
{
    Registered,
    Provider,
    ProgramStatus,
    Process,
    Operator,
}

public sealed record Desk(
    Guid Id,
    string SessionName,
    string WorkspacePath,
    string? TemplateName,
    IReadOnlyList<string> Tags,
    int MaxConcurrentExpensiveTasks,
    int MemoryWarningMegabytes,
    DeskStatus Status,
    IReadOnlyList<DeskPane> Panes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastReconciledAt,
    string LayoutName = "tiled",
    string? FocusedPaneName = null);

public sealed record DeskPane(
    Guid Id,
    Guid DeskId,
    string PaneId,
    AgentProvider Provider,
    string Name,
    string WorkingDirectory,
    string LaunchCommand,
    string Role,
    string Task,
    string Color,
    IReadOnlyList<string> Tags,
    string? ProviderSessionId,
    DeskPaneStatus Status,
    DeskStateSource StatusSource,
    int? ProcessId,
    int? ExitCode,
    DateTimeOffset StateChangedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DeskFailureReceipt(
    Guid Id,
    Guid DeskId,
    Guid PaneId,
    string PaneName,
    AgentProvider Provider,
    string WorkingDirectory,
    string LaunchCommand,
    int? ExitCode,
    string Reason,
    string RecentOutput,
    long? WorkingSetBytes,
    DateTimeOffset OccurredAt);

public sealed record DeskEvent(
    long Sequence,
    Guid DeskId,
    string DeskName,
    string EventType,
    string? PaneName,
    string Detail,
    DateTimeOffset OccurredAt);

public enum DeskWorkItemStatus
{
    Queued,
    Dispatched,
    Completed,
    Failed,
    Cancelled,
}

public sealed record DeskWorkItem(
    Guid Id,
    Guid DeskId,
    string DeskName,
    string PaneName,
    string Payload,
    bool Expensive,
    DeskWorkItemStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome);

public sealed record DeskPaneRuntime(
    string Name,
    string PaneId,
    DeskPaneStatus Status,
    DeskStateSource Source,
    int? ProcessId,
    int? ExitCode,
    bool IsActive = false);
