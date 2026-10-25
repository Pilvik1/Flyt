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

        var psmux = new PsmuxClient(CommandExecutor, presentation: PsmuxPresentationProfile.FromEnvironment());
        var store = CreateStateStore();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var providers = ProviderRegistry.CreateDefault();
        return args[0].ToLowerInvariant() switch
        {
            "start" => await StartDeskAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "open" => await OpenDeskAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "action" => await RunDeskActionAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "status" => await ShowDeskStatusAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "watch" => await WatchDeskAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "send" => await SendDeskTextAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "queue" => await RunDeskQueueAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "recover" => await RecoverDeskAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "reopen" => await ReopenDeskAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "failures" => await ShowFailureReceiptsAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "history" => await ShowDeskHistoryAsync(store, args[1..], cancellationToken).ConfigureAwait(false),
            "notify" => await SendTestNotificationAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "configure" => await ConfigureDeskAsync(
                psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "template" => await RunDeskTemplateAsync(args[1..], cancellationToken).ConfigureAwait(false),
            "handoff" => await CreateDeskHandoffAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "attach" => await AttachDeskAsync(psmux, args[1..], cancellationToken).ConfigureAwait(false),
            "add-pane" => await AddDeskPaneAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "remove-pane" or "close-pane" => await RemoveDeskPaneAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
            "focus" => await FocusDeskPaneAsync(psmux, store, args[1..], cancellationToken).ConfigureAwait(false),
            "restart-pane" => await RestartDeskPaneAsync(psmux, store, providers, args[1..], cancellationToken).ConfigureAwait(false),
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

        var templateReference = GetOption(args, "--template");
        DeskTemplate? template = null;
        IReadOnlyList<AgentSessionSpec> sessions;
        if (templateReference is not null)
        {
            (template, _) = await DeskTemplateLoader.LoadAsync(
                templateReference,
                path,
                cancellationToken).ConfigureAwait(false);
            sessions = DeskTemplateLoader.BuildSessions(template, path, providers);
        }
        else
        {
            var sessionValue = GetOption(args, "--sessions")
                ?? "claude:planner,claude:builder,codex:reviewer";
            sessions = ParseSessionSpecs(sessionValue, path, providers);
        }

        var name = GetOption(args, "--name")
            ?? (template is null ? $"flyt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}" : template.Name);
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

        EnsurePsmuxCompatibility(probe, sessions.Count);

        var started = await psmux.StartDeskAsync(name, sessions, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var deskId = Guid.NewGuid();
        var desk = new Desk(
            deskId,
            started.SessionName,
            path,
            template?.Name,
            template?.Tags ?? [],
            template?.MaxConcurrentExpensiveTasks ?? 2,
            template?.MemoryWarningMegabytes ?? 12_000,
            DeskStatus.Running,
            started.Panes.Select(pane => new DeskPane(
                Guid.NewGuid(),
                deskId,
                pane.PaneId,
                pane.Session.Provider,
                pane.Session.Name,
                pane.Session.WorkingDirectory,
                pane.Session.LaunchCommand,
                pane.Session.Role,
                pane.Session.Task,
                pane.Session.Color,
                pane.Session.Tags,
                pane.Session.ProviderSessionId,
                DeskPaneStatus.Starting,
                DeskStateSource.Registered,
                ProcessId: null,
                ExitCode: null,
                StateChangedAt: now,
                now,
                now)).ToArray(),
            now,
            now,
            now,
            PsmuxLayoutPlanner.LayoutNameForCount(started.Panes.Count),
            started.Panes[0].Session.Name);

        try
        {
            await psmux.ConfigureDeskControlsAsync(started.SessionName, cancellationToken).ConfigureAwait(false);
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
                    "Desk initialization failed and psmux cleanup also failed.",
                    persistenceException,
                    cleanupException);
            }

            throw new InvalidOperationException(
                "Desk initialization failed; the psmux session was rolled back.",
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
        var snapshot = await DeskRuntime.ReconcileAsync(
            psmux,
            store,
            applyStyles: true,
            captureFailures: true,
            cancellationToken).ConfigureAwait(false);

        if (HasFlag(args, "--json"))
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(
                new
                {
                    registered = snapshot.Desks,
                    externalSessions = snapshot.ExternalSessions,
                    resources = snapshot.Resources,
                    warnings = snapshot.Warnings,
                },
                JsonOptions));
            return 0;
        }

        AnsiConsole.Write(BuildDeskDashboard(snapshot, args));
        PrintDeskWarnings(snapshot);
        return 0;
    }

    private static async Task<int> AttachDeskAsync(
        PsmuxClient psmux,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = RequireOption(args, "--name");
        var panes = await psmux.ListPanesAsync(name, cancellationToken).ConfigureAwait(false);
        await EnsureInteractiveCompatibilityAsync(
            psmux,
            panes.Count,
            HasFlag(args, "--allow-small-terminal"),
            cancellationToken).ConfigureAwait(false);
        return await psmux.AttachAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsurePsmuxCompatibility(CommandResult probe, int paneCount)
    {
        var report = TerminalCompatibility.Evaluate(
            FirstNonEmpty(probe.StandardOutput, probe.StandardError),
            paneCount,
            viewport: null);
        if (!report.IsSupported)
        {
            throw new InvalidOperationException(string.Join(" ", report.Errors));
        }
    }

    private static async Task EnsureInteractiveCompatibilityAsync(
        PsmuxClient psmux,
        int paneCount,
        bool allowSmallTerminal,
        CancellationToken cancellationToken)
    {
        var probe = await psmux.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!probe.IsSuccess)
        {
            throw new InvalidOperationException(
                $"psmux is unavailable: {FirstNonEmpty(probe.StandardError, probe.StandardOutput)}");
        }

        var report = TerminalCompatibility.Evaluate(
            FirstNonEmpty(probe.StandardOutput, probe.StandardError),
            paneCount,
            TerminalCompatibility.TryGetCurrentViewport());
        var viewportErrors = report.Errors.Where(value => value.Contains("viewport", StringComparison.OrdinalIgnoreCase)).ToArray();
        var blockingErrors = report.Errors.Except(viewportErrors).ToArray();
        if (blockingErrors.Length > 0)
        {
            throw new InvalidOperationException(string.Join(" ", blockingErrors));
        }

        if (viewportErrors.Length > 0 && !allowSmallTerminal)
        {
            throw new InvalidOperationException(
                string.Join(" ", viewportErrors)
                + " Resize the terminal or rerun with --allow-small-terminal to override.");
        }

        foreach (var warning in report.Warnings.Concat(allowSmallTerminal ? viewportErrors : []))
        {
            AnsiConsole.MarkupLine($"[yellow]Compatibility warning:[/] {Markup.Escape(warning)}");
        }
    }

    private static async Task<int> FocusDeskPaneAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        DeskPane pane;
        if (HasFlag(args, "--next"))
        {
            var name = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
            var desk = await store.GetDeskAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Desk '{name}' is not registered.");
            pane = desk.Panes
                .Where(value => value.Status is not (DeskPaneStatus.Missing or DeskPaneStatus.Stopped))
                .OrderBy(value => value.Status switch
                {
                    DeskPaneStatus.NeedsInput => 0,
                    DeskPaneStatus.Failed => 1,
                    DeskPaneStatus.Done => 2,
                    _ => 3,
                })
                .ThenBy(value => value.UpdatedAt)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("No focusable pane was found.");
        }
        else
        {
            pane = await RequireRegisteredPaneAsync(store, args, cancellationToken).ConfigureAwait(false);
        }
        await psmux.FocusAsync(pane.PaneId, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Focused pane:[/] {Markup.Escape(pane.Name)}");
        return 0;
    }

    private static async Task<int> RestartDeskPaneAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var pane = await RequireRegisteredPaneAsync(store, args, cancellationToken).ConfigureAwait(false);
        var session = SessionFromPane(pane, providers);
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
        if (pane.Status is DeskPaneStatus.Missing or DeskPaneStatus.Stopped)
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
