using Flyt.Cli;
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

    [Fact]
    public void PlanFingerprint_IsStableAndContentBound()
    {
        var missionId = Guid.NewGuid();
        var first = MissionPlan.Create(
            missionId,
            ["Tests pass", "Receipt exists"],
            ["Build", "Verify"],
            "dotnet test");
        var equivalent = MissionPlan.Create(
            missionId,
            ["Tests pass", "Receipt exists"],
            ["Build", "Verify"],
            "dotnet test");
        var changed = MissionPlan.Create(
            missionId,
            ["Tests pass", "Receipt exists"],
            ["Build", "Verify"],
            "dotnet test --no-restore");

        Assert.Equal(first.ComputeFingerprint(), equivalent.ComputeFingerprint());
        Assert.NotEqual(first.ComputeFingerprint(), changed.ComputeFingerprint());
        Assert.Matches("^[0-9a-f]{64}$", first.ComputeFingerprint());
    }

    [Fact]
    public void Configuration_ValidatesNameProvidersAndTimeout()
    {
        var missionId = Guid.NewGuid();
        var configuration = MissionConfiguration.Create(
            missionId,
            "thesis-review",
            AgentProvider.Codex,
            AgentProvider.Claude,
            120);

        Assert.Equal("thesis-review", configuration.Name);
        Assert.Equal(AgentProvider.Codex, configuration.BuilderProvider);
        Assert.Equal(AgentProvider.Claude, configuration.ReviewerProvider);
        Assert.Equal(120, configuration.TimeoutMinutes);
        Assert.Throws<ArgumentException>(() => MissionConfiguration.Create(missionId, "not valid"));
        Assert.Throws<ArgumentException>(() => MissionConfiguration.Create(
            missionId,
            "invalid-provider",
            AgentProvider.Shell));
        Assert.Throws<ArgumentOutOfRangeException>(() => MissionConfiguration.Create(
            missionId,
            "invalid-timeout",
            timeoutMinutes: 0));
    }

    [Fact]
    public void GuidedConfiguration_ParsesProvidersAndSafeNames()
    {
        Assert.Equal(AgentProvider.Claude, Program.ParseMissionProvider("claude", "builder"));
        Assert.Equal(AgentProvider.Codex, Program.ParseMissionReviewer("codex"));
        Assert.Null(Program.ParseMissionReviewer("auto"));
        Assert.True(Program.IsValidMissionName("thesis-review.v2"));
        Assert.False(Program.IsValidMissionName("thesis review"));
        Assert.False(Program.IsValidMissionName(Guid.NewGuid().ToString("D")));
    }
}
