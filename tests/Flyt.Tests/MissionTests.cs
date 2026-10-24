using Flyt.Core;

namespace Flyt.Tests;

public sealed class MissionTests
{
    [Fact]
    public void Create_UsesBriefingAndBalancedDefaults()
    {
        var mission = Mission.Create("Build the thing", Environment.CurrentDirectory);

        Assert.Equal(MissionState.Briefing, mission.State);
        Assert.Equal("Bounded", mission.Profile);
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

    [Fact]
    public void Pause_ResumesOnlyToRecordedPriorState()
    {
        var paused = Mission.Create("Build", Environment.CurrentDirectory)
            .TransitionTo(MissionState.PlanApproval)
            .TransitionTo(MissionState.Planning)
            .TransitionTo(MissionState.Executing)
            .TransitionTo(MissionState.Paused);

        Assert.Equal(MissionState.Executing, paused.ResumeState);
        Assert.True(paused.CanTransitionTo(MissionState.Executing));
        Assert.False(paused.CanTransitionTo(MissionState.Verifying));

        var resumed = paused.TransitionTo(MissionState.Executing);
        Assert.Null(resumed.ResumeState);
    }
}
