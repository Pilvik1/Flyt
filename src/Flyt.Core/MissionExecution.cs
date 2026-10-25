namespace Flyt.Core;

public enum MissionAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed record MissionWorkspace(
    Guid MissionId,
    string RepositoryRoot,
    string WorktreePath,
    string BranchName,
    string BaseCommit,
    string HeadCommit,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MissionAttempt(
    Guid Id,
    Guid MissionId,
    int Number,
    AgentProvider Provider,
    MissionAttemptStatus Status,
    int PlanVersion,
    string PlanFingerprint,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record MissionVerificationReceipt(
    Guid Id,
    Guid MissionId,
    Guid AttemptId,
    string Command,
    int ExitCode,
    bool Passed,
    string BaseCommit,
    string HeadCommit,
    IReadOnlyList<string> ChangedPaths,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
