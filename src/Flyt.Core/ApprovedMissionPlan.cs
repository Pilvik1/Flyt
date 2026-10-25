namespace Flyt.Core;

public sealed record MissionApproval(
    long Sequence,
    Guid MissionId,
    int PlanVersion,
    string PlanFingerprint,
    DateTimeOffset DecidedAt);

public sealed record ApprovedMissionPlan(
    Mission Mission,
    MissionPlan Plan,
    MissionApproval Approval);
