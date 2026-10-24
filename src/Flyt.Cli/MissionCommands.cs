using System.Globalization;
using Flyt.Core;
using Flyt.Persistence;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> RunMissionAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintMissionHelp();
            return 0;
        }

        var store = CreateStateStore();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        return args[0].ToLowerInvariant() switch
        {
            "create" => await CreateMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "plan" => await SaveMissionPlanAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "list" => await ListMissionsAsync(store, cancellationToken).ConfigureAwait(false),
            "show" => await ShowMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "approve" => await ApproveMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "pause" => await TransitionMissionAsync(
                store,
                args[1..],
                MissionState.Paused,
                "Operator paused the mission.",
                cancellationToken).ConfigureAwait(false),
            "resume" => await ResumeMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "cancel" => await TransitionMissionAsync(
                store,
                args[1..],
                MissionState.Cancelled,
                "Operator cancelled the mission.",
                cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand($"mission {args[0]}"),
        };
    }

    private static async Task<int> CreateMissionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var goal = RequireOption(args, "--goal");
        var path = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        var gitCheck = await ProbeCommandAsync(
            "git",
            ["-C", path, "rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (!gitCheck.IsSuccess)
        {
            throw new InvalidOperationException(
                "V1 write missions require a git repository. Desk mode still supports ordinary folders.");
        }

        var profile = AutonomyPolicy.Parse(GetOption(args, "--profile") ?? "bounded");
        var mission = Mission.Create(goal, path, profile);
        await store.CreateMissionAsync(mission, cancellationToken).ConfigureAwait(false);

        PrintMission(mission);
        AnsiConsole.MarkupLine(
            $"Persist a plan with: [aqua]flyt mission plan --id {mission.Id:D} "
            + "--acceptance \"criterion 1|criterion 2\" --steps \"build|review|verify\" "
            + "--verify \"dotnet test\"[/]");
        return 0;
    }

    private static async Task<int> SaveMissionPlanAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var acceptanceCriteria = SplitListOption(RequireOption(args, "--acceptance"));
        var steps = SplitListOption(RequireOption(args, "--steps"));
        var verificationCommand = RequireOption(args, "--verify");
        var plan = MissionPlan.Create(id, acceptanceCriteria, steps, verificationCommand);
        var saved = await store.SaveMissionPlanAsync(plan, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Plan persisted:[/] version {saved.Version}");
        AnsiConsole.MarkupLine($"Approve with: [aqua]flyt mission approve --id {id:D}[/]");
        return 0;
    }

    private static async Task<int> ListMissionsAsync(
        FlytStateStore store,
        CancellationToken cancellationToken)
    {
        var missions = await store.ListMissionsAsync(cancellationToken).ConfigureAwait(false);
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("State")
            .AddColumn("Goal")
            .AddColumn("Updated");

        foreach (var mission in missions)
        {
            table.AddRow(
                mission.Id.ToString("D")[..8],
                Markup.Escape(mission.State.ToString()),
                Markup.Escape(mission.Goal),
                mission.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> ShowMissionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var mission = await store.GetMissionAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {id:D} was not found.");
        PrintMission(mission);

        var plan = await store.GetMissionPlanAsync(id, cancellationToken).ConfigureAwait(false);
        if (plan is not null)
        {
            var planGrid = new Grid().AddColumn().AddColumn();
            planGrid.AddRow("[grey]Version[/]", plan.Version.ToString(CultureInfo.InvariantCulture));
            planGrid.AddRow("[grey]Acceptance[/]", Markup.Escape(string.Join(Environment.NewLine, plan.AcceptanceCriteria)));
            planGrid.AddRow("[grey]Steps[/]", Markup.Escape(string.Join(Environment.NewLine, plan.Steps)));
            planGrid.AddRow("[grey]Verification[/]", Markup.Escape(plan.VerificationCommand));
            AnsiConsole.Write(new Panel(planGrid).Header("[bold aqua]Persisted plan[/]").RoundedBorder());
        }

        var events = await store.GetEventsAsync(id, cancellationToken).ConfigureAwait(false);
        var table = new Table()
            .Border(TableBorder.Minimal)
            .AddColumn("Seq")
            .AddColumn("Transition")
            .AddColumn("Detail");
        foreach (var missionEvent in events)
        {
            table.AddRow(
                missionEvent.Sequence.ToString(CultureInfo.InvariantCulture),
                Markup.Escape($"{missionEvent.FromState?.ToString() ?? "-"} -> {missionEvent.ToState}"),
                Markup.Escape(missionEvent.Detail));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> ApproveMissionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var mission = await store.ApproveMissionPlanAsync(id, cancellationToken).ConfigureAwait(false);
        PrintMission(mission);
        return 0;
    }

    private static async Task<int> TransitionMissionAsync(
        FlytStateStore store,
        string[] args,
        MissionState state,
        string detail,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var mission = await store.TransitionMissionAsync(id, state, detail, cancellationToken)
            .ConfigureAwait(false);
        PrintMission(mission);
        return 0;
    }

    private static async Task<int> ResumeMissionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var current = await store.GetMissionAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {id:D} was not found.");
        if (current.State != MissionState.Paused || current.ResumeState is null)
        {
            throw new InvalidOperationException("Only a paused mission with a recorded resume state can resume.");
        }

        var mission = await store.TransitionMissionAsync(
            id,
            current.ResumeState.Value,
            $"Operator resumed the mission to {current.ResumeState.Value}.",
            cancellationToken).ConfigureAwait(false);
        PrintMission(mission);
        return 0;
    }
}
