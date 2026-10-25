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
    public void BuildPreview_RejectsMoreThanTenSessions()
    {
        var sessions = Enumerable.Range(1, 11)
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

    [Theory]
    [InlineData(1, "tiled")]
    [InlineData(2, "even-horizontal")]
    [InlineData(3, "main-vertical")]
    [InlineData(4, "tiled")]
    [InlineData(6, "tiled")]
    [InlineData(10, "tiled")]
    public void LayoutNameForCount_UsesReadableAdaptiveLayouts(int count, string expected)
    {
        Assert.Equal(expected, PsmuxLayoutPlanner.LayoutNameForCount(count));
    }

    [Fact]
    public void PaneTitle_IsCompactScannableAndKeepsStableIdentity()
    {
        var session = AgentSessionSpec.Create(
            AgentProvider.Claude,
            "builder",
            Path.Combine(Path.GetTempPath(), "Flyt"),
            "claude",
            role: "builder",
            task: new string('x', 180));

        var title = PsmuxLayoutPlanner.PaneTitle(session, DeskPaneStatus.NeedsInput);

        Assert.StartsWith("builder · BUILDER · ! NEEDS INPUT ·", title, StringComparison.Ordinal);
        Assert.Contains("Flyt", title, StringComparison.Ordinal);
        Assert.True(title.Length <= PsmuxLayoutPlanner.MaximumPaneTitleLength);
        Assert.Equal("builder", PsmuxLayoutPlanner.StableNameFromTitle(title));
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
                AgentProvider.OpenCode => "opencode",
                AgentProvider.Shell => "pwsh",
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            });
}
