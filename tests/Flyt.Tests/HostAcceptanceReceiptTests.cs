using Flyt.Core;

namespace Flyt.Tests;

public sealed class HostAcceptanceReceiptTests
{
    [Fact]
    public void Receipt_PassesOnlyWhenEveryGatePassed()
    {
        var passed = Receipt([new AcceptanceCheck("visual", AcceptanceCheckStatus.Passed, "confirmed")]);
        var incomplete = Receipt([new AcceptanceCheck("resume", AcceptanceCheckStatus.NotRun, "missing metadata")]);

        Assert.True(passed.Passed);
        Assert.False(incomplete.Passed);
    }

    [Fact]
    public void Markdown_StatesTheRedactionBoundary()
    {
        var markdown = HostAcceptanceReceiptWriter.ToMarkdown(
            Receipt([new AcceptanceCheck("toast", AcceptanceCheckStatus.Passed, "operator confirmed")]));

        Assert.Contains("PASS", markdown, StringComparison.Ordinal);
        Assert.Contains("excludes prompts", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("session ID:", markdown, StringComparison.OrdinalIgnoreCase);
    }

    private static HostAcceptanceReceipt Receipt(IReadOnlyList<AcceptanceCheck> checks) => new(
        "1.0",
        DateTimeOffset.UnixEpoch,
        "0.5.1",
        "Windows",
        "Windows Terminal",
        "psmux 3.4.0",
        new Dictionary<string, string> { ["Claude"] = "1.0", ["Codex"] = "1.0" },
        "colour, unicode, standard-contrast",
        checks);
}
