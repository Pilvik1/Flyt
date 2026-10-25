using System.Globalization;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;
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
            "run" => await RunMissionExecutionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "retry" => await RetryMissionExecutionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "recover" => await RecoverMissionExecutionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "integrate" => await IntegrateMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
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

        var workspace = await store.GetMissionWorkspaceAsync(id, cancellationToken).ConfigureAwait(false);
        var attempt = await store.GetLatestMissionAttemptAsync(id, cancellationToken).ConfigureAwait(false);
        var receipt = await store.GetLatestMissionVerificationReceiptAsync(id, cancellationToken)
            .ConfigureAwait(false);
        var review = await store.GetLatestMissionReviewReceiptAsync(id, cancellationToken).ConfigureAwait(false);
        if (workspace is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Worktree[/] {Markup.Escape(workspace.WorktreePath)} "
                + $"[grey]Branch[/] {Markup.Escape(workspace.BranchName)}");
        }

        if (attempt is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Latest attempt[/] {attempt.Number} / {Markup.Escape(attempt.Provider.ToString())} / "
                + Markup.Escape(attempt.Status.ToString()));
        }

        if (receipt is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Verification[/] {(receipt.Passed ? "[green]passed[/]" : "[red]failed[/]")} "
                + $"([grey]exit {receipt.ExitCode}[/], {receipt.ChangedPaths.Count} changed path(s))");
        }

        if (review is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Review[/] {Markup.Escape(review.Provider.ToString())} / "
                + Markup.Escape(review.Verdict.ToString()));
        }

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

    private static async Task<int> RunMissionExecutionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var provider = (GetOption(args, "--provider") ?? "claude").ToLowerInvariant() switch
        {
            "claude" => AgentProvider.Claude,
            "codex" => AgentProvider.Codex,
            var value => throw new ArgumentException(
                $"Unsupported Mission provider '{value}'. Expected claude or codex."),
        };
        var timeoutMinutes = int.Parse(
            GetOption(args, "--timeout-minutes") ?? "90",
            CultureInfo.InvariantCulture);
        if (timeoutMinutes is < 1 or > 720)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Mission timeout must be between 1 and 720 minutes.");
        }

        AgentProvider? reviewer = (GetOption(args, "--reviewer") ?? "auto").ToLowerInvariant() switch
        {
            "auto" => null,
            "claude" => AgentProvider.Claude,
            "codex" => AgentProvider.Codex,
            var value => throw new ArgumentException(
                $"Unsupported Mission reviewer '{value}'. Expected auto, claude, or codex."),
        };

        var worktreeRoot = Path.Combine(StatePaths.StateDirectory, "missions");
        var coordinator = new MissionExecutionCoordinator(
            store,
            new GitWorktreeManager(CommandExecutor, worktreeRoot),
            new MissionWorkspaceSafetyScanner(),
            CommandExecutor);
        var outcome = await coordinator.RunAsync(
            id,
            provider,
            TimeSpan.FromMinutes(timeoutMinutes),
            reviewer,
            cancellationToken).ConfigureAwait(false);

        PrintMission(outcome.Mission);
        AnsiConsole.MarkupLine($"[grey]Worktree[/] {Markup.Escape(outcome.Workspace.WorktreePath)}");
        AnsiConsole.MarkupLine(
            $"[grey]Attempt[/] {outcome.Attempt.Number} / {Markup.Escape(outcome.Attempt.Status.ToString())}");
        if (outcome.Verification is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Verification[/] {(outcome.Verification.Passed ? "[green]passed[/]" : "[red]failed[/]")}");
        }

        if (outcome.Review is not null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Review[/] {Markup.Escape(outcome.Review.Provider.ToString())} / "
                + Markup.Escape(outcome.Review.Verdict.ToString()));
        }

        if (outcome.Mission.State == MissionState.Integrating)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Stopped before integration.[/] Inspect the diff and evidence; Flyt did not commit, push, or merge.");
            return 0;
        }

        return 2;
    }

    private static async Task<int> RecoverMissionExecutionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var coordinator = new MissionExecutionCoordinator(
            store,
            new GitWorktreeManager(CommandExecutor, Path.Combine(StatePaths.StateDirectory, "missions")),
            new MissionWorkspaceSafetyScanner(),
            CommandExecutor);
        var outcome = await coordinator.RecoverAsync(id, cancellationToken).ConfigureAwait(false);
        PrintMission(outcome.Mission);
        AnsiConsole.MarkupLine(
            "[yellow]Recovered conservatively.[/] No provider was relaunched; inspect the worktree before retrying.");
        return 2;
    }

    private static async Task<int> RetryMissionExecutionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var mission = await store.GetMissionAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {id:D} was not found.");
        if (mission.State != MissionState.Blocked)
        {
            throw new InvalidOperationException(
                $"Only a Blocked Mission can retry; current state is {mission.State}.");
        }

        var attempts = await store.GetMissionAttemptsAsync(id, cancellationToken).ConfigureAwait(false);
        var policy = AutonomyPolicy.Parse(mission.Profile);
        if (attempts.Count >= policy.MaxFixLoops + 1)
        {
            throw new InvalidOperationException(
                $"Mission reached the {policy.Name} profile limit of {policy.MaxFixLoops + 1} provider attempt(s).");
        }

        await store.TransitionMissionAsync(
            id,
            MissionState.Planning,
            $"Operator approved retry {attempts.Count + 1} of {policy.MaxFixLoops + 1}.",
            cancellationToken).ConfigureAwait(false);
        return await RunMissionExecutionAsync(store, args, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> IntegrateMissionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var id = ParseMissionId(RequireOption(args, "--id"));
        var coordinator = new MissionIntegrationCoordinator(
            store,
            new GitWorktreeManager(CommandExecutor, Path.Combine(StatePaths.StateDirectory, "missions")),
            new MissionWorkspaceSafetyScanner(),
            CommandExecutor);
        var result = await coordinator.PrepareAsync(
            id,
            RequireOption(args, "--commit-message"),
            HasFlag(args, "--push"),
            HasFlag(args, "--draft-pr"),
            GetOption(args, "--base") ?? "main",
            cancellationToken).ConfigureAwait(false);

        PrintMission(result.Mission);
        AnsiConsole.MarkupLine($"[green]Commit:[/] {Markup.Escape(result.Commit)}");
        if (result.Pushed)
        {
            AnsiConsole.MarkupLine("[green]Branch pushed.[/]");
        }

        if (!string.IsNullOrWhiteSpace(result.PullRequestUrl))
        {
            AnsiConsole.MarkupLine($"[green]Draft PR:[/] {Markup.Escape(result.PullRequestUrl)}");
        }

        AnsiConsole.MarkupLine("[yellow]Merge remains manual.[/]");
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
