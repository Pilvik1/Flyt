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
        var directories = new[] { "/repo/one", "/repo/two", "/repo/three", "/repo/four", "/repo/five" };
        var panes = Program.BuildConfiguredPanes(
            [AgentProvider.Claude, AgentProvider.Claude, AgentProvider.Codex, AgentProvider.OpenCode, AgentProvider.Shell],
            directories);

        Assert.Equal(["claude-1", "claude-2", "codex", "opencode", "shell"], panes.Select(value => value.Name));
        Assert.Equal("reviewer", panes[2].Role);
        Assert.Equal("builder", panes[3].Role);
        Assert.Equal("ops", panes[4].Role);
        Assert.Equal(directories, panes.Select(value => value.WorkingDirectory));
    }

    [Fact]
    public void BuildConfiguredPanes_AcceptsTenAndRejectsEleven()
    {
        var ten = Enumerable.Repeat(AgentProvider.Shell, 10).ToArray();
        var eleven = Enumerable.Repeat(AgentProvider.Shell, 11).ToArray();

        Assert.Equal(10, Program.BuildConfiguredPanes(ten).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => Program.BuildConfiguredPanes(eleven));
    }

    [Fact]
    public void BuildConfiguredPanes_RejectsMismatchedWorkingDirectories() =>
        Assert.Throws<ArgumentException>(() => Program.BuildConfiguredPanes(
            [AgentProvider.Claude, AgentProvider.Codex],
            ["/only/one"]));

    [Fact]
    public void ParseConfiguredWorkingDirectories_ResolvesRelativePathsAndValidatesExistence()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "flyt-config-tests", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(workspace, "first");
        var second = Path.Combine(workspace, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var directories = Program.ParseConfiguredWorkingDirectories("first|./second", workspace);

            Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], directories);
            Assert.Throws<DirectoryNotFoundException>(() =>
                Program.ParseConfiguredWorkingDirectories("missing", workspace));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void ResolveWorkingDirectory_AcceptsRecentDirectoryNumber()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "flyt-config-tests", Guid.NewGuid().ToString("N"));
        var recent = Path.Combine(workspace, "recent");
        Directory.CreateDirectory(recent);
        try
        {
            Assert.Equal(
                Path.GetFullPath(recent),
                Program.ResolveWorkingDirectory("1", workspace, [recent]));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
