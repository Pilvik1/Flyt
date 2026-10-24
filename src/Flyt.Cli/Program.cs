using System.Globalization;
using System.Reflection;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static class Program
{
    private static readonly SystemCommandExecutor CommandExecutor = new();

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }
    }

    private static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return Task.FromResult(0);
        }

        return args[0].ToLowerInvariant() switch
        {
            "doctor" => RunDoctorAsync(cancellationToken),
            "desk" => RunDeskAsync(args[1..], cancellationToken),
            "mission" => RunMissionAsync(args[1..], cancellationToken),
            "version" or "--version" or "-v" => Task.FromResult(PrintVersion()),
            _ => Task.FromResult(UnknownCommand(args[0])),
        };
    }

    private static async Task<int> RunDoctorAsync(CancellationToken cancellationToken)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold aqua]FlytV1 environment[/]")
            .AddColumn("Component")
            .AddColumn("Status")
            .AddColumn("Details");

        AddRow(
            table,
            "Operating system",
            OperatingSystem.IsWindows(),
            System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        var registry = ProviderRegistry.CreateDefault();
        var providerProbes = await registry.ProbeAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var probe in providerProbes)
        {
            AddRow(
                table,
                probe.Provider.ToString(),
                probe.IsAvailable,
                probe.Version ?? probe.Error ?? "Unknown");
        }

        var psmux = new PsmuxClient(CommandExecutor);
        var psmuxProbe = await psmux.ProbeAsync(cancellationToken).ConfigureAwait(false);
        AddRow(
            table,
            "psmux",
            psmuxProbe.IsSuccess,
            FirstNonEmpty(psmuxProbe.StandardOutput, psmuxProbe.StandardError));

        var gitResult = await ProbeCommandAsync("git", ["--version"], cancellationToken).ConfigureAwait(false);
        AddRow(table, "Git", gitResult.IsSuccess, FirstNonEmpty(gitResult.StandardOutput, gitResult.StandardError));

        var stateStore = CreateStateStore();
        await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        AddRow(table, "SQLite state", true, StatePaths.DatabasePath);

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[grey]Native Windows, psmux, Claude, and Codex are required for the Phase 0 proof.[/]");

        return OperatingSystem.IsWindows()
               && providerProbes.All(probe => probe.IsAvailable)
               && psmuxProbe.IsSuccess
               && gitResult.IsSuccess
            ? 0
            : 2;
    }

    private static async Task<int> RunDeskAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDeskHelp();
            return 0;
        }

        var psmux = new PsmuxClient(CommandExecutor);
        return args[0].ToLowerInvariant() switch
        {
            "start" => await StartDeskAsync(psmux, args[1..], cancellationToken).ConfigureAwait(false),
            "status" => await ShowDeskStatusAsync(psmux, cancellationToken).ConfigureAwait(false),
            "attach" => await AttachDeskAsync(psmux, args[1..], cancellationToken).ConfigureAwait(false),
            "stop" => await StopDeskAsync(psmux, args[1..], cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand($"desk {args[0]}"),
        };
    }

    private static async Task<int> StartDeskAsync(
        PsmuxClient psmux,
        string[] args,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        var name = GetOption(args, "--name") ?? $"flyt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        var sessionValue = GetOption(args, "--sessions") ?? "claude:planner,claude:builder,codex:reviewer";
        var sessions = ParseSessionSpecs(sessionValue, path);
        var dryRun = HasFlag(args, "--dry-run");

        if (dryRun)
        {
            var preview = PsmuxLayoutPlanner.BuildPreview("psmux", name, sessions);
            var table = new Table()
                .Border(TableBorder.Minimal)
                .Title("[bold aqua]psmux command plan[/]")
                .AddColumn("#")
                .AddColumn("Command");
            for (var index = 0; index < preview.Count; index++)
            {
                table.AddRow(
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    Markup.Escape(preview[index].ToString()));
            }

            AnsiConsole.Write(table);
            return 0;
        }

        var probe = await psmux.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            throw new InvalidOperationException(
                $"psmux is unavailable: {FirstNonEmpty(probe.StandardError, probe.StandardOutput)}");
        }

        var sessionName = await psmux.StartDeskAsync(name, sessions, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Desk started:[/] {Markup.Escape(sessionName)}");
        AnsiConsole.MarkupLine($"Attach with: [aqua]flyt desk attach --name {Markup.Escape(sessionName)}[/]");
        return 0;
    }

    private static async Task<int> ShowDeskStatusAsync(
        PsmuxClient psmux,
        CancellationToken cancellationToken)
    {
        var sessions = await psmux.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No psmux sessions found.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("psmux session");
        foreach (var session in sessions)
        {
            table.AddRow(Markup.Escape(session));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> AttachDeskAsync(
        PsmuxClient psmux,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = RequireOption(args, "--name");
        return await psmux.AttachAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> StopDeskAsync(
        PsmuxClient psmux,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = RequireOption(args, "--name");
        await psmux.StopAsync(name, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Desk stopped:[/] {Markup.Escape(name)}");
        return 0;
    }

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
            "list" => await ListMissionsAsync(store, cancellationToken).ConfigureAwait(false),
            "show" => await ShowMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "approve" => await ApproveMissionAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
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

        var mission = Mission.Create(goal, path);
        await store.CreateMissionAsync(mission, cancellationToken).ConfigureAwait(false);
        mission = await store.TransitionMissionAsync(
            mission.Id,
            MissionState.PlanApproval,
            "Brief captured; waiting for operator plan approval.",
            cancellationToken).ConfigureAwait(false);

        PrintMission(mission);
        AnsiConsole.MarkupLine(
            $"Approve with: [aqua]flyt mission approve --id {mission.Id:D}[/]");
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
        var mission = await store.TransitionMissionAsync(
            id,
            MissionState.Planning,
            "Operator approved the mission plan.",
            cancellationToken).ConfigureAwait(false);
        PrintMission(mission);
        return 0;
    }

    private static FlytStateStore CreateStateStore() => new(StatePaths.DatabasePath);

    private static async Task<CommandResult> ProbeCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CommandExecutor.RunAsync(
                new CommandInvocation(executable, arguments),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CommandResult(127, string.Empty, exception.Message);
        }
    }

    private static List<AgentSessionSpec> ParseSessionSpecs(string value, string workingDirectory)
    {
        var result = new List<AgentSessionSpec>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new ArgumentException(
                    $"Invalid session '{item}'. Expected provider:name, for example claude:builder.");
            }

            var provider = parts[0].ToLowerInvariant() switch
            {
                "claude" => AgentProvider.Claude,
                "codex" => AgentProvider.Codex,
                "shell" or "pwsh" => AgentProvider.Shell,
                _ => throw new ArgumentException($"Unsupported provider '{parts[0]}'."),
            };
            result.Add(AgentSessionSpec.Create(provider, parts[1], workingDirectory));
        }

        return result;
    }

    private static void PrintMission(Mission mission)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Id[/]", Markup.Escape(mission.Id.ToString("D")));
        grid.AddRow("[grey]State[/]", Markup.Escape(mission.State.ToString()));
        grid.AddRow("[grey]Profile[/]", Markup.Escape(mission.Profile));
        grid.AddRow("[grey]Workspace[/]", Markup.Escape(mission.WorkspacePath));
        grid.AddRow("[grey]Goal[/]", Markup.Escape(mission.Goal));
        AnsiConsole.Write(new Panel(grid).Header("[bold aqua]Mission[/]").RoundedBorder());
    }

    private static void AddRow(Table table, string component, bool success, string details) =>
        table.AddRow(
            Markup.Escape(component),
            success ? "[green]OK[/]" : "[yellow]Missing[/]",
            Markup.Escape(details.Trim()));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "No details";

    private static Guid ParseMissionId(string value) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new ArgumentException($"'{value}' is not a valid mission id.");

    private static string RequireOption(string[] args, string option) =>
        GetOption(args, option)
        ?? throw new ArgumentException($"Missing required option {option}.");

    private static string? GetOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option {option} requires a value.");
            }

            return args[index + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase));

    private static bool IsHelp(string value) => value is "help" or "--help" or "-h";

    private static int PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        AnsiConsole.WriteLine($"FlytV1 {version}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command:[/] {Markup.Escape(command)}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        AnsiConsole.Write(
            new Panel(
                """
                [aqua]flyt doctor[/]                         Check the local toolchain
                [aqua]flyt desk start [[options]][/]         Create a psmux workspace
                [aqua]flyt desk status[/]                    List psmux sessions
                [aqua]flyt desk attach --name NAME[/]        Take over a workspace
                [aqua]flyt desk stop --name NAME[/]          Stop a workspace
                [aqua]flyt mission create [[options]][/]     Create a durable mission
                [aqua]flyt mission list[/]                   List missions
                [aqua]flyt mission show --id ID[/]           Show state and events
                [aqua]flyt mission approve --id ID[/]        Approve the plan
                """)
                .Header("[bold]FlytV1[/]")
                .RoundedBorder());
    }

    private static void PrintDeskHelp()
    {
        AnsiConsole.MarkupLine(
            "[aqua]flyt desk start[/] [[--name NAME]] [[--path PATH]] "
            + "[[--sessions claude:planner,codex:reviewer]] [[--dry-run]]");
    }

    private static void PrintMissionHelp()
    {
        AnsiConsole.MarkupLine("[aqua]flyt mission create[/] --goal TEXT [[--path PATH]]");
        AnsiConsole.MarkupLine("[aqua]flyt mission list[/]");
        AnsiConsole.MarkupLine("[aqua]flyt mission show[/] --id ID");
        AnsiConsole.MarkupLine("[aqua]flyt mission approve[/] --id ID");
    }
}

internal static class StatePaths
{
    public static string StateDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("FLYTV1_STATE_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localData, "FlytV1");
        }
    }

    public static string DatabasePath => Path.Combine(StateDirectory, "flyt.db");
}
