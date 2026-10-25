using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> ConfigureDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new DirectoryNotFoundException(workspace);
        }

        var interactive = !Console.IsInputRedirected && GetOption(args, "--providers") is null;
        var providerList = GetOption(args, "--providers") is { } rawProviders
            ? ParseConfiguredProviders(rawProviders)
            : [];
        var paneCount = GetOption(args, "--panes") is null
            ? providerList.Count > 0
                ? providerList.Count
                : interactive
                    ? AnsiConsole.Prompt(
                        new TextPrompt<int>("How many panes [grey](1-10)[/]?")
                            .DefaultValue(3)
                            .ValidationErrorMessage("[red]Choose between 1 and 10 panes.[/]")
                            .Validate(value => value is >= 1 and <= PsmuxLayoutPlanner.MaximumSessions))
                    : throw new ArgumentException("Provide --panes and --providers when input is redirected.")
            : ParseBoundedIntOption(
                args, "--panes", 3, 1, PsmuxLayoutPlanner.MaximumSessions);

        if (providerList.Count > 0 && providerList.Count != paneCount)
        {
            throw new ArgumentException(
                $"--providers contains {providerList.Count} values but --panes is {paneCount}.");
        }

        var panes = interactive
            ? PromptForPanes(paneCount)
            : BuildConfiguredPanes(providerList);
        var templateName = GetOption(args, "--name")
            ?? (interactive
                ? AnsiConsole.Ask("Desk name?", "full-review")
                : "configured-desk");
        var template = new DeskTemplate(templateName, panes, Tags: ["configured"]);
        template.Validate();

        PrintConfigurationPreview(template);
        var target = Path.Combine(workspace, ".flyt", "desk.json");
        var overwrite = HasFlag(args, "--force");
        if (File.Exists(target) && !overwrite && interactive)
        {
            overwrite = AnsiConsole.Confirm($"Replace {Markup.Escape(target)}?", defaultValue: false);
            if (!overwrite)
            {
                AnsiConsole.MarkupLine("[yellow]Configuration cancelled; the existing template was not changed.[/]");
                return 2;
            }
        }

        var written = await DeskTemplateLoader.WriteAsync(
            template, workspace, overwrite, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Desk configuration saved:[/] {Markup.Escape(written)}");

        var shouldOpen = HasFlag(args, "--open")
                         || (interactive && AnsiConsole.Confirm("Open this Desk now?", defaultValue: true));
        if (!shouldOpen)
        {
            AnsiConsole.MarkupLine("Open it with: [aqua]flyt desk open --path . --template default[/]");
            return 0;
        }

        return await OpenDeskAsync(
            psmux,
            store,
            providers,
            ["--path", workspace, "--template", written],
            cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<AgentProvider> ParseConfiguredProviders(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseConfiguredProvider)
            .ToArray();
    }

    internal static AgentProvider ParseConfiguredProvider(string value) => value.Trim().ToLowerInvariant() switch
    {
        "claude" => AgentProvider.Claude,
        "codex" => AgentProvider.Codex,
        "opencode" or "open-code" => AgentProvider.OpenCode,
        "shell" or "pwsh" or "powershell" => AgentProvider.Shell,
        _ => throw new ArgumentException(
            $"Unsupported provider '{value}'. Choose claude, codex, opencode, or shell."),
    };

    internal static IReadOnlyList<DeskTemplatePane> BuildConfiguredPanes(
        IReadOnlyList<AgentProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count is < 1 or > PsmuxLayoutPlanner.MaximumSessions)
        {
            throw new ArgumentOutOfRangeException(nameof(providers), "Choose between one and ten providers.");
        }

        var counts = new Dictionary<AgentProvider, int>();
        return providers.Select((provider, index) =>
        {
            counts.TryGetValue(provider, out var seen);
            counts[provider] = ++seen;
            var providerName = provider.ToString().ToLowerInvariant();
            var name = providers.Count(value => value == provider) == 1
                ? providerName
                : $"{providerName}-{seen}";
            var role = provider switch
            {
                AgentProvider.Claude => "builder",
                AgentProvider.Codex => "reviewer",
                AgentProvider.OpenCode => "builder",
                AgentProvider.Shell => "ops",
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
            return new DeskTemplatePane(
                provider,
                name,
                role,
                $"{role} workspace {index + 1}",
                Tags: [providerName, role]);
        }).ToArray();
    }

    private static List<DeskTemplatePane> PromptForPanes(int paneCount)
    {
        var panes = new List<DeskTemplatePane>(paneCount);
        for (var index = 0; index < paneCount; index++)
        {
            AnsiConsole.MarkupLine($"\n[bold aqua]Pane {index + 1} of {paneCount}[/]");
            var provider = AnsiConsole.Prompt(
                new SelectionPrompt<AgentProvider>()
                    .Title("Provider?")
                    .AddChoices(
                        AgentProvider.Claude,
                        AgentProvider.Codex,
                        AgentProvider.OpenCode,
                        AgentProvider.Shell));
            var defaults = BuildConfiguredPanes([provider])[0];
            var suggestedName = panes.Any(value => value.Provider == provider)
                ? $"{defaults.Name}-{panes.Count(value => value.Provider == provider) + 1}"
                : defaults.Name;
            var name = AnsiConsole.Prompt(
                new TextPrompt<string>("Pane name?")
                    .DefaultValue(suggestedName)
                    .ValidationErrorMessage("[red]Pane names must be non-empty and unique.[/]")
                    .Validate(value => !string.IsNullOrWhiteSpace(value)
                                       && panes.All(existing => !string.Equals(
                                           existing.Name,
                                           value,
                                           StringComparison.OrdinalIgnoreCase))));
            var role = AnsiConsole.Ask("Role?", defaults.Role);
            var task = AnsiConsole.Ask("Task label?", defaults.Task);
            var command = provider == AgentProvider.Shell
                ? AnsiConsole.Ask("Shell command?", "pwsh")
                : null;
            panes.Add(new DeskTemplatePane(
                provider,
                name,
                role,
                task,
                Command: command,
                Tags: [provider.ToString().ToLowerInvariant(), role]));
        }

        return panes;
    }

    private static void PrintConfigurationPreview(DeskTemplate template)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold aqua]{Markup.Escape(template.Name)}[/]")
            .AddColumn("#")
            .AddColumn("Provider")
            .AddColumn("Pane")
            .AddColumn("Role")
            .AddColumn("Task");
        for (var index = 0; index < template.Panes.Count; index++)
        {
            var pane = template.Panes[index];
            table.AddRow(
                (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(pane.Provider.ToString()),
                Markup.Escape(pane.Name),
                Markup.Escape(pane.Role),
                Markup.Escape(pane.Task));
        }

        AnsiConsole.Write(table);
        var requirement = TerminalCompatibility.RequirementFor(template.Panes.Count);
        AnsiConsole.MarkupLine(
            $"[grey]Recommended viewport: {requirement.MinimumWidth}x{requirement.MinimumHeight}. "
            + "Use --allow-small-terminal when opening only if you accept cramped panes.[/]");
    }
}
