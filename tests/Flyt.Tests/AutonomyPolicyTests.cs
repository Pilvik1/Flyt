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
    public void Bounded_RequiresApprovalForSensitiveActions(ProtectedAction action)
    {
        Assert.True(AutonomyPolicy.Bounded.RequiresApproval(action));
    }

    [Theory]
    [InlineData(ProtectedAction.PushBranch)]
    [InlineData(ProtectedAction.CreatePullRequest)]
    public void Bounded_AllowsConfiguredRemoteGitActions(ProtectedAction action)
    {
        Assert.False(AutonomyPolicy.Bounded.RequiresApproval(action));
    }

    [Fact]
    public void Profiles_HaveIncreasingAutonomyAndBoundedLimits()
    {
        var policy = AutonomyPolicy.Bounded;

        Assert.Equal("Bounded", policy.Name);
        Assert.Equal(3, policy.MaxConcurrentAgents);
        Assert.Equal(2, policy.MaxFixLoops);
        Assert.Equal(TimeSpan.FromMinutes(90), policy.MissionTimeout);
        Assert.Equal(3, policy.MaxConsecutiveProviderFailures);
        Assert.True(AutonomyPolicy.Manual.RequiresApproval(ProtectedAction.PushBranch));
        Assert.False(AutonomyPolicy.Auto.RequiresApproval(ProtectedAction.PushBranch));
        Assert.True(AutonomyPolicy.Auto.RequiresApproval(ProtectedAction.MergePullRequest));
    }
}
