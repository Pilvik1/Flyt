using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;
using Spectre.Console;

namespace Flyt.Cli;

internal static partial class Program
{
    private static async Task<int> RunHomeAsync(CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var hasTemplate = File.Exists(Path.Combine(workspace, ".flyt", "desk.json"));
        IReadOnlyList<Desk> desks = [];

        if (File.Exists(StatePaths.DatabasePath))
        {
            var store = CreateStateStore();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            desks = await store.ListDesksAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var psmux = new PsmuxClient(
                    CommandExecutor,
                    presentation: PsmuxPresentationProfile.FromEnvironment());
                if ((await psmux.ProbeAsync(cancellationToken).ConfigureAwait(false)).IsSuccess)
                {
                    desks = (await DeskRuntime.ReconcileAsync(
                        psmux,
                        store,
                        applyStyles: false,
                        captureFailures: false,
                        cancellationToken).ConfigureAwait(false)).Desks;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The home screen stays useful with last-known state when psmux is unavailable.
            }
        }

        var home = FlytHomeExperience.Evaluate(workspace, hasTemplate, desks);
        var content = new Rows(
            new Markup($"[grey]Workspace[/]  {Markup.Escape(home.WorkspacePath)}"),
            Text.Empty,
            BuildHomePrimaryAction(home),
            Text.Empty,
            new Markup("[grey]Help[/]       [aqua]flyt help quickstart[/] · [aqua]flyt help desk[/] · [aqua]flyt help guide[/]"));

        AnsiConsole.Write(new Panel(content).Header("[bold]FlytV1[/]").RoundedBorder());
        return 0;
    }

    private static Markup BuildHomePrimaryAction(FlytHomeExperience home)
    {
        if (home.Desk is null && !home.HasTemplate)
        {
            return new Markup(
                "[bold]START HERE[/]\n"
                + "  [aqua]flyt desk configure --path .[/]  Choose 1-10 panes and their providers\n"
                + "  [grey]Flyt saves the template, previews the layout, and can open it immediately.[/]");
        }

        if (home.Desk is null)
        {
            return new Markup(
                "[bold]READY TO OPEN[/]\n"
                + "  [aqua]flyt desk open --path . --template default[/]  Start this repository's cockpit");
        }

        var desk = home.Desk;
        var escapedName = Markup.Escape(desk.SessionName);
        var attention = desk.Panes.Count(pane => pane.Status is
            DeskPaneStatus.NeedsInput or DeskPaneStatus.Failed or DeskPaneStatus.Done);
        var summary = $"  [grey]{escapedName} · {Markup.Escape(desk.Status.ToString())} · "
                      + $"{desk.Panes.Count} panes · {attention} attention[/]";

        return desk.Status switch
        {
            DeskStatus.Running => new Markup(
                "[bold green]CURRENT DESK[/]\n"
                + $"{summary}\n"
                + $"  [aqua]flyt desk open --name {escapedName}[/]  Enter the cockpit\n"
                + $"  [aqua]flyt desk focus --name {escapedName} --next[/]  Jump to attention"),
            DeskStatus.Stopped => new Markup(
                "[bold yellow]DESK STOPPED[/]\n"
                + $"{summary}\n"
                + $"  [aqua]flyt desk open --name {escapedName}[/]  Reopen and restore focus"),
            DeskStatus.Missing => new Markup(
                "[bold yellow]DESK NEEDS RECOVERY[/]\n"
                + $"{summary}\n"
                + $"  [aqua]flyt desk recover --name {escapedName}[/]  Preview recovery\n"
                + $"  [grey]Apply after review: flyt desk recover --name {escapedName} --apply[/]"),
            _ => throw new InvalidOperationException($"Unsupported Desk status: {desk.Status}"),
        };
    }
}

internal sealed record FlytHomeExperience(
    string WorkspacePath,
    bool HasTemplate,
    Desk? Desk)
{
    public static FlytHomeExperience Evaluate(
        string workspacePath,
        bool hasTemplate,
        IReadOnlyList<Desk> desks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(desks);

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var desk = desks.FirstOrDefault(candidate => string.Equals(
            Path.GetFullPath(candidate.WorkspacePath),
            fullWorkspacePath,
            comparison));

        return new FlytHomeExperience(fullWorkspacePath, hasTemplate, desk);
    }
}
