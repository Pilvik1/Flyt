namespace Flyt.Core;

public enum ProtectedAction
{
    ReadOrChangeSecrets,
    DeleteMaterialFiles,
    WriteOutsideWorkspace,
    InstallSystemSoftware,
    ProductionDeployment,
    UsePaidExternalService,
    WeakenSecurityControl,
    PushBranch,
    CreatePullRequest,
    MergePullRequest,
}

public sealed record AutonomyPolicy(
    string Name,
    int MaxConcurrentAgents,
    int MaxFixLoops,
    TimeSpan MissionTimeout,
    int MaxConsecutiveProviderFailures,
    IReadOnlySet<ProtectedAction> ApprovalRequired)
{
    public static AutonomyPolicy Balanced { get; } = new(
        "Balanced",
        MaxConcurrentAgents: 3,
        MaxFixLoops: 2,
        MissionTimeout: TimeSpan.FromMinutes(90),
        MaxConsecutiveProviderFailures: 3,
        ApprovalRequired: new HashSet<ProtectedAction>
        {
            ProtectedAction.ReadOrChangeSecrets,
            ProtectedAction.DeleteMaterialFiles,
            ProtectedAction.WriteOutsideWorkspace,
            ProtectedAction.InstallSystemSoftware,
            ProtectedAction.ProductionDeployment,
            ProtectedAction.UsePaidExternalService,
            ProtectedAction.WeakenSecurityControl,
        });

    public bool RequiresApproval(ProtectedAction action) => ApprovalRequired.Contains(action);
}

