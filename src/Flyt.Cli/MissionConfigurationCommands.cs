using System.Globalization;
using Flyt.Core;
using Flyt.Persistence;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> ConfigureMissionAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException(workspace);
        }

        var gitCheck = await ProbeCommandAsync(
            "git",
            ["-C", workspace, "rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);
        if (!gitCheck.IsSuccess)
        {
            throw new InvalidOperationException(
                "Missions require a Git repository. Use Desk mode for ordinary folders.");
        }

        var interactive = !Console.IsInputRedirected;
        var goal = GetOption(args, "--goal")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>("What should this Mission deliver?")
                        .ValidationErrorMessage("[red]Describe a concrete repository change or artifact.[/]")
                        .Validate(value => !string.IsNullOrWhiteSpace(value)))
                : throw new ArgumentException("Provide --goal when input is redirected."));
        var suggestedName = SuggestMissionName(workspace);
        var name = GetOption(args, "--name")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>("Mission name?")
                        .DefaultValue(suggestedName)
                        .ValidationErrorMessage(
                            "[red]Use 1-80 letters, digits, dots, dashes, or underscores.[/]")
                        .Validate(IsValidMissionName))
                : suggestedName);
        if (!IsValidMissionName(name))
        {
            throw new ArgumentException(
                "Mission names must contain 1-80 letters, digits, dots, dashes, or underscores and cannot be a GUID.");
        }

        var profileValue = GetOption(args, "--profile")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Autonomy profile?")
                        .AddChoices("bounded", "manual", "auto"))
                : "bounded");
        var policy = AutonomyPolicy.Parse(profileValue);
        var acceptanceValue = GetOption(args, "--acceptance")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>("Acceptance criteria [grey](separate with |)[/]?")
                        .ValidationErrorMessage("[red]Enter at least one observable criterion.[/]")
                        .Validate(value => SplitListOption(value).Length > 0))
                : throw new ArgumentException("Provide --acceptance when input is redirected."));
        var stepsValue = GetOption(args, "--steps")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>("Plan steps [grey](separate with |)[/]?")
                        .DefaultValue("implement|review|verify")
                        .ValidationErrorMessage("[red]Enter at least one step.[/]")
                        .Validate(value => SplitListOption(value).Length > 0))
                : throw new ArgumentException("Provide --steps when input is redirected."));
        var verificationCommand = GetOption(args, "--verify")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>("Deterministic verification command?")
                        .DefaultValue("git diff --check")
                        .ValidationErrorMessage("[red]Enter one direct command.[/]")
                        .Validate(value => !string.IsNullOrWhiteSpace(value)))
                : throw new ArgumentException("Provide --verify when input is redirected."));

        var providerValue = GetOption(args, "--provider")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Builder provider?")
                        .AddChoices("claude", "codex"))
                : "claude");
        var builder = ParseMissionProvider(providerValue, "builder");
        var reviewerValue = GetOption(args, "--reviewer")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Reviewer provider?")
                        .AddChoices("auto", "claude", "codex"))
                : "auto");
        var reviewer = ParseMissionReviewer(reviewerValue);
        var timeoutMinutes = GetOption(args, "--timeout-minutes") is { } rawTimeout
            ? int.Parse(rawTimeout, CultureInfo.InvariantCulture)
            : interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<int>("Timeout in minutes?")
                        .DefaultValue((int)policy.MissionTimeout.TotalMinutes)
                        .ValidationErrorMessage("[red]Choose between 1 and 720 minutes.[/]")
                        .Validate(value => value is >= 1 and <= 720))
                : (int)policy.MissionTimeout.TotalMinutes;

        if (!interactive && HasFlag(args, "--run") && !HasFlag(args, "--approve"))
        {
            throw new ArgumentException("--run requires --approve so the plan crosses an explicit approval boundary.");
        }

        try
        {
            _ = await store.ResolveMissionIdAsync(name, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Mission name '{name}' is already in use. Choose another name.");
        }
        catch (KeyNotFoundException)
        {
            // Expected for a new human-readable Mission name.
        }

        var mission = Mission.Create(goal, workspace, policy);
        var plan = MissionPlan.Create(
            mission.Id,
            SplitListOption(acceptanceValue),
            SplitListOption(stepsValue),
            verificationCommand);
        var configuration = MissionConfiguration.Create(
            mission.Id,
            name,
            builder,
            reviewer,
            timeoutMinutes);

        PrintMissionConfigurationPreview(mission, plan, configuration);
        var confirmed = HasFlag(args, "--yes")
                        || (interactive && AnsiConsole.Confirm("Save this Mission configuration?", defaultValue: true));
        if (!confirmed)
        {
            AnsiConsole.MarkupLine("[yellow]Mission configuration cancelled; nothing was saved.[/]");
            return 2;
        }

        await store.CreateMissionAsync(mission, cancellationToken).ConfigureAwait(false);
        await store.SaveMissionPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        await store.SaveMissionConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Mission configured:[/] {Markup.Escape(configuration.Name)}");

        var approve = HasFlag(args, "--approve")
                      || (interactive && AnsiConsole.Confirm("Approve this exact plan?", defaultValue: false));
        if (HasFlag(args, "--run") && !approve)
        {
            throw new ArgumentException("--run requires --approve so the plan crosses an explicit approval boundary.");
        }

        if (!approve)
        {
            AnsiConsole.MarkupLine(
                $"Approve with: [aqua]flyt mission approve --id {Markup.Escape(configuration.Name)}[/]");
            return 0;
        }

        await store.ApproveMissionPlanAsync(mission.Id, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]Plan approved.[/]");
        var runNow = HasFlag(args, "--run")
                     || (interactive && AnsiConsole.Confirm("Run this Mission now?", defaultValue: true));
        if (!runNow)
        {
            AnsiConsole.MarkupLine(
                $"Run with: [aqua]flyt mission run --id {Markup.Escape(configuration.Name)}[/]");
            return 0;
        }

        return await RunMissionExecutionAsync(
            store,
            ["--id", configuration.Name],
            cancellationToken).ConfigureAwait(false);
    }

    internal static AgentProvider ParseMissionProvider(string value, string role) =>
        value.Trim().ToLowerInvariant() switch
        {
            "claude" => AgentProvider.Claude,
            "codex" => AgentProvider.Codex,
            _ => throw new ArgumentException(
                $"Unsupported Mission {role} '{value}'. Expected claude or codex."),
        };

    internal static AgentProvider? ParseMissionReviewer(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "auto" => null,
            _ => ParseMissionProvider(value, "reviewer"),
        };

    internal static bool IsValidMissionName(string value)
    {
        try
        {
            _ = MissionConfiguration.Create(Guid.Empty, value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string SuggestMissionName(string workspace)
    {
        var repository = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        var safeRepository = new string(repository
            .Where(value => char.IsLetterOrDigit(value) || value is '-' or '_' or '.')
            .Take(40)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeRepository) || !char.IsLetterOrDigit(safeRepository[0]))
        {
            safeRepository = "mission";
        }

        return $"{safeRepository}-{DateTimeOffset.Now:MMdd-HHmmss}";
    }

    private static void PrintMissionConfigurationPreview(
        Mission mission,
        MissionPlan plan,
        MissionConfiguration configuration)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Name[/]", Markup.Escape(configuration.Name));
        grid.AddRow("[grey]Workspace[/]", Markup.Escape(mission.WorkspacePath));
        grid.AddRow("[grey]Goal[/]", Markup.Escape(mission.Goal));
        grid.AddRow("[grey]Profile[/]", Markup.Escape(mission.Profile));
        grid.AddRow("[grey]Builder[/]", Markup.Escape(configuration.BuilderProvider.ToString()));
        grid.AddRow(
            "[grey]Reviewer[/]",
            Markup.Escape(configuration.ReviewerProvider?.ToString() ?? "Auto (opposite provider)"));
        grid.AddRow(
            "[grey]Timeout[/]",
            $"{configuration.TimeoutMinutes.ToString(CultureInfo.InvariantCulture)} minutes");
        grid.AddRow(
            "[grey]Acceptance[/]",
            Markup.Escape(string.Join(Environment.NewLine, plan.AcceptanceCriteria)));
        grid.AddRow("[grey]Steps[/]", Markup.Escape(string.Join(Environment.NewLine, plan.Steps)));
        grid.AddRow("[grey]Verification[/]", Markup.Escape(plan.VerificationCommand));
        AnsiConsole.Write(new Panel(grid).Header("[bold aqua]Mission preview[/]").RoundedBorder());
    }

    private static void PrintMissionConfiguration(MissionConfiguration configuration)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Name[/]", Markup.Escape(configuration.Name));
        grid.AddRow("[grey]Builder[/]", Markup.Escape(configuration.BuilderProvider.ToString()));
        grid.AddRow(
            "[grey]Reviewer[/]",
            Markup.Escape(configuration.ReviewerProvider?.ToString() ?? "Auto (opposite provider)"));
        grid.AddRow(
            "[grey]Timeout[/]",
            $"{configuration.TimeoutMinutes.ToString(CultureInfo.InvariantCulture)} minutes");
        AnsiConsole.Write(new Panel(grid).Header("[bold aqua]Run configuration[/]").RoundedBorder());
    }
}
