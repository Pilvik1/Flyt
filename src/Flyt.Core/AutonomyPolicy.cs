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
    public static AutonomyPolicy Manual { get; } = new(
        "Manual",
        MaxConcurrentAgents: 1,
        MaxFixLoops: 0,
        MissionTimeout: TimeSpan.FromMinutes(60),
        MaxConsecutiveProviderFailures: 1,
        ApprovalRequired: Enum.GetValues<ProtectedAction>().ToHashSet());

    public static AutonomyPolicy Bounded { get; } = new(
        "Bounded",
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
            ProtectedAction.MergePullRequest,
        });

    public static AutonomyPolicy Auto { get; } = new(
        "Auto",
        MaxConcurrentAgents: 4,
        MaxFixLoops: 3,
        MissionTimeout: TimeSpan.FromMinutes(120),
        MaxConsecutiveProviderFailures: 3,
        ApprovalRequired: new HashSet<ProtectedAction>
        {
            ProtectedAction.ReadOrChangeSecrets,
            ProtectedAction.DeleteMaterialFiles,
            ProtectedAction.WriteOutsideWorkspace,
            ProtectedAction.ProductionDeployment,
            ProtectedAction.UsePaidExternalService,
            ProtectedAction.WeakenSecurityControl,
            ProtectedAction.MergePullRequest,
        });

    public static AutonomyPolicy Balanced => Bounded;

    public static AutonomyPolicy Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "manual" => Manual,
        "bounded" or "balanced" => Bounded,
        "auto" => Auto,
        _ => throw new ArgumentException(
            $"Unknown autonomy profile '{value}'. Expected manual, bounded, or auto.",
            nameof(value)),
    };

    public bool RequiresApproval(ProtectedAction action) => ApprovalRequired.Contains(action);
}
