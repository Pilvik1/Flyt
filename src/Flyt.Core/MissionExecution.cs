namespace Flyt.Core;

public enum MissionAttemptStatus
{
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Interrupted,
}

public enum MissionReviewVerdict
{
    Pass,
    Block,
    Invalid,
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
    DateTimeOffset? CompletedAt,
    string? ProviderProtocol = null,
    string? ProviderVersion = null,
    string? ProviderSessionId = null,
    string? ProviderTurnId = null);

public sealed record MissionVerificationReceipt(
    Guid Id,
    Guid MissionId,
    Guid AttemptId,
    string Command,
    int ExitCode,
    bool Passed,
    string BaseCommit,
    string HeadCommit,
    string DiffFingerprint,
    IReadOnlyList<string> ChangedPaths,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record MissionReviewReceipt(
    Guid Id,
    Guid MissionId,
    Guid AttemptId,
    AgentProvider Provider,
    MissionReviewVerdict Verdict,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? ProviderProtocol = null,
    string? ProviderVersion = null,
    string? ProviderSessionId = null,
    string? ProviderTurnId = null);

public sealed record MissionProviderMetadata(
    string Protocol,
    string? Version,
    string? SessionId,
    string? TurnId);

public enum MissionPilotMode
{
    Baseline,
    Mission,
}

public sealed record MissionPilotReceipt(
    Guid Id,
    Guid? MissionId,
    string Scenario,
    MissionPilotMode Mode,
    bool Passed,
    bool EvidenceComplete,
    bool FailureInjected,
    int InterventionCount,
    IReadOnlyList<string> InterventionReasons,
    int AttemptCount,
    int ReviewBlockCount,
    int RecoveryCount,
    double DurationMinutes,
    string OperatingSystem,
    string ProviderMetadata,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    DateTimeOffset RecordedAt);

public sealed record MissionPilotReport(
    int BaselineCount,
    int MissionCount,
    int PairedScenarioCount,
    int BaselineInterventions,
    int MissionInterventions,
    double? InterventionReductionPercent,
    double? MedianMissionDurationMinutes,
    int PilotSpanDays,
    bool AllMissionsPassed,
    bool AllEvidenceComplete,
    bool WindowsOnly,
    bool RecoveryScenarioProved,
    bool InterventionTargetMet,
    bool Ready)
{
    public static MissionPilotReport Create(IReadOnlyList<MissionPilotReceipt> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var baselines = LatestByScenario(receipts, MissionPilotMode.Baseline);
        var missions = LatestByScenario(receipts, MissionPilotMode.Mission);
        var paired = missions.Keys.Intersect(baselines.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var baselineInterventions = baselines.Values.Sum(value => value.InterventionCount);
        var missionInterventions = missions.Values.Sum(value => value.InterventionCount);
        var pairedBaselineInterventions = paired.Sum(key => baselines[key].InterventionCount);
        var pairedMissionInterventions = paired.Sum(key => missions[key].InterventionCount);
        double? reduction = paired.Length == 0 || pairedBaselineInterventions == 0
            ? null
            : (pairedBaselineInterventions - pairedMissionInterventions) * 100d / pairedBaselineInterventions;
        var durations = missions.Values.Select(value => value.DurationMinutes).Order().ToArray();
        double? median = durations.Length == 0
            ? null
            : durations.Length % 2 == 1
                ? durations[durations.Length / 2]
                : (durations[(durations.Length / 2) - 1] + durations[durations.Length / 2]) / 2d;
        var spanDays = missions.Count < 2
            ? 0
            : (int)Math.Floor(
                (missions.Values.Max(value => value.CompletedAt)
                 - missions.Values.Min(value => value.CompletedAt)).TotalDays);
        var allPassed = missions.Count >= 5 && missions.Values.All(value => value.Passed);
        var allEvidence = missions.Count >= 5 && missions.Values.All(value => value.EvidenceComplete);
        var windowsOnly = paired.Length >= 5
                          && paired.All(key =>
                              IsWindows(baselines[key]) && IsWindows(missions[key]));
        var recoveryProved = missions.Values.Any(value => value.FailureInjected && value.RecoveryCount > 0);
        var interventionTarget = reduction is >= 50d;
        var ready = missions.Count >= 5
                    && paired.Length >= 5
                    && spanDays >= 14
                    && allPassed
                    && allEvidence
                    && windowsOnly
                    && recoveryProved
                    && interventionTarget;

        return new MissionPilotReport(
            baselines.Count,
            missions.Count,
            paired.Length,
            baselineInterventions,
            missionInterventions,
            reduction,
            median,
            spanDays,
            allPassed,
            allEvidence,
            windowsOnly,
            recoveryProved,
            interventionTarget,
            ready);
    }

    private static Dictionary<string, MissionPilotReceipt> LatestByScenario(
        IReadOnlyList<MissionPilotReceipt> receipts,
        MissionPilotMode mode) => receipts
        .Where(value => value.Mode == mode)
        .GroupBy(value => value.Scenario, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.MaxBy(value => value.RecordedAt)!,
            StringComparer.OrdinalIgnoreCase);

    private static bool IsWindows(MissionPilotReceipt receipt) =>
        receipt.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase);
}
