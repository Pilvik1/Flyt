using Flyt.Core;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class PsmuxLayoutPlannerTests
{
    [Fact]
    public void BuildPreview_CreatesSixSessionTiledPlan()
    {
        var sessions = new[]
        {
            Session(AgentProvider.Claude, "planner"),
            Session(AgentProvider.Claude, "builder-1"),
            Session(AgentProvider.Claude, "builder-2"),
            Session(AgentProvider.Codex, "reviewer"),
            Session(AgentProvider.Codex, "verifier"),
            Session(AgentProvider.Shell, "ops"),
        };

        var commands = PsmuxLayoutPlanner.BuildPreview("psmux", "Flyt Demo", sessions);

        Assert.Equal(24, commands.Count);
        Assert.Equal("new-session", commands[0].Arguments[0]);
        Assert.Contains("240", commands[0].Arguments);
        Assert.Contains("60", commands[0].Arguments);
        Assert.Contains(commands, command => command.Arguments.Contains("claude"));
        Assert.Contains(commands, command => command.Arguments.Contains("codex"));
        Assert.Contains(commands, command => command.Arguments.Contains("pwsh"));
        Assert.Equal("select-layout", commands[^1].Arguments[0]);
        Assert.Contains("Flyt-Demo:0", commands[^1].Arguments);
    }

    [Fact]
    public void BuildPreview_RejectsMoreThanSixSessions()
    {
        var sessions = Enumerable.Range(1, 7)
            .Select(index => Session(AgentProvider.Claude, $"agent-{index}"))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PsmuxLayoutPlanner.BuildPreview("psmux", "too-many", sessions));
    }

    [Fact]
    public void BuildPreview_RejectsDuplicateNames()
    {
        var sessions = new[]
        {
            Session(AgentProvider.Claude, "builder"),
            Session(AgentProvider.Codex, "builder"),
        };

        Assert.Throws<ArgumentException>(
            () => PsmuxLayoutPlanner.BuildPreview("psmux", "duplicate", sessions));
    }

    private static AgentSessionSpec Session(AgentProvider provider, string name) =>
        AgentSessionSpec.Create(
            provider,
            name,
            Environment.CurrentDirectory,
            provider switch
            {
                AgentProvider.Claude => "claude",
                AgentProvider.Codex => "codex",
                AgentProvider.Shell => "pwsh",
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            });
}
