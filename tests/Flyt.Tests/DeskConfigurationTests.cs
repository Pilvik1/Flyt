using Flyt.Cli;
using Flyt.Core;

namespace Flyt.Tests;

public sealed class DeskConfigurationTests
{
    [Theory]
    [InlineData("claude", AgentProvider.Claude)]
    [InlineData("codex", AgentProvider.Codex)]
    [InlineData("opencode", AgentProvider.OpenCode)]
    [InlineData("shell", AgentProvider.Shell)]
    [InlineData("pwsh", AgentProvider.Shell)]
    public void ParseConfiguredProvider_AcceptsSupportedAliases(string input, AgentProvider expected) =>
        Assert.Equal(expected, Program.ParseConfiguredProvider(input));

    [Fact]
    public void BuildConfiguredPanes_AssignsUniqueNamesAndProviderDefaults()
    {
        var panes = Program.BuildConfiguredPanes(
            [AgentProvider.Claude, AgentProvider.Claude, AgentProvider.Codex, AgentProvider.OpenCode, AgentProvider.Shell]);

        Assert.Equal(["claude-1", "claude-2", "codex", "opencode", "shell"], panes.Select(value => value.Name));
        Assert.Equal("reviewer", panes[2].Role);
        Assert.Equal("builder", panes[3].Role);
        Assert.Equal("ops", panes[4].Role);
    }

    [Fact]
    public void BuildConfiguredPanes_AcceptsTenAndRejectsEleven()
    {
        var ten = Enumerable.Repeat(AgentProvider.Shell, 10).ToArray();
        var eleven = Enumerable.Repeat(AgentProvider.Shell, 11).ToArray();

        Assert.Equal(10, Program.BuildConfiguredPanes(ten).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => Program.BuildConfiguredPanes(eleven));
    }
}
