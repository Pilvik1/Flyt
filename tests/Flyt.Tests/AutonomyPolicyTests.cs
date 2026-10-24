using Flyt.Core;

namespace Flyt.Tests;

public sealed class AutonomyPolicyTests
{
    [Theory]
    [InlineData(ProtectedAction.ReadOrChangeSecrets)]
    [InlineData(ProtectedAction.DeleteMaterialFiles)]
    [InlineData(ProtectedAction.WriteOutsideWorkspace)]
    [InlineData(ProtectedAction.InstallSystemSoftware)]
    [InlineData(ProtectedAction.ProductionDeployment)]
    [InlineData(ProtectedAction.UsePaidExternalService)]
    [InlineData(ProtectedAction.WeakenSecurityControl)]
    public void Balanced_RequiresApprovalForSensitiveActions(ProtectedAction action)
    {
        Assert.True(AutonomyPolicy.Balanced.RequiresApproval(action));
    }

    [Theory]
    [InlineData(ProtectedAction.PushBranch)]
    [InlineData(ProtectedAction.CreatePullRequest)]
    [InlineData(ProtectedAction.MergePullRequest)]
    public void Balanced_AllowsConfiguredRemoteGitActions(ProtectedAction action)
    {
        Assert.False(AutonomyPolicy.Balanced.RequiresApproval(action));
    }

    [Fact]
    public void Balanced_HasAgreedLimits()
    {
        var policy = AutonomyPolicy.Balanced;

        Assert.Equal(3, policy.MaxConcurrentAgents);
        Assert.Equal(2, policy.MaxFixLoops);
        Assert.Equal(TimeSpan.FromMinutes(90), policy.MissionTimeout);
        Assert.Equal(3, policy.MaxConsecutiveProviderFailures);
    }
}

