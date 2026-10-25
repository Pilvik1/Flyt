using Flyt.Core;
using Flyt.Providers;

namespace Flyt.Tests;

public sealed class MissionProviderCommandBuilderTests
{
    [Theory]
    [InlineData(AgentProvider.Claude, "claude", "acceptEdits")]
    [InlineData(AgentProvider.Codex, "codex", "workspace-write")]
    public void Build_WriteCommand_IsBoundedAndUsesArgumentList(
        AgentProvider provider,
        string executable,
        string writeMode)
    {
        var request = CreateRequest(readOnly: false);

        var command = MissionProviderCommandBuilder.Build(provider, request);

        Assert.Equal(executable, command.FileName);
        Assert.Equal(request.Workspace.WorktreePath, command.WorkingDirectory);
        Assert.Contains(writeMode, command.Arguments);
        Assert.Contains(command.Arguments, argument => argument.Contains(request.Mission.Goal, StringComparison.Ordinal));
        Assert.DoesNotContain("--dangerously-skip-permissions", command.Arguments);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", command.Arguments);
    }

    [Theory]
    [InlineData(AgentProvider.Claude, "plan")]
    [InlineData(AgentProvider.Codex, "read-only")]
    public void Build_ReadOnlyCommand_DisablesWrites(AgentProvider provider, string readOnlyMode)
    {
        var command = MissionProviderCommandBuilder.Build(provider, CreateRequest(readOnly: true));

        Assert.Contains(readOnlyMode, command.Arguments);
        Assert.Contains(command.Arguments, argument => argument.Contains("Do not edit files", StringComparison.Ordinal));
    }

    private static MissionProviderRequest CreateRequest(bool readOnly)
    {
        var now = DateTimeOffset.UtcNow;
        var mission = Mission.Create("Implement the queue", Environment.CurrentDirectory);
        var plan = MissionPlan.Create(
            mission.Id,
            ["Tests pass", "Receipt exists"],
            ["Implement", "Verify"],
            "dotnet test");
        var workspace = new MissionWorkspace(
            mission.Id,
            Environment.CurrentDirectory,
            Environment.CurrentDirectory,
            "flyt/mission-test/builder",
            "base",
            "head",
            now,
            now);
        return new MissionProviderRequest(mission, plan, workspace, readOnly);
    }
}
