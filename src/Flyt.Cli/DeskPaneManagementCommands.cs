using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> AddDeskPaneAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var deskName = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var desk = await RequireRunningDeskAsync(psmux, store, deskName, cancellationToken).ConfigureAwait(false);
        if (desk.Panes.Count >= PsmuxLayoutPlanner.MaximumSessions)
        {
            throw new InvalidOperationException(
                $"Desk '{deskName}' already has the maximum of {PsmuxLayoutPlanner.MaximumSessions} panes.");
        }

        var interactive = !Console.IsInputRedirected && GetOption(args, "--provider") is null;
        var provider = GetOption(args, "--provider") is { } providerValue
            ? ParseConfiguredProvider(providerValue)
            : interactive
                ? AnsiConsole.Prompt(
                    new SelectionPrompt<AgentProvider>()
                        .Title("Provider for the new pane?")
                        .AddChoices(
                            AgentProvider.Claude,
                            AgentProvider.Codex,
                            AgentProvider.OpenCode,
                            AgentProvider.Shell))
                : throw new ArgumentException("Provide --provider when input is redirected.");
        var defaults = BuildConfiguredPanes([provider])[0];
        var suggestedName = UniquePaneName(defaults.Name, desk.Panes);
        var paneName = GetOption(args, "--pane")
            ?? (interactive
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>("Pane name?")
                        .DefaultValue(suggestedName)
                        .ValidationErrorMessage("[red]Pane names must be non-empty and unique.[/]")
                        .Validate(value => !string.IsNullOrWhiteSpace(value)
                                           && desk.Panes.All(existing => !string.Equals(
                                               existing.Name,
                                               value,
                                               StringComparison.OrdinalIgnoreCase))))
                : suggestedName);
        paneName = paneName.Trim();
        if (desk.Panes.Any(value => string.Equals(value.Name, paneName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Pane '{paneName}' already exists in Desk '{deskName}'.");
        }

        var role = GetOption(args, "--role")
            ?? (interactive ? AnsiConsole.Ask("Role?", defaults.Role) : defaults.Role);
        var task = GetOption(args, "--task")
            ?? (interactive ? AnsiConsole.Ask("Task label?", defaults.Task) : defaults.Task);
        var workingDirectoryValue = GetOption(args, "--path")
            ?? (interactive ? AnsiConsole.Ask("Working directory?", desk.WorkspacePath) : desk.WorkspacePath);
        var workingDirectory = ResolveWorkingDirectory(workingDirectoryValue, desk.WorkspacePath, []);
        var launchCommand = GetOption(args, "--command")
            ?? (provider == AgentProvider.Shell && interactive
                ? AnsiConsole.Ask("Shell command?", providers.BuildInteractiveCommand(provider))
                : providers.BuildInteractiveCommand(provider));
        var tags = GetOption(args, "--tags")?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [provider.ToString().ToLowerInvariant(), role];
        var session = AgentSessionSpec.Create(
            provider,
            paneName,
            workingDirectory,
            launchCommand,
            role,
            task,
            color: GetOption(args, "--color"),
            tags: tags);

        var handle = await psmux.AddPaneAsync(deskName, session, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var registeredPane = new DeskPane(
            Guid.NewGuid(),
            desk.Id,
            handle.PaneId,
            session.Provider,
            session.Name,
            session.WorkingDirectory,
            session.LaunchCommand,
            session.Role,
            session.Task,
            session.Color,
            session.Tags,
            session.ProviderSessionId,
            DeskPaneStatus.Starting,
            DeskStateSource.Operator,
            ProcessId: null,
            ExitCode: null,
            StateChangedAt: now,
            CreatedAt: now,
            UpdatedAt: now);

        Desk updated;
        try
        {
            updated = await store.AddDeskPaneAsync(
                deskName,
                registeredPane,
                PsmuxLayoutPlanner.LayoutNameForCount(desk.Panes.Count + 1),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception persistenceException)
        {
            try
            {
                await psmux.RemovePaneAsync(handle.PaneId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "The pane could not be registered and psmux rollback also failed.",
                    persistenceException,
                    cleanupException);
            }

            throw new InvalidOperationException(
                "The pane could not be registered; the psmux split was rolled back.",
                persistenceException);
        }

        await psmux.ConfigureRoleBorderFormatAsync(
            deskName,
            updated.Panes.Select(value => new DeskPaneHandle(
                value.PaneId,
                SessionFromPane(value, providers))).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (!HasFlag(args, "--no-focus"))
        {
            await psmux.FocusAsync(handle.PaneId, cancellationToken).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine(
            $"[green]Pane added:[/] {Markup.Escape(session.Name)} ({Markup.Escape(session.Provider.ToString())})");
        return 0;
    }

    private static async Task<int> RemoveDeskPaneAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var deskName = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var desk = await RequireRunningDeskAsync(psmux, store, deskName, cancellationToken).ConfigureAwait(false);
        if (desk.Panes.Count <= 1)
        {
            throw new InvalidOperationException(
                "The last pane cannot be removed. Stop the Desk if you want to close the entire session.");
        }

        var paneName = GetOption(args, "--pane");
        var paneId = GetOption(args, "--pane-id") ?? Environment.GetEnvironmentVariable("TMUX_PANE");
        var pane = paneName is not null
            ? desk.Panes.SingleOrDefault(value => string.Equals(
                value.Name, paneName, StringComparison.OrdinalIgnoreCase))
            : paneId is not null
                ? desk.Panes.SingleOrDefault(value => string.Equals(
                    value.PaneId, paneId, StringComparison.Ordinal))
                : null;
        if (pane is null)
        {
            throw new KeyNotFoundException(
                $"Pane '{paneName ?? paneId ?? "(unknown)"}' is not registered in Desk '{deskName}'.");
        }

        var activeWork = (await store.ListWorkItemsAsync(deskName, limit: 1000, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            .Count(value => string.Equals(value.PaneName, pane.Name, StringComparison.OrdinalIgnoreCase)
                            && value.Status is DeskWorkItemStatus.Queued or DeskWorkItemStatus.Dispatched);
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Closing [bold]{Markup.Escape(pane.Name)}[/] terminates its current "
            + "process and removes it from this Desk. Unsaved terminal work may be lost."
            + (activeWork == 0 ? string.Empty : $" {activeWork} queued or dispatched work item(s) will be cancelled."));
        if (!HasFlag(args, "--yes")
            && !AnsiConsole.Confirm("Close this pane?", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[grey]Pane kept open.[/]");
            return 2;
        }

        await psmux.RemovePaneAsync(pane.PaneId, cancellationToken).ConfigureAwait(false);
        var updated = await store.RemoveDeskPaneAsync(
            deskName,
            pane.Name,
            PsmuxLayoutPlanner.LayoutNameForCount(desk.Panes.Count - 1),
            cancellationToken).ConfigureAwait(false);
        await psmux.ApplyLayoutAsync(deskName, updated.Panes.Count, cancellationToken).ConfigureAwait(false);
        await psmux.ConfigureRoleBorderFormatAsync(
            deskName,
            updated.Panes.Select(value => new DeskPaneHandle(
                value.PaneId,
                SessionFromPane(value, providers))).ToArray(),
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Pane closed:[/] {Markup.Escape(pane.Name)}");
        return 0;
    }

    private static async Task<Desk> RequireRunningDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string deskName,
        CancellationToken cancellationToken)
    {
        var snapshot = await DeskRuntime.ReconcileAsync(
            psmux, store, applyStyles: true, captureFailures: true, cancellationToken).ConfigureAwait(false);
        var desk = snapshot.Desks.SingleOrDefault(value => string.Equals(
            value.SessionName, deskName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Desk '{deskName}' is not registered.");
        if (desk.Status != DeskStatus.Running)
        {
            throw new InvalidOperationException($"Desk '{deskName}' is not running. Open it before changing panes.");
        }

        return desk;
    }

    internal static string UniquePaneName(string baseName, IReadOnlyList<DeskPane> panes)
    {
        if (panes.All(value => !string.Equals(value.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (var suffix = 2; suffix <= PsmuxLayoutPlanner.MaximumSessions; suffix++)
        {
            var candidate = $"{baseName}-{suffix}";
            if (panes.All(value => !string.Equals(value.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No unique pane name is available for '{baseName}'.");
    }
}
