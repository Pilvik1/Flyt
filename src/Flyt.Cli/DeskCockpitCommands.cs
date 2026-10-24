using System.Globalization;
using System.Text;
using System.Text.Json;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static Table BuildDeskDashboard(DeskRuntimeSnapshot snapshot, string[] args)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold aqua]Flyt Desk[/]")
            .AddColumn("Desk")
            .AddColumn("Role")
            .AddColumn("Pane")
            .AddColumn("State")
            .AddColumn("Task")
            .AddColumn("Provider")
            .AddColumn("Memory")
            .AddColumn("Tags");

        foreach (var desk in snapshot.Desks)
        {
            foreach (var pane in desk.Panes
                         .Where(value => MatchesFilters(desk, value, args))
                         .OrderBy(value => AttentionPriority(value.Status))
                         .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var roleColor = DeskPalette.NormalizeColor(pane.Color);
                var stateColor = DeskPalette.ForState(pane.Status);
                var resource = snapshot.Resources.GetValueOrDefault(
                    DeskRuntime.ResourceKey(desk.SessionName, pane.Name));
                table.AddRow(
                    Markup.Escape(desk.SessionName),
                    $"[{roleColor}]{Markup.Escape(pane.Role)}[/]",
                    Markup.Escape(pane.Name),
                    $"[{stateColor}]{DeskPalette.Glyph(pane.Status)} {Markup.Escape(StateLabel(pane.Status))}[/]",
                    Markup.Escape(pane.Task),
                    Markup.Escape(pane.Provider.ToString()),
                    Markup.Escape(resource?.Display ?? "-"),
                    Markup.Escape(string.Join(',', pane.Tags)));
            }
        }

        foreach (var session in snapshot.ExternalSessions)
        {
            table.AddRow(Markup.Escape(session), "-", "-", "[grey]? EXTERNAL[/]", "-", "-", "-", "-");
        }

        return table;
    }

    private static async Task<int> WatchDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var intervalValue = GetOption(args, "--interval") ?? "1000";
        if (!int.TryParse(intervalValue, NumberStyles.None, CultureInfo.InvariantCulture, out var interval)
            || interval is < 250 or > 60_000)
        {
            throw new ArgumentException("--interval must be between 250 and 60000 milliseconds.");
        }

        var snapshot = await DeskRuntime.ReconcileAsync(
            psmux, store, applyStyles: true, captureFailures: true, cancellationToken).ConfigureAwait(false);
        var previous = FlattenStates(snapshot);
        if (HasFlag(args, "--once"))
        {
            AnsiConsole.Write(BuildDeskDashboard(snapshot, args));
            PrintDeskWarnings(snapshot);
            return 0;
        }

        await AnsiConsole.Live(BuildDeskDashboard(snapshot, args))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async context =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    snapshot = await DeskRuntime.ReconcileAsync(
                        psmux, store, applyStyles: true, captureFailures: true, cancellationToken)
                        .ConfigureAwait(false);
                    if (HasFlag(args, "--notify"))
                    {
                        NotifyTransitions(previous, snapshot);
                    }

                    previous = FlattenStates(snapshot);
                    context.UpdateTarget(BuildDeskDashboard(snapshot, args));
                    context.Refresh();
                }
            }).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SendDeskTextAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var deskName = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var text = GetOption(args, "--text");
        if (text is null && Console.IsInputRedirected)
        {
            text = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Provide --text TEXT or pipe text to stdin.");
        }

        var snapshot = await DeskRuntime.ReconcileAsync(
            psmux, store, applyStyles: false, captureFailures: true, cancellationToken).ConfigureAwait(false);
        var desk = snapshot.Desks.SingleOrDefault(value => string.Equals(
            value.SessionName, deskName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Desk '{deskName}' is not registered.");
        var targets = SelectTargets(desk, args).ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidOperationException("The selector matched no runnable panes.");
        }

        AnsiConsole.MarkupLine(
            $"[aqua]Recipients:[/] {Markup.Escape(string.Join(", ", targets.Select(value => value.Name)))}");
        if (HasFlag(args, "--dry-run"))
        {
            return 0;
        }

        if (targets.Length > 1 && !HasFlag(args, "--yes"))
        {
            AnsiConsole.MarkupLine("[yellow]Multi-pane send requires --yes after reviewing recipients.[/]");
            return 2;
        }

        foreach (var target in targets)
        {
            await psmux.SendTextAsync(
                target.PaneId,
                text,
                submit: !HasFlag(args, "--no-submit"),
                cancellationToken).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine($"[green]Sent to {targets.Length} pane(s).[/]");
        return 0;
    }

    private static async Task<int> RecoverDeskAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        ProviderRegistry providers,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var snapshot = await DeskRuntime.ReconcileAsync(
            psmux, store, applyStyles: true, captureFailures: true, cancellationToken).ConfigureAwait(false);
        var desk = snapshot.Desks.SingleOrDefault(value => string.Equals(
            value.SessionName, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Desk '{name}' is not registered.");
        var candidates = desk.Panes.Where(value => value.Status is
            DeskPaneStatus.Failed or DeskPaneStatus.Exited or DeskPaneStatus.Missing).ToArray();

        if (candidates.Length == 0)
        {
            AnsiConsole.MarkupLine("[green]Desk is healthy; nothing requires recovery.[/]");
            return 0;
        }

        foreach (var pane in candidates)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{Markup.Escape(pane.Name)}[/] {Markup.Escape(pane.Status.ToString())} -> resume/restart");
        }

        if (!HasFlag(args, "--apply"))
        {
            AnsiConsole.MarkupLine("[grey]Preview only. Re-run with --apply to recover.[/]");
            return 2;
        }

        if (desk.Status == DeskStatus.Missing)
        {
            var sessions = desk.Panes.Select(pane => SessionFromPane(pane, providers)).ToArray();
            var started = await psmux.StartDeskAsync(desk.SessionName, sessions, cancellationToken)
                .ConfigureAwait(false);
            var byName = started.Panes.ToDictionary(value => value.Session.Name, StringComparer.OrdinalIgnoreCase);
            await store.SaveDeskAsync(desk with
            {
                Status = DeskStatus.Running,
                Panes = desk.Panes.Select(pane => pane with
                {
                    PaneId = byName[pane.Name].PaneId,
                    Status = DeskPaneStatus.Starting,
                    StatusSource = DeskStateSource.Operator,
                    ExitCode = null,
                    StateChangedAt = DateTimeOffset.UtcNow,
                }).ToArray(),
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var pane in candidates)
            {
                var session = SessionFromPane(pane, providers);
                if (pane.Status == DeskPaneStatus.Missing)
                {
                    await psmux.AddPaneAsync(desk.SessionName, session, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await psmux.RestartPaneAsync(pane.PaneId, session, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await DeskRuntime.ReconcileAsync(
            psmux, store, applyStyles: true, captureFailures: false, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Recovered {candidates.Length} pane(s).[/]");
        return 0;
    }

    private static async Task<int> ShowFailureReceiptsAsync(
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var name = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var receipts = await store.ListFailureReceiptsAsync(name, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (HasFlag(args, "--json"))
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(receipts, JsonOptions));
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .Title("[bold red]Failure receipts[/]")
            .AddColumn("Time").AddColumn("Pane").AddColumn("Exit").AddColumn("Reason").AddColumn("Last output");
        foreach (var receipt in receipts)
        {
            var lastLine = receipt.RecentOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? "-";
            table.AddRow(
                Markup.Escape(receipt.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Markup.Escape(receipt.PaneName),
                receipt.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-",
                Markup.Escape(receipt.Reason),
                Markup.Escape(lastLine.Length > 80 ? lastLine[..80] : lastLine));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static async Task<int> RunDeskTemplateAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[aqua]flyt desk template init[/] [[--path PATH]]");
            return 0;
        }

        var path = Path.GetFullPath(GetOption(args[1..], "--path") ?? Environment.CurrentDirectory);
        var written = await DeskTemplateLoader.WriteStarterAsync(path, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]Desk template created:[/] {Markup.Escape(written)}");
        return 0;
    }

    private static async Task<int> CreateDeskHandoffAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        string[] args,
        CancellationToken cancellationToken)
    {
        var deskName = PsmuxLayoutPlanner.NormalizeSessionName(RequireOption(args, "--name"));
        var from = RequireOption(args, "--from");
        var to = RequireOption(args, "--to");
        var goal = RequireOption(args, "--goal");
        var requestedAction = RequireOption(args, "--action");
        var desk = await store.GetDeskAsync(deskName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Desk '{deskName}' is not registered.");
        _ = desk.Panes.Single(value => string.Equals(value.Name, from, StringComparison.OrdinalIgnoreCase));
        var target = desk.Panes.Single(value => string.Equals(value.Name, to, StringComparison.OrdinalIgnoreCase));

        var branchResult = await ProbeCommandAsync(
            "git", ["-C", desk.WorkspacePath, "branch", "--show-current"], cancellationToken).ConfigureAwait(false);
        var changedResult = await ProbeCommandAsync(
            "git", ["-C", desk.WorkspacePath, "status", "--porcelain=v1"], cancellationToken).ConfigureAwait(false);
        var diffResult = await ProbeCommandAsync(
            "git", ["-C", desk.WorkspacePath, "diff", "--binary", "HEAD"], cancellationToken).ConfigureAwait(false);

        var directory = Path.Combine(desk.WorkspacePath, ".flyt", "handoffs");
        Directory.CreateDirectory(directory);
        var timestamp = DateTimeOffset.UtcNow;
        var stem = $"{timestamp:yyyyMMdd-HHmmss}-{Slug(from)}-to-{Slug(to)}";
        string? diffPath = null;
        if (diffResult.IsSuccess && !string.IsNullOrWhiteSpace(diffResult.StandardOutput))
        {
            diffPath = Path.Combine(directory, $"{stem}.patch");
            await File.WriteAllTextAsync(diffPath, diffResult.StandardOutput, cancellationToken).ConfigureAwait(false);
        }

        var handoff = new DeskHandoff(
            Guid.NewGuid(), deskName, from, to, goal, desk.WorkspacePath,
            branchResult.IsSuccess ? branchResult.StandardOutput.Trim() : null,
            diffPath,
            changedResult.IsSuccess
                ? changedResult.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Length > 3 ? value[3..] : value)
                    .ToArray()
                : [],
            SplitOptional(args, "--decisions"),
            SplitOptional(args, "--verification"),
            SplitOptional(args, "--blockers"),
            requestedAction,
            timestamp);
        var jsonPath = Path.Combine(directory, $"{stem}.json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(handoff, JsonOptions) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);

        if (HasFlag(args, "--send"))
        {
            var message = $"Structured handoff from {from}. Goal: {goal}. Requested action: {requestedAction}. "
                + $"Read {jsonPath}" + (diffPath is null ? "." : $" and patch {diffPath}.");
            await psmux.SendTextAsync(target.PaneId, message, submit: true, cancellationToken)
                .ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine($"[green]Handoff created:[/] {Markup.Escape(jsonPath)}");
        return 0;
    }

    private static AgentSessionSpec SessionFromPane(DeskPane pane, ProviderRegistry providers) =>
        AgentSessionSpec.Create(
            pane.Provider,
            pane.Name,
            pane.WorkingDirectory,
            pane.ProviderSessionId is null
                ? pane.LaunchCommand
                : providers.BuildInteractiveCommand(pane.Provider, pane.ProviderSessionId),
            pane.Role,
            pane.Task,
            pane.Color,
            pane.Tags,
            pane.ProviderSessionId);

    private static IEnumerable<DeskPane> SelectTargets(Desk desk, string[] args)
    {
        var pane = GetOption(args, "--pane");
        var role = GetOption(args, "--role");
        var tag = GetOption(args, "--tag");
        var state = GetOption(args, "--state");
        var all = HasFlag(args, "--all");
        if (pane is null && role is null && tag is null && state is null && !all)
        {
            throw new ArgumentException("Select recipients with --pane, --role, --tag, --state, or --all.");
        }

        return desk.Panes.Where(value =>
                value.Status is not (DeskPaneStatus.Missing or DeskPaneStatus.Stopped or DeskPaneStatus.Exited)
                && (HasFlag(args, "--include-shell") || value.Provider != AgentProvider.Shell)
                && (all
                    || string.Equals(value.Name, pane, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value.Role, role, StringComparison.OrdinalIgnoreCase)
                    || (tag is not null && value.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    || string.Equals(value.Status.ToString(), state, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(value => value.Id);
    }

    private static bool MatchesFilters(Desk desk, DeskPane pane, string[] args)
    {
        var name = GetOption(args, "--name");
        var role = GetOption(args, "--role");
        var tag = GetOption(args, "--tag");
        var state = GetOption(args, "--state");
        var search = GetOption(args, "--search");
        return (name is null || string.Equals(desk.SessionName, name, StringComparison.OrdinalIgnoreCase))
            && (role is null || string.Equals(pane.Role, role, StringComparison.OrdinalIgnoreCase))
            && (tag is null || pane.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            && (state is null || string.Equals(pane.Status.ToString(), state, StringComparison.OrdinalIgnoreCase))
            && (search is null || new[] { desk.SessionName, pane.Name, pane.Role, pane.Task, pane.WorkingDirectory }
                .Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private static Dictionary<string, DeskPaneStatus> FlattenStates(DeskRuntimeSnapshot snapshot) =>
        snapshot.Desks.SelectMany(desk => desk.Panes.Select(pane => new
        {
            Key = DeskRuntime.ResourceKey(desk.SessionName, pane.Name),
            pane.Status,
        }))
            .ToDictionary(value => value.Key, value => value.Status, StringComparer.OrdinalIgnoreCase);

    private static void NotifyTransitions(
        IReadOnlyDictionary<string, DeskPaneStatus> previous,
        DeskRuntimeSnapshot current)
    {
        foreach (var desk in current.Desks)
        {
            foreach (var pane in desk.Panes)
            {
                var key = DeskRuntime.ResourceKey(desk.SessionName, pane.Name);
                if (pane.Status is not (DeskPaneStatus.NeedsInput or DeskPaneStatus.Done or DeskPaneStatus.Failed)
                    || previous.GetValueOrDefault(key) == pane.Status)
                {
                    continue;
                }

                var message = $"Flyt: {desk.SessionName}/{pane.Name} {StateLabel(pane.Status)}";
                Console.Write($"\u001b]9;{SanitizeNotification(message)}\u001b\\\a");
            }
        }
    }

    private static void PrintDeskWarnings(DeskRuntimeSnapshot snapshot)
    {
        foreach (var warning in snapshot.Warnings)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(warning)}");
        }
    }

    private static int AttentionPriority(DeskPaneStatus status) => status switch
    {
        DeskPaneStatus.NeedsInput => 0,
        DeskPaneStatus.Failed => 1,
        DeskPaneStatus.Done => 2,
        DeskPaneStatus.Working => 3,
        DeskPaneStatus.Running => 4,
        _ => 5,
    };

    private static string StateLabel(DeskPaneStatus status) => status switch
    {
        DeskPaneStatus.NeedsInput => "NEEDS INPUT",
        _ => status.ToString().ToUpperInvariant(),
    };

    private static string SanitizeNotification(string value) =>
        new(value.Where(character => !char.IsControl(character)).Take(180).ToArray());

    private static string Slug(string value) =>
        new(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    private static string[] SplitOptional(string[] args, string option) =>
        GetOption(args, option) is { } value ? SplitListOption(value) : [];
}
