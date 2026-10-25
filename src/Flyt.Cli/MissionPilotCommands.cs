using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Flyt.Core;
using Flyt.Persistence;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static Task<int> RunMissionPilotAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintMissionPilotHelp();
            return Task.FromResult(0);
        }

        return args[0].ToLowerInvariant() switch
        {
            "record" => RecordMissionPilotAsync(store, args[1..], cancellationToken),
            "report" => ReportMissionPilotAsync(store, args[1..], cancellationToken),
            _ => Task.FromResult(UnknownCommand($"mission pilot {args[0]}")),
        };
    }

    private static async Task<int> RecordMissionPilotAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var scenario = RequireOption(args, "--scenario").Trim();
        var mode = (GetOption(args, "--mode") ?? "mission").ToLowerInvariant() switch
        {
            "baseline" => MissionPilotMode.Baseline,
            "mission" => MissionPilotMode.Mission,
            var value => throw new ArgumentException(
                $"Unsupported pilot mode '{value}'. Expected baseline or mission."),
        };
        var interventionReasons = SplitOptionalList(GetOption(args, "--interventions"));
        var completedAt = ParseOptionalTimestamp(GetOption(args, "--completed-at")) ?? DateTimeOffset.UtcNow;
        MissionPilotReceipt receipt;

        if (mode == MissionPilotMode.Baseline)
        {
            var duration = ParsePositiveDouble(RequireOption(args, "--duration-minutes"), "--duration-minutes");
            var startedAt = ParseOptionalTimestamp(GetOption(args, "--started-at"))
                            ?? completedAt.AddMinutes(-duration);
            receipt = new MissionPilotReceipt(
                Guid.NewGuid(),
                MissionId: null,
                scenario,
                mode,
                Passed: HasFlag(args, "--passed"),
                EvidenceComplete: HasFlag(args, "--passed"),
                FailureInjected: HasFlag(args, "--failure-injected"),
                interventionReasons.Length,
                interventionReasons,
                AttemptCount: 0,
                ReviewBlockCount: 0,
                RecoveryCount: 0,
                duration,
                RuntimeInformation.OSDescription,
                GetOption(args, "--provider-metadata") ?? "operator-recorded baseline",
                startedAt,
                completedAt,
                DateTimeOffset.UtcNow);
        }
        else
        {
            var missionId = ParseMissionId(RequireOption(args, "--id"));
            var mission = await store.GetMissionAsync(missionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Mission {missionId:D} was not found.");
            var attempts = await store.GetMissionAttemptsAsync(missionId, cancellationToken).ConfigureAwait(false);
            var reviews = await store.GetMissionReviewReceiptsAsync(missionId, cancellationToken).ConfigureAwait(false);
            var verification = await store.GetLatestMissionVerificationReceiptAsync(missionId, cancellationToken)
                .ConfigureAwait(false);
            var events = await store.GetEventsAsync(missionId, cancellationToken).ConfigureAwait(false);
            var latestReview = reviews.Count == 0 ? null : reviews[reviews.Count - 1];
            var evidenceComplete = mission.State == MissionState.Completed
                                   && attempts.Count > 0
                                   && latestReview?.Verdict == MissionReviewVerdict.Pass
                                   && verification?.Passed == true
                                   && verification.AttemptId == attempts[^1].Id;
            var startedAt = attempts.Count == 0
                ? mission.CreatedAt
                : attempts.Min(value => value.StartedAt);
            var duration = Math.Max(0d, (mission.UpdatedAt - startedAt).TotalMinutes);
            var recoveryCount = events.Count(value => value.Detail.Contains(
                "Recovered interrupted",
                StringComparison.OrdinalIgnoreCase));
            var providerMetadata = string.Join(
                "; ",
                attempts.Select(value => FormatProviderMetadata(value.Provider, value.ProviderProtocol, value.ProviderVersion))
                    .Concat(reviews.Select(value => FormatProviderMetadata(
                        value.Provider,
                        value.ProviderProtocol,
                        value.ProviderVersion)))
                    .Distinct(StringComparer.Ordinal));
            receipt = new MissionPilotReceipt(
                Guid.NewGuid(),
                missionId,
                scenario,
                mode,
                Passed: evidenceComplete,
                EvidenceComplete: evidenceComplete,
                FailureInjected: HasFlag(args, "--failure-injected"),
                interventionReasons.Length,
                interventionReasons,
                attempts.Count,
                reviews.Count(value => value.Verdict != MissionReviewVerdict.Pass),
                recoveryCount,
                duration,
                RuntimeInformation.OSDescription,
                string.IsNullOrWhiteSpace(providerMetadata) ? "unavailable" : providerMetadata,
                startedAt,
                mission.UpdatedAt,
                DateTimeOffset.UtcNow);
        }

        await store.SaveMissionPilotReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine(
            $"[green]Pilot receipt recorded:[/] {Markup.Escape(receipt.Mode.ToString())} / "
            + $"{Markup.Escape(receipt.Scenario)} / {(receipt.Passed ? "[green]PASS[/]" : "[yellow]INCOMPLETE[/]")}");
        return receipt.Passed ? 0 : 2;
    }

    private static async Task<int> ReportMissionPilotAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var receipts = await store.ListMissionPilotReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var report = MissionPilotReport.Create(receipts);
        if (HasFlag(args, "--json"))
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return report.Ready ? 0 : 2;
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Baseline scenarios[/]", report.BaselineCount.ToString(CultureInfo.InvariantCulture));
        grid.AddRow("[grey]Mission scenarios[/]", report.MissionCount.ToString(CultureInfo.InvariantCulture));
        grid.AddRow("[grey]Paired scenarios[/]", report.PairedScenarioCount.ToString(CultureInfo.InvariantCulture));
        grid.AddRow(
            "[grey]Interventions[/]",
            $"{report.BaselineInterventions} baseline / {report.MissionInterventions} Mission");
        grid.AddRow(
            "[grey]Reduction[/]",
            report.InterventionReductionPercent is null
                ? "not measurable"
                : report.InterventionReductionPercent.Value.ToString("0.0'%'", CultureInfo.InvariantCulture));
        grid.AddRow("[grey]Pilot span[/]", $"{report.PilotSpanDays} day(s)");
        grid.AddRow("[grey]Windows only[/]", PassFail(report.WindowsOnly));
        grid.AddRow("[grey]Evidence complete[/]", PassFail(report.AllEvidenceComplete));
        grid.AddRow("[grey]Recovery proved[/]", PassFail(report.RecoveryScenarioProved));
        grid.AddRow("[grey]50% intervention target[/]", PassFail(report.InterventionTargetMet));
        AnsiConsole.Write(
            new Panel(grid)
                .Header(report.Ready ? "[bold green]Pilot PASS[/]" : "[bold yellow]Pilot incomplete[/]")
                .RoundedBorder());
        return report.Ready ? 0 : 2;
    }

    private static string PassFail(bool passed) => passed ? "[green]PASS[/]" : "[yellow]PENDING[/]";

    private static string[] SplitOptionalList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : SplitListOption(value);

    private static DateTimeOffset? ParseOptionalTimestamp(string? value) =>
        value is null
            ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{value}' is not a valid ISO-8601 timestamp.");

    private static double ParsePositiveDouble(string value, string option)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new ArgumentException($"{option} must be a positive number.");
        }

        return parsed;
    }

    private static string FormatProviderMetadata(
        AgentProvider provider,
        string? protocol,
        string? version) =>
        string.Join(
            '/',
            new[] { provider.ToString(), protocol, version }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static void PrintMissionPilotHelp()
    {
        AnsiConsole.MarkupLine(
            "[aqua]flyt mission pilot record[/] --mode baseline --scenario NAME "
            + "--duration-minutes N --passed [[--interventions \"reason|reason\"]] "
            + "[[--provider-metadata TEXT]] [[--started-at ISO]] [[--completed-at ISO]]");
        AnsiConsole.MarkupLine(
            "[aqua]flyt mission pilot record[/] --mode mission --scenario NAME --id ID "
            + "[[--interventions \"reason|reason\"]] [[--failure-injected]]");
        AnsiConsole.MarkupLine("[aqua]flyt mission pilot report[/] [[--json]]");
        AnsiConsole.MarkupLine(
            "[grey]Interventions are unplanned corrections, restarts, extra prompts, terminal rescues, or evidence overrides. "
            + "Required approval and integration commands do not count.[/]");
    }
}
