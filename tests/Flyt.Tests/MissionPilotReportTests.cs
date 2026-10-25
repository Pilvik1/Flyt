using Flyt.Core;

namespace Flyt.Tests;

public sealed class MissionPilotReportTests
{
    [Fact]
    public void Create_PassesFivePairedWindowsScenariosAcrossTwoWeeks()
    {
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var scenarios = new[] { "docs", "bug", "feature", "refactor", "recovery" };
        var receipts = new List<MissionPilotReceipt>();
        for (var index = 0; index < scenarios.Length; index++)
        {
            receipts.Add(Receipt(
                scenarios[index],
                MissionPilotMode.Baseline,
                start.AddDays(index),
                interventions: 4));
            receipts.Add(Receipt(
                scenarios[index],
                MissionPilotMode.Mission,
                start.AddDays(index == scenarios.Length - 1 ? 14 : index * 3),
                interventions: 1,
                failureInjected: index == scenarios.Length - 1,
                recoveryCount: index == scenarios.Length - 1 ? 1 : 0));
        }

        var report = MissionPilotReport.Create(receipts);

        Assert.True(report.Ready);
        Assert.Equal(75d, report.InterventionReductionPercent);
        Assert.Equal(14, report.PilotSpanDays);
        Assert.True(report.WindowsOnly);
        Assert.True(report.RecoveryScenarioProved);
    }

    [Fact]
    public void Create_DoesNotInventReductionWithoutBaselineInterventions()
    {
        var now = DateTimeOffset.UtcNow;
        var report = MissionPilotReport.Create(
            [
                Receipt("docs", MissionPilotMode.Baseline, now, interventions: 0),
                Receipt("docs", MissionPilotMode.Mission, now, interventions: 0),
            ]);

        Assert.Null(report.InterventionReductionPercent);
        Assert.False(report.InterventionTargetMet);
        Assert.False(report.Ready);
    }

    private static MissionPilotReceipt Receipt(
        string scenario,
        MissionPilotMode mode,
        DateTimeOffset completedAt,
        int interventions,
        bool failureInjected = false,
        int recoveryCount = 0) => new(
        Guid.NewGuid(),
        mode == MissionPilotMode.Mission ? Guid.NewGuid() : null,
        scenario,
        mode,
        Passed: true,
        EvidenceComplete: true,
        failureInjected,
        interventions,
        Enumerable.Range(0, interventions).Select(index => $"intervention-{index}").ToArray(),
        AttemptCount: mode == MissionPilotMode.Mission ? 1 : 0,
        ReviewBlockCount: 0,
        recoveryCount,
        DurationMinutes: 20,
        "Windows 11",
        "Codex/codex-app-server-v2/1.0",
        completedAt.AddMinutes(-20),
        completedAt,
        completedAt);
}
