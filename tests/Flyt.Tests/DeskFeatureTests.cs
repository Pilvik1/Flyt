using Flyt.Cli;
using Flyt.Core;
using Flyt.Providers;

namespace Flyt.Tests;

public sealed class DeskFeatureTests
{
    [Fact]
    public void UniquePaneName_AppendsFirstAvailableNumericSuffix()
    {
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        DeskPane Pane(string name) => new(
            Guid.NewGuid(), deskId, $"%{name}", AgentProvider.Codex, name,
            Environment.CurrentDirectory, "codex", "reviewer", "Review", "magenta", [], null,
            DeskPaneStatus.Running, DeskStateSource.Registered, null, null, now, now, now);

        var result = Program.UniquePaneName("codex", [Pane("codex"), Pane("codex-2"), Pane("other")]);

        Assert.Equal("codex-3", result);
    }

    [Theory]
    [InlineData("planner", "cyan")]
    [InlineData("builder", "blue")]
    [InlineData("reviewer", "magenta")]
    [InlineData("verifier", "green")]
    [InlineData("ops", "yellow")]
    public void Palette_AssignsStableRoleColors(string role, string expected) =>
        Assert.Equal(expected, DeskPalette.ForRole(role));

    [Fact]
    public void Template_RejectsDuplicatePaneNames()
    {
        var template = new DeskTemplate(
            "duplicate",
            [
                new DeskTemplatePane(AgentProvider.Claude, "builder", "builder", "Build"),
                new DeskTemplatePane(AgentProvider.Codex, "BUILDER", "reviewer", "Review"),
            ]);

        Assert.Throws<ArgumentException>(template.Validate);
    }

    [Fact]
    public void Template_AcceptsTenPanesAndRejectsEleven()
    {
        var tenPanes = Enumerable.Range(1, 10)
            .Select(index => new DeskTemplatePane(
                AgentProvider.Shell,
                $"shell-{index}",
                "ops",
                $"Shell {index}"))
            .ToArray();
        var supported = new DeskTemplate("ten", tenPanes);
        var unsupported = new DeskTemplate(
            "eleven",
            [.. tenPanes, new DeskTemplatePane(AgentProvider.Shell, "shell-11", "ops", "Shell 11")]);

        supported.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(unsupported.Validate);
    }

    [Fact]
    public void Template_RejectsUnsafeResourceAndPresentationValues()
    {
        var lowMemory = new DeskTemplate(
            "unsafe",
            [new DeskTemplatePane(AgentProvider.Claude, "builder", "builder", "Build")],
            MemoryWarningMegabytes: 128);
        var invalidColor = new DeskTemplate(
            "unsafe",
            [new DeskTemplatePane(AgentProvider.Claude, "builder", "builder", "Build", Color: "red[/]")]);

        Assert.Throws<ArgumentOutOfRangeException>(lowMemory.Validate);
        Assert.Throws<ArgumentException>(invalidColor.Validate);
    }

    [Fact]
    public void ProviderRegistry_BuildsProviderOwnedResumeCommands()
    {
        var registry = ProviderRegistry.CreateDefault();

        Assert.Equal("claude --resume 'abc-123'", registry.BuildInteractiveCommand(AgentProvider.Claude, "abc-123"));
        Assert.Equal("codex resume 'abc-123'", registry.BuildInteractiveCommand(AgentProvider.Codex, "abc-123"));
        Assert.Equal("opencode --session 'abc-123'", registry.BuildInteractiveCommand(AgentProvider.OpenCode, "abc-123"));
        Assert.Equal("pwsh", registry.BuildInteractiveCommand(AgentProvider.Shell, "ignored"));
    }

    [Theory]
    [InlineData("Do you want to proceed? [y/n]", DeskPaneStatus.NeedsInput)]
    [InlineData("Permission required: allow this command", DeskPaneStatus.NeedsInput)]
    [InlineData("Working… esc to interrupt", DeskPaneStatus.Working)]
    public void AttentionDetector_RecognizesConservativeProviderSignals(
        string output,
        DeskPaneStatus expected) =>
        Assert.Equal(expected, ProviderAttentionDetector.Detect(AgentProvider.Claude, output));

    [Fact]
    public void AttentionDetector_DoesNotGuessFromOrdinaryOutput() =>
        Assert.Null(ProviderAttentionDetector.Detect(AgentProvider.Codex, "Tests passed. Ready."));

    [Fact]
    public void AttentionDetector_IgnoresAnsweredPromptInOldScrollback()
    {
        var output = "Do you want to proceed? [y/n]\ny\nstep 1\nstep 2\nstep 3\nstep 4\nstep 5\nDone.";

        Assert.Null(ProviderAttentionDetector.Detect(AgentProvider.Claude, output));
    }
}
