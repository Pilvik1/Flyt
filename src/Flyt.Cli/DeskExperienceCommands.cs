using System.Globalization;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> OpenDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = GetOption(args, "--name");
        if (name is null && GetOption(args, "--template") is { } templateReference)
        {
            var path = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
            var (template, _) = await DeskTemplateLoader.LoadAsync(templateReference, path, cancellationToken)
                .ConfigureAwait(false);
            name = PsmuxLayoutPlanner.NormalizeSessionName(template.Name);
        }

        if (name is null)
        {
            var requestedPath = Path.GetFullPath(GetOption(args, "--path") ?? Environment.CurrentDirectory);
            var recent = await store.ListDesksAsync(cancellationToken).ConfigureAwait(false);
            name = GetOption(args, "--sessions") is not null
                ? null
                : recent.FirstOrDefault(value => string.Equals(
                    Path.GetFullPath(value.WorkspacePath),
                    requestedPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))?.SessionName;
        }
        else
        {
            name = PsmuxLayoutPlanner.NormalizeSessionName(name);
        }
        name ??= $"flyt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        var desk = await store.GetDeskAsync(name, cancellationToken).ConfigureAwait(false);
        if (desk is null)
        {
            var startArguments = args
                .Where(value => !string.Equals(value, "--notify", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (GetOption(args, "--name") is null)
            {
                startArguments.AddRange(["--name", name]);
            }

            var started = await StartDeskAsync(
                psmux,
                store,
                providers,
                startArguments.ToArray(),
                cancellationToken).ConfigureAwait(false);
            if (started != 0)
            {
                return started;
            }
        }
        else
        {
            var snapshot = await DeskRuntime.ReconcileAsync(
                psmux, store, applyStyles: true, captureFailures: true, cancellationToken).ConfigureAwait(false);
            desk = snapshot.Desks.Single(value => string.Equals(
                value.SessionName, name, StringComparison.OrdinalIgnoreCase));
            if (desk.Status is DeskStatus.Missing or DeskStatus.Stopped)
            {
                var reopened = await ReopenDeskAsync(
                    psmux,
                    store,
                    providers,
                    ["--name", name],
                    cancellationToken).ConfigureAwait(false);
                if (reopened != 0)
                {
                    return reopened;
                }
            }
        }

        desk = await store.GetDeskAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Desk '{name}' disappeared before attach.");
        await EnsureInteractiveCompatibilityAsync(
            psmux,
            desk.Panes.Count,
            HasFlag(args, "--allow-small-terminal") || HasFlag(args, "--no-attach"),
            cancellationToken).ConfigureAwait(false);
        await psmux.ConfigureDeskPresentationAsync(name, cancellationToken).ConfigureAwait(false);
        await psmux.ConfigureRoleBorderFormatAsync(
            name,
            desk.Panes.Select(value => new DeskPaneHandle(
                value.PaneId,
                SessionFromPane(value, providers))).ToArray(),
            cancellationToken).ConfigureAwait(false);
        await psmux.ConfigureDeskControlsAsync(name, cancellationToken).ConfigureAwait(false);
        await psmux.ApplyLayoutAsync(name, desk.Panes.Count, cancellationToken).ConfigureAwait(false);
        if (desk.FocusedPaneName is not null
            && desk.Panes.SingleOrDefault(value => string.Equals(
                value.Name, desk.FocusedPaneName, StringComparison.OrdinalIgnoreCase)) is { } focused)
        {
            await psmux.FocusAsync(focused.PaneId, cancellationToken).ConfigureAwait(false);
        }

        var workspaceLabel = await GetWorkspaceLabelAsync(desk, cancellationToken).ConfigureAwait(false);
        if (HasFlag(args, "--no-attach"))
        {
            var snapshot = await DeskRuntime.ReconcileAsync(
                psmux, store, applyStyles: true, captureFailures: true, cancellationToken).ConfigureAwait(false);
            var dispatch = await DeskQueueDispatcher.DispatchAsync(
                psmux, store, snapshot, name, cancellationToken).ConfigureAwait(false);
            await UpdateIntegratedChromeAsync(
                psmux, snapshot, dispatch, name, workspaceLabel, cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[green]Desk cockpit ready:[/] {Markup.Escape(name)}");
            return 0;
        }

        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = MonitorOpenDeskAsync(
            psmux,
            store,
            name,
            workspaceLabel,
            monitorCancellation.Token);
        try
        {
            return await psmux.AttachAsync(name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await monitorCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the attached psmux client detaches.
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await DeskRuntime.ReconcileAsync(
                    psmux, store, applyStyles: true, captureFailures: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task MonitorOpenDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string deskName,
        string workspaceLabel,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await DeskRuntime.ReconcileAsync(
                    psmux, store, applyStyles: true, captureFailures: true, cancellationToken)
                    .ConfigureAwait(false);
                var dispatch = await DeskQueueDispatcher.DispatchAsync(
                    psmux, store, snapshot, deskName, cancellationToken).ConfigureAwait(false);
                await UpdateIntegratedChromeAsync(
                    psmux, snapshot, dispatch, deskName, workspaceLabel, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var desk = await store.GetDeskAsync(deskName, CancellationToken.None).ConfigureAwait(false);
                if (desk is not null)
                {
                    await store.AddDeskEventAsync(
                        desk.Id,
                        "cockpit.monitor.failed",
                        paneName: null,
                        exception.Message,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpdateIntegratedChromeAsync(
        PsmuxClient psmux,
        DeskRuntimeSnapshot snapshot,
        DeskDispatchResult dispatch,
        string deskName,
        string workspaceLabel,
        CancellationToken cancellationToken)
    {
        var desk = snapshot.Desks.SingleOrDefault(value => string.Equals(
            value.SessionName, deskName, StringComparison.OrdinalIgnoreCase));
        if (desk is null || desk.Status != DeskStatus.Running)
        {
            return;
        }

        var working = desk.Panes.Count(value => value.Status is DeskPaneStatus.Working or DeskPaneStatus.Running);
        var waiting = desk.Panes.Count(value => value.Status == DeskPaneStatus.NeedsInput);
        var memoryBytes = desk.Panes
            .Select(value => snapshot.Resources.GetValueOrDefault(
                DeskRuntime.ResourceKey(desk.SessionName, value.Name))?.WorkingSetBytes)
            .Where(value => value.HasValue)
            .Sum(value => value!.Value);
        var memory = memoryBytes == 0 ? "RAM -" : $"RAM {memoryBytes / 1024d / 1024d / 1024d:N1} GB";
        var summary = $"{desk.Panes.Count} agents · {working} working · {waiting} waiting · queue {dispatch.Remaining} · {memory}";
        var activeStatus = desk.Panes.SingleOrDefault(value => string.Equals(
            value.Name, desk.FocusedPaneName, StringComparison.OrdinalIgnoreCase))?.Status
            ?? DeskPaneStatus.Running;
        await psmux.UpdateDeskChromeAsync(
            desk.SessionName,
            workspaceLabel,
            summary,
            activeStatus,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> GetWorkspaceLabelAsync(Desk desk, CancellationToken cancellationToken)
    {
        var repository = new DirectoryInfo(desk.WorkspacePath).Name;
        var branch = await ProbeCommandAsync(
            "git", ["-C", desk.WorkspacePath, "branch", "--show-current"], cancellationToken).ConfigureAwait(false);
        var branchName = branch.IsSuccess ? branch.StandardOutput.Trim() : string.Empty;
        return string.IsNullOrWhiteSpace(branchName) ? repository : $"{repository}/{branchName}";
    }

    private static async Task<int> RunDeskActionAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var action = RequireOption(args, "--action").ToLowerInvariant();
        var snapshot = await DeskRuntime.ReconcileAsync(
            psmux, store, applyStyles: true, captureFailures: true, cancellationToken).ConfigureAwait(false);
        var desk = snapshot.Desks.SingleOrDefault(value => string.Equals(
            value.SessionName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Desk '{name}' is not registered.");
        var paneId = GetOption(args, "--pane-id") ?? Environment.GetEnvironmentVariable("TMUX_PANE");
        var pane = paneId is null
            ? null
            : desk.Panes.SingleOrDefault(value => string.Equals(value.PaneId, paneId, StringComparison.Ordinal));

        switch (action)
        {
            case "menu":
                var selected = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold aqua]Flyt Desk[/] action")
                        .PageSize(12)
                        .AddChoices(
                            "Overview",
                            "Jump to pane",
                            "Add pane",
                            "Close pane",
                            "Recent logs",
                            "Send text",
                            "Queue work",
                            "Restart pane",
                            "Create handoff",
                            "Queue status",
                            "Failure receipts",
                            "Session history"));
                var nextAction = selected switch
                {
                    "Overview" => "overview",
                    "Jump to pane" => "jump",
                    "Add pane" => "add-pane",
                    "Close pane" => "remove-pane",
                    "Recent logs" => "logs",
                    "Send text" => "send",
                    "Queue work" => "queue",
                    "Restart pane" => "restart",
                    "Create handoff" => "handoff",
                    "Queue status" => "queue-view",
                    "Failure receipts" => "failures",
                    "Session history" => "history",
                    _ => throw new InvalidOperationException($"Unknown action menu choice '{selected}'."),
                };
                var nextArguments = new List<string>
                {
                    "--name", name,
                    "--action", nextAction,
                };
                if (paneId is not null)
                {
                    nextArguments.AddRange(["--pane-id", paneId]);
                }

                return await RunDeskActionAsync(
                    psmux, store, providers, nextArguments.ToArray(), cancellationToken).ConfigureAwait(false);

            case "add-pane":
                return await AddDeskPaneAsync(
                    psmux, store, providers, ["--name", name], cancellationToken).ConfigureAwait(false);

            case "remove-pane":
                pane = RequireActionPane(pane, paneId);
                return await RemoveDeskPaneAsync(
                    psmux,
                    store,
                    providers,
                    ["--name", name, "--pane", pane.Name],
                    cancellationToken).ConfigureAwait(false);

            case "overview":
                var dispatch = await DeskQueueDispatcher.DispatchAsync(
                    psmux, store, snapshot, name, cancellationToken).ConfigureAwait(false);
                AnsiConsole.Write(BuildDeskDashboard(snapshot, ["--name", name], dispatch));
                PrintDeskWarnings(snapshot);
                return 0;

            case "queue-view":
                return await RunDeskQueueAsync(
                    psmux, store, ["list", "--name", name, "--limit", "100"], cancellationToken)
                    .ConfigureAwait(false);

            case "failures":
                return await ShowFailureReceiptsAsync(
                    store, ["--name", name], cancellationToken).ConfigureAwait(false);

            case "history":
                var historySearch = GetOption(args, "--search");
                if (historySearch is null && !HasFlag(args, "--all"))
                {
                    historySearch = AnsiConsole.Prompt(
                        new TextPrompt<string>("History search (blank for recent):").AllowEmpty());
                }

                var historyArguments = new List<string> { "--name", name, "--limit", "100" };
                if (!string.IsNullOrWhiteSpace(historySearch))
                {
                    historyArguments.AddRange(["--search", historySearch]);
                }

                return await ShowDeskHistoryAsync(
                    store, historyArguments.ToArray(), cancellationToken).ConfigureAwait(false);

            case "jump":
                var jumpSearch = GetOption(args, "--search");
                if (jumpSearch is null && !HasFlag(args, "--all"))
                {
                    jumpSearch = AnsiConsole.Prompt(
                        new TextPrompt<string>("Filter by pane, role, task, tag, or state (blank for all):").AllowEmpty());
                }

                var jumpCandidates = desk.Panes.Where(value => string.IsNullOrWhiteSpace(jumpSearch)
                    || new[]
                    {
                        value.Name,
                        value.Role,
                        value.Task,
                        value.Status.ToString(),
                        string.Join(',', value.Tags),
                    }.Any(field => field.Contains(jumpSearch, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (jumpCandidates.Length == 0)
                {
                    throw new InvalidOperationException($"No panes match '{jumpSearch}'.");
                }

                var jumpTarget = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Jump to pane:")
                        .AddChoices(jumpCandidates.Select(value =>
                            $"{value.Name} · {StateLabel(value.Status)} · {value.Task}")));
                var targetName = jumpTarget.Split('·', 2, StringSplitOptions.TrimEntries)[0];
                var targetPane = desk.Panes.Single(value => string.Equals(
                    value.Name, targetName, StringComparison.OrdinalIgnoreCase));
                await psmux.FocusAsync(targetPane.PaneId, cancellationToken).ConfigureAwait(false);
                return 0;

            case "logs":
                pane = RequireActionPane(pane, paneId);
                var output = await psmux.CapturePaneAsync(
                    pane.PaneId,
                    ParseBoundedIntOption(args, "--lines", 160, 1, 10000),
                    cancellationToken).ConfigureAwait(false);
                AnsiConsole.Write(new Panel(Markup.Escape(output)).Header($"[bold]{Markup.Escape(pane.Name)} logs[/]"));
                return 0;

            case "restart":
                pane = RequireActionPane(pane, paneId);
                if (!HasFlag(args, "--yes")
                    && !AnsiConsole.Confirm($"Restart [yellow]{Markup.Escape(pane.Name)}[/]?", defaultValue: false))
                {
                    return 2;
                }

                await psmux.RestartPaneAsync(pane.PaneId, SessionFromPane(pane, providers), cancellationToken)
                    .ConfigureAwait(false);
                await store.AddDeskEventAsync(
                    desk.Id, "pane.restarted", pane.Name, "Restarted from cockpit action.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return 0;

            case "send":
                pane = RequireActionPane(pane, paneId);
                var text = GetOption(args, "--text") ?? AnsiConsole.Ask<string>(
                    $"Text for [aqua]{Markup.Escape(pane.Name)}[/]:");
                AnsiConsole.Write(new Panel(Markup.Escape(text)).Header("[bold]Send preview[/]"));
                if (!HasFlag(args, "--yes") && !AnsiConsole.Confirm("Send and submit?", defaultValue: true))
                {
                    return 2;
                }

                await psmux.SendTextAsync(pane.PaneId, text, submit: true, cancellationToken)
                    .ConfigureAwait(false);
                await store.AddDeskEventAsync(
                    desk.Id, "pane.sent", pane.Name, $"Cockpit sent {text.Length} characters.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return 0;

            case "queue":
                pane = RequireActionPane(pane, paneId);
                var payload = GetOption(args, "--text") ?? AnsiConsole.Ask<string>(
                    $"Queue work for [aqua]{Markup.Escape(pane.Name)}[/]:");
                var expensive = HasFlag(args, "--expensive")
                    || (!HasFlag(args, "--normal") && AnsiConsole.Confirm("Treat as expensive work?", defaultValue: false));
                var item = await store.EnqueueWorkItemAsync(
                    desk.SessionName, pane.Name, payload, expensive, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Queued:[/] {item.Id.ToString("N", CultureInfo.InvariantCulture)[..8]}");
                return 0;

            case "handoff":
                pane = RequireActionPane(pane, paneId);
                var candidates = desk.Panes.Where(value => !string.Equals(
                    value.Name, pane.Name, StringComparison.OrdinalIgnoreCase)).Select(value => value.Name).ToArray();
                if (candidates.Length == 0)
                {
                    throw new InvalidOperationException("A handoff requires at least two panes.");
                }

                var target = GetOption(args, "--to") ?? AnsiConsole.Prompt(
                    new SelectionPrompt<string>().Title("Handoff target:").AddChoices(candidates));
                var goal = GetOption(args, "--goal") ?? AnsiConsole.Ask<string>("Goal:");
                var requestedAction = GetOption(args, "--requested-action")
                    ?? AnsiConsole.Ask<string>("Requested action:");
                return await CreateDeskHandoffAsync(
                    psmux,
                    store,
                    [
                        "--name", desk.SessionName,
                        "--from", pane.Name,
                        "--to", target,
                        "--goal", goal,
                        "--action", requestedAction,
                        "--send",
                    ],
                    cancellationToken).ConfigureAwait(false);

            default:
                throw new ArgumentException($"Unknown Desk action '{action}'.");
        }
    }

    private static DeskPane RequireActionPane(DeskPane? pane, string? paneId) => pane
        ?? throw new KeyNotFoundException($"Pane '{paneId ?? "(unknown)"}' is not registered in this Desk.");
}
