using Flyt.Core;

namespace Flyt.Tests;

public sealed class MissionTests
{
    [Fact]
    public void Create_UsesBriefingAndBalancedDefaults()
    {
        var mission = Mission.Create("Build the thing", Environment.CurrentDirectory);

        Assert.Equal(MissionState.Briefing, mission.State);
        Assert.Equal("Balanced", mission.Profile);
        Assert.Equal("Build the thing", mission.Goal);
        Assert.True(Path.IsPathFullyQualified(mission.WorkspacePath));
    }

    [Fact]
    public void TransitionTo_AllowsEvidenceBearingHappyPath()
    {
        var mission = Mission.Create("Build", Environment.CurrentDirectory)
            .TransitionTo(MissionState.PlanApproval)
            .TransitionTo(MissionState.Planning)
            .TransitionTo(MissionState.Executing)
            .TransitionTo(MissionState.Reviewing)
            .TransitionTo(MissionState.Verifying)
            .TransitionTo(MissionState.Integrating)
            .TransitionTo(MissionState.Completed);

        Assert.Equal(MissionState.Completed, mission.State);
    }

    [Fact]
    public void TransitionTo_RejectsSkippingPlanApproval()
    {
        var mission = Mission.Create("Build", Environment.CurrentDirectory);

        var exception = Assert.Throws<InvalidOperationException>(
            () => mission.TransitionTo(MissionState.Executing));

        Assert.Contains("Briefing to Executing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedMission_IsTerminal()
    {
        var mission = Mission.Create("Build", Environment.CurrentDirectory)
            .TransitionTo(MissionState.PlanApproval)
            .TransitionTo(MissionState.Planning)
            .TransitionTo(MissionState.Executing)
            .TransitionTo(MissionState.Verifying)
            .TransitionTo(MissionState.Integrating)
            .TransitionTo(MissionState.Completed);

        Assert.False(mission.CanTransitionTo(MissionState.Executing));
    }
}

