using System.Globalization;
using System.Text.Json;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> RunDeskAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintDeskHelp();
            return 0;
        }

        var psmux = new PsmuxClient(CommandExecutor);
        var store = CreateStateStore();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var providers = ProviderRegistry.CreateDefault();
        return args[0].ToLowerInvariant() switch
        {
            "start" => await StartDeskAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "status" => await ShowDeskStatusAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "attach" => await AttachDeskAsync(psmux, args[1..], cancellationToken).ConfigureAwait(false),
            "focus" => await FocusDeskPaneAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "restart-pane" => await RestartDeskPaneAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "logs" => await ShowDeskPaneLogsAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "stop" => await StopDeskAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand($"desk {args[0]}"),
        };
    }

    private static async Task<int> StartDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
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
        var sessions = ParseSessionSpecs(sessionValue, path, providers);
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

        var started = await psmux.StartDeskAsync(name, sessions, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        var desk = new Desk(
            deskId,
            started.SessionName,
            path,
            DeskStatus.Running,
            started.Panes.Select(pane => new DeskPane(
                Guid.NewGuid(),
                deskId,
                pane.PaneId,
                pane.Session.Provider,
                pane.Session.Name,
                pane.Session.WorkingDirectory,
                pane.Session.LaunchCommand,
                DeskPaneStatus.Running,
                now,
                now)).ToArray(),
            now,
            now,
            now);

        try
        {
            await store.SaveDeskAsync(desk, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception persistenceException)
        {
            try
            {
                await psmux.StopAsync(started.SessionName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Desk started but registry persistence and psmux cleanup both failed.",
                    persistenceException,
                    cleanupException);
            }

            throw new InvalidOperationException(
                "Desk registry persistence failed; the psmux session was rolled back.",
                persistenceException);
        }

        AnsiConsole.MarkupLine($"[green]Desk started:[/] {Markup.Escape(started.SessionName)}");
        AnsiConsole.MarkupLine($"Attach with: [aqua]flyt desk attach --name {Markup.Escape(started.SessionName)}[/]");
        return 0;
    }

    private static async Task<int> ShowDeskStatusAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var liveSessions = await psmux.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var liveSet = liveSessions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = await store.ListDesksAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = new List<Desk>();

        foreach (var desk in registered)
        {
            if (!liveSet.Contains(desk.SessionName))
            {
                reconciled.Add(await store.UpdateDeskRuntimeAsync(
                    desk.SessionName,
                    desk.Status == DeskStatus.Stopped ? DeskStatus.Stopped : DeskStatus.Missing,
                    new Dictionary<string, string>(),
                    cancellationToken).ConfigureAwait(false));
                continue;
            }

            var panes = await psmux.ListPanesAsync(desk.SessionName, cancellationToken).ConfigureAwait(false);
            var byName = panes
                .Where(pane => !string.IsNullOrWhiteSpace(pane.Title))
                .GroupBy(pane => pane.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().PaneId, StringComparer.OrdinalIgnoreCase);
            reconciled.Add(await store.UpdateDeskRuntimeAsync(
                desk.SessionName,
                DeskStatus.Running,
                byName,
                cancellationToken).ConfigureAwait(false));
        }

        var external = liveSessions
            .Where(session => registered.All(desk => !string.Equals(
                desk.SessionName,
                session,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (HasFlag(args, "--json"))
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(
                new
                {
                    registered = reconciled,
                    externalSessions = external,
                },
                JsonOptions));
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Desk")
            .AddColumn("State")
            .AddColumn("Panes")
            .AddColumn("Workspace");
        foreach (var desk in reconciled)
        {
            var running = desk.Panes.Count(pane => pane.Status == DeskPaneStatus.Running);
            table.AddRow(
                Markup.Escape(desk.SessionName),
                Markup.Escape(desk.Status.ToString()),
                $"{running}/{desk.Panes.Count}",
                Markup.Escape(desk.WorkspacePath));
        }

        foreach (var session in external)
        {
            table.AddRow(Markup.Escape(session), "External", "?", "-");
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

    private static async Task<int> FocusDeskPaneAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var pane = await RequireRegisteredPaneAsync(store, args, cancellationToken).ConfigureAwait(false);
        await psmux.FocusAsync(pane.PaneId, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Focused pane:[/] {Markup.Escape(pane.Name)}");
        return 0;
    }

    private static async Task<int> RestartDeskPaneAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var pane = await RequireRegisteredPaneAsync(store, args, cancellationToken).ConfigureAwait(false);
        var session = AgentSessionSpec.Create(
            pane.Provider,
            pane.Name,
            pane.WorkingDirectory,
            pane.LaunchCommand);
        await psmux.RestartPaneAsync(pane.PaneId, session, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Restarted pane:[/] {Markup.Escape(pane.Name)}");
        return 0;
    }

    private static async Task<int> ShowDeskPaneLogsAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var pane = await RequireRegisteredPaneAsync(store, args, cancellationToken).ConfigureAwait(false);
        var linesValue = GetOption(args, "--lines") ?? "200";
        if (!int.TryParse(linesValue, NumberStyles.None, CultureInfo.InvariantCulture, out var lines))
        {
            throw new ArgumentException($"'{linesValue}' is not a valid line count.");
        }

        var output = await psmux.CapturePaneAsync(pane.PaneId, lines, cancellationToken).ConfigureAwait(false);
        AnsiConsole.WriteLine(output);
        return 0;
    }

    private static async Task<DeskPane> RequireRegisteredPaneAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var paneName = RequireOption(args, "--pane");
        var desk = await store.GetDeskAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Desk '{name}' is not registered.");
        var pane = desk.Panes.SingleOrDefault(value => string.Equals(
            value.Name,
            paneName,
            StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Pane '{paneName}' is not registered in desk '{name}'.");
        if (pane.Status != DeskPaneStatus.Running)
        {
            throw new InvalidOperationException(
                $"Pane '{paneName}' is {pane.Status}; run 'flyt desk status' to reconcile first.");
        }

        return pane;
    }

    private static async Task<int> StopDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = RequireOption(args, "--name");
        await psmux.StopAsync(name, cancellationToken).ConfigureAwait(false);
        var registered = await store.GetDeskAsync(
            PsmuxLayoutPlanner.NormalizeSessionName(name),
            cancellationToken).ConfigureAwait(false);
        if (registered is not null)
        {
            await store.UpdateDeskRuntimeAsync(
                registered.SessionName,
                DeskStatus.Stopped,
                new Dictionary<string, string>(),
                cancellationToken).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine($"[green]Desk stopped:[/] {Markup.Escape(name)}");
        return 0;
    }
}
