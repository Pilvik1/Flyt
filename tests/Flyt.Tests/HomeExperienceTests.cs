using Flyt.Cli;
using Flyt.Core;

namespace Flyt.Tests;

public sealed class HomeExperienceTests
{
    [Fact]
    public void Evaluate_WithoutTemplateOrDesk_SelectsFirstRunState()
    {
        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "flyt-home-empty"));

        var result = FlytHomeExperience.Evaluate(workspace, hasTemplate: false, []);

        Assert.Equal(workspace, result.WorkspacePath);
        Assert.False(result.HasTemplate);
        Assert.Null(result.Desk);
    }

    [Fact]
    public void Evaluate_WithTemplateAndNoDesk_SelectsReadyState()
    {
        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "flyt-home-template"));

        var result = FlytHomeExperience.Evaluate(workspace, hasTemplate: true, []);

        Assert.True(result.HasTemplate);
        Assert.Null(result.Desk);
    }

    [Fact]
    public void Evaluate_PrefersMostRecentDeskForCurrentWorkspace()
    {
        var workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "flyt-home-current"));
        var other = CreateDesk("other", Path.Combine(Path.GetTempPath(), "flyt-home-other"), DeskStatus.Running);
        var current = CreateDesk("current", workspace, DeskStatus.Stopped);

        var result = FlytHomeExperience.Evaluate(workspace, hasTemplate: true, [other, current]);

        Assert.Equal("current", result.Desk?.SessionName);
        Assert.Equal(DeskStatus.Stopped, result.Desk?.Status);
    }

    private static Desk CreateDesk(string name, string workspace, DeskStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new Desk(
            Guid.NewGuid(),
            name,
            Path.GetFullPath(workspace),
            "default",
            [],
            2,
            12_000,
            status,
            [],
            now,
            now,
            now);
    }
}
