using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class TerminalCompatibilityTests
{
    [Theory]
    [InlineData("psmux 3.4.0", 3, 4, 0)]
    [InlineData("psmux version 4.1.2 (windows)", 4, 1, 2)]
    public void ParsePsmuxVersion_ExtractsSemanticVersion(
        string output,
        int major,
        int minor,
        int build) =>
        Assert.Equal(new Version(major, minor, build), TerminalCompatibility.ParsePsmuxVersion(output));

    [Fact]
    public void Evaluate_BlocksOldPsmuxAndUndersizedSixPaneViewport()
    {
        var report = TerminalCompatibility.Evaluate(
            "psmux 3.3.9",
            6,
            new TerminalViewport(120, 30));

        Assert.False(report.IsSupported);
        Assert.Contains(report.Errors, value => value.Contains("unsupported", StringComparison.Ordinal));
        Assert.Contains(report.Errors, value => value.Contains("160x40", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_AllowsPinnedVersionAndSupportedViewport()
    {
        var report = TerminalCompatibility.Evaluate(
            "psmux 3.4.0",
            6,
            new TerminalViewport(180, 50));

        Assert.True(report.IsSupported);
        Assert.Empty(report.Errors);
    }
}
