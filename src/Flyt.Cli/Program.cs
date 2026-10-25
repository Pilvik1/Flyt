using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static readonly SystemCommandExecutor CommandExecutor = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

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
        if (args.Length == 0)
        {
            return RunHomeAsync(cancellationToken);
        }

        if (IsHelp(args[0]))
        {
            return Task.FromResult(PrintTopicHelp(args.Length > 1 ? args[1] : null));
        }

        return args[0].ToLowerInvariant() switch
        {
            "doctor" => RunDoctorAsync(args[1..], cancellationToken),
            "desk" => RunDeskAsync(args[1..], cancellationToken),
            "mission" => RunMissionAsync(args[1..], cancellationToken),
            "version" or "--version" or "-v" => Task.FromResult(PrintVersion()),
            _ => Task.FromResult(UnknownCommand(args[0])),
        };
    }

    private static async Task<int> RunDoctorAsync(string[] args, CancellationToken cancellationToken)
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
                probe.Provider == AgentProvider.OpenCode ? "OpenCode (optional)" : probe.Provider.ToString(),
                probe.IsAvailable,
                probe.Version ?? probe.Error ?? "Unknown");
        }

        var psmux = new PsmuxClient(CommandExecutor, presentation: PsmuxPresentationProfile.FromEnvironment());
        var psmuxProbe = await psmux.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var psmuxVersion = TerminalCompatibility.ParsePsmuxVersion(
            FirstNonEmpty(psmuxProbe.StandardOutput, psmuxProbe.StandardError));
        var psmuxCompatible = psmuxProbe.IsSuccess
                              && psmuxVersion is not null
                              && psmuxVersion >= TerminalCompatibility.MinimumPsmuxVersion;
        AddRow(
            table,
            "psmux",
            psmuxCompatible,
            FirstNonEmpty(psmuxProbe.StandardOutput, psmuxProbe.StandardError));

        var gitResult = await ProbeCommandAsync("git", ["--version"], cancellationToken).ConfigureAwait(false);
        AddRow(table, "Git", gitResult.IsSuccess, FirstNonEmpty(gitResult.StandardOutput, gitResult.StandardError));

        var stateStore = CreateStateStore();
        await stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        AddRow(table, "SQLite state", true, StatePaths.DatabasePath);

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[grey]Native Windows, psmux, Claude, and Codex are required for interactive target-host acceptance.[/]");

        var ready = OperatingSystem.IsWindows()
                    && providerProbes
                        .Where(probe => probe.Provider is AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Shell)
                        .All(probe => probe.IsAvailable)
                    && psmuxCompatible
                    && gitResult.IsSuccess;
        if (!HasFlag(args, "--interactive"))
        {
            return ready ? 0 : 2;
        }

        if (!ready)
        {
            AnsiConsole.MarkupLine("[red]Interactive acceptance cannot start until every prerequisite is available.[/]");
            return 2;
        }

        return await RunInteractiveAcceptanceAsync(
            args,
            psmux,
            stateStore,
            registry,
            providerProbes,
            psmuxProbe,
            cancellationToken).ConfigureAwait(false);
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

    private static List<AgentSessionSpec> ParseSessionSpecs(
        string value,
        string workingDirectory,
        ProviderRegistry providers)
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
                "opencode" or "open-code" => AgentProvider.OpenCode,
                "shell" or "pwsh" => AgentProvider.Shell,
                _ => throw new ArgumentException($"Unsupported provider '{parts[0]}'."),
            };
            result.Add(AgentSessionSpec.Create(
                provider,
                parts[1],
                workingDirectory,
                providers.Get(provider).InteractiveCommand,
                role: parts[1].Split('-', 2, StringSplitOptions.TrimEntries)[0]));
        }

        return result;
    }

    private static void PrintMission(Mission mission)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Id[/]", Markup.Escape(mission.Id.ToString("D")));
        grid.AddRow("[grey]State[/]", Markup.Escape(mission.State.ToString()));
        grid.AddRow("[grey]Resume state[/]", Markup.Escape(mission.ResumeState?.ToString() ?? "-"));
        grid.AddRow("[grey]Version[/]", mission.Version.ToString(CultureInfo.InvariantCulture));
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

    private static string[] SplitListOption(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    private static int PrintTopicHelp(string? topic)
    {
        switch (topic?.ToLowerInvariant())
        {
            case null:
                PrintHelp();
                return 0;
            case "desk":
                PrintDeskHelp();
                return 0;
            case "mission":
                PrintMissionHelp();
                return 0;
            case "first-run" or "quickstart":
                AnsiConsole.MarkupLine("[bold]First run[/]");
                AnsiConsole.MarkupLine("  1. [aqua]flyt doctor[/] — check prerequisites");
                AnsiConsole.MarkupLine("  2. [aqua]flyt desk configure --path .[/] — choose panes and providers");
                AnsiConsole.MarkupLine("  3. [aqua]flyt desk open --path . --template default[/] — enter the cockpit");
                AnsiConsole.MarkupLine("  4. [aqua]flyt doctor --interactive --template default[/] — prove the real Windows host");
                return 0;
            case "guide":
                var installedGuide = Path.Combine(AppContext.BaseDirectory, "docs", "operator-guide.md");
                if (File.Exists(installedGuide))
                {
                    AnsiConsole.MarkupLine($"Operator guide: [aqua]{Markup.Escape(installedGuide)}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        "Operator guide: [aqua]https://github.com/Pilvik1/FlytV1/blob/main/docs/operator-guide.md[/]");
                }

                return 0;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown help topic:[/] {Markup.Escape(topic)}");
                AnsiConsole.MarkupLine("Topics: [aqua]quickstart[/], [aqua]desk[/], [aqua]mission[/], [aqua]guide[/]");
                return 1;
        }
    }

    private static void PrintHelp()
    {
        AnsiConsole.Write(
            new Panel(
                """
                [bold]FIRST TIME[/]
                  [aqua]flyt help quickstart[/]              Four-step setup
                  [aqua]flyt doctor[/]                       Check prerequisites
                  [aqua]flyt desk configure[/]               Choose 1-10 panes and providers
                  [aqua]flyt doctor --interactive[/]         Prove the real Windows host

                [bold]DAILY WORK[/]
                  [aqua]flyt desk open[/]                    Enter or restore the cockpit
                  [aqua]flyt desk status[/]                  See agents needing attention
                  [aqua]flyt desk watch --notify[/]          Monitor without attaching

                [bold]MORE COMMANDS[/]
                  [aqua]flyt help desk[/]                    Desk operations and recovery
                  [aqua]flyt help mission[/]                 Bounded mission workflows
                  [aqua]flyt help guide[/]                   Locate the full operator guide
                  [aqua]flyt version[/]                      Installed version
                """)
                .Header("[bold]FlytV1[/]")
                .RoundedBorder());
    }

    private static void PrintDeskHelp()
    {
        AnsiConsole.MarkupLine("[bold]Open and observe[/]");
        AnsiConsole.MarkupLine(
            "[aqua]flyt desk start[/] [[--name NAME]] [[--path PATH]] "
            + "[[--template NAME|PATH]] [[--sessions claude:planner,codex:reviewer]] [[--dry-run]]");
        AnsiConsole.MarkupLine(
            "[aqua]flyt desk open[/] [[--name NAME]] [[--path PATH]] [[--template NAME|PATH]] [[--no-attach]] [[--allow-small-terminal]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk status|watch[/] [[filters]] [[--json|--notify]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk attach[/] --name NAME [[--allow-small-terminal]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Act on panes[/]");
        AnsiConsole.MarkupLine("[aqua]flyt desk focus[/] --name NAME [[--pane PANE|--next]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk restart-pane|logs[/] --name NAME --pane PANE");
        AnsiConsole.MarkupLine("[aqua]flyt desk send[/] --name N --text TEXT --pane|--role|--tag|--state|--all");
        AnsiConsole.MarkupLine("[aqua]flyt desk queue add[/] --name N --pane P --text TEXT [[--expensive]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk queue list|dispatch|cancel[/] [[options]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk handoff[/] --name N --from P --to P --goal G --action A [[--send]]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Recover and inspect[/]");
        AnsiConsole.MarkupLine("[aqua]flyt desk recover[/] --name NAME [[--apply]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk reopen[/] --name NAME");
        AnsiConsole.MarkupLine("[aqua]flyt desk failures[/] --name NAME [[--json]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk history[/] [[--name NAME]] [[--search TEXT]] [[--json]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk stop[/] --name NAME");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Setup and diagnostics[/]");
        AnsiConsole.MarkupLine("[aqua]flyt desk notify[/] [[--title TEXT]] [[--text TEXT]]");
        AnsiConsole.MarkupLine(
            "[aqua]flyt desk configure[/] [[--path PATH]] [[--panes 1-10]] "
            + "[[--providers claude,codex,opencode,shell]] [[--open]]");
        AnsiConsole.MarkupLine("[aqua]flyt desk template init[/] [[--path PATH]]");
        AnsiConsole.MarkupLine("[grey]Run 'flyt help guide' to locate the full operator guide.[/]");
    }

    private static void PrintMissionHelp()
    {
        AnsiConsole.MarkupLine(
            "[aqua]flyt mission create[/] --goal TEXT [[--path PATH]] [[--profile manual|bounded|auto]]");
        AnsiConsole.MarkupLine(
            "[aqua]flyt mission plan[/] --id ID --acceptance \"A|B\" --steps \"build|review|verify\" --verify COMMAND");
        AnsiConsole.MarkupLine("[aqua]flyt mission list[/]");
        AnsiConsole.MarkupLine("[aqua]flyt mission show[/] --id ID");
        AnsiConsole.MarkupLine("[aqua]flyt mission approve[/] --id ID");
        AnsiConsole.MarkupLine("[aqua]flyt mission pause|resume|cancel[/] --id ID");
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
