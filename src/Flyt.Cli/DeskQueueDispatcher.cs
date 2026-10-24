using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;

namespace Flyt.Cli;

internal sealed record DeskDispatchResult(
    int Completed,
    int Failed,
    int Dispatched,
    int Remaining,
    IReadOnlyList<string> Warnings);

internal static class DeskQueueDispatcher
{
    public static async Task<DeskDispatchResult> DispatchAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        DeskRuntimeSnapshot snapshot,
        string? sessionName,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        var failed = 0;
        var dispatched = 0;
        var remaining = 0;
        var warnings = new List<string>();

        foreach (var desk in snapshot.Desks.Where(value => sessionName is null
                     || string.Equals(value.SessionName, sessionName, StringComparison.OrdinalIgnoreCase)))
        {
            var items = await store.ListWorkItemsAsync(
                desk.SessionName,
                limit: 1000,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var active = new List<DeskWorkItem>();
            foreach (var item in items.Where(value => value.Status == DeskWorkItemStatus.Dispatched))
            {
                var pane = desk.Panes.SingleOrDefault(value => string.Equals(
                    value.Name,
                    item.PaneName,
                    StringComparison.OrdinalIgnoreCase));
                if (pane is null || pane.Status is DeskPaneStatus.Failed or DeskPaneStatus.Exited
                    or DeskPaneStatus.Missing or DeskPaneStatus.Stopped)
                {
                    await store.TransitionWorkItemAsync(
                        item.Id,
                        DeskWorkItemStatus.Failed,
                        pane is null ? "Target pane disappeared." : $"Target pane entered {pane.Status}.",
                        cancellationToken).ConfigureAwait(false);
                    failed++;
                    continue;
                }

                if (item.DispatchedAt is not null
                    && pane.StateChangedAt > item.DispatchedAt
                    && pane.Status == DeskPaneStatus.Done)
                {
                    await store.TransitionWorkItemAsync(
                        item.Id,
                        DeskWorkItemStatus.Completed,
                        "Target pane reported Done after dispatch.",
                        cancellationToken).ConfigureAwait(false);
                    completed++;
                    continue;
                }

                active.Add(item);
            }

            var busyPanes = active.Select(value => value.PaneName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var activeExpensive = active.Count(value => value.Expensive);
            foreach (var pane in desk.Panes.Where(value =>
                         value.Tags.Contains("expensive", StringComparer.OrdinalIgnoreCase)
                         && value.Status == DeskPaneStatus.Working
                         && busyPanes.Add(value.Name)))
            {
                activeExpensive++;
            }

            var memoryBytes = desk.Panes
                .Select(pane => snapshot.Resources.GetValueOrDefault(
                    DeskRuntime.ResourceKey(desk.SessionName, pane.Name))?.WorkingSetBytes)
                .Where(value => value.HasValue)
                .Sum(value => value!.Value);
            var memoryGateOpen = memoryBytes <= (long)desk.MemoryWarningMegabytes * 1024 * 1024;

            foreach (var item in items.Where(value => value.Status == DeskWorkItemStatus.Queued))
            {
                var pane = desk.Panes.SingleOrDefault(value => string.Equals(
                    value.Name,
                    item.PaneName,
                    StringComparison.OrdinalIgnoreCase));
                if (pane is null
                    || pane.Status is DeskPaneStatus.Starting or DeskPaneStatus.Working or DeskPaneStatus.NeedsInput
                        or DeskPaneStatus.Failed or DeskPaneStatus.Exited or DeskPaneStatus.Missing
                        or DeskPaneStatus.Stopped
                    || busyPanes.Contains(item.PaneName))
                {
                    remaining++;
                    continue;
                }

                if (item.Expensive && (!memoryGateOpen || activeExpensive >= desk.MaxConcurrentExpensiveTasks))
                {
                    remaining++;
                    continue;
                }

                var claimed = await store.TransitionWorkItemAsync(
                    item.Id,
                    DeskWorkItemStatus.Dispatched,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                try
                {
                    await psmux.SendTextAsync(pane.PaneId, item.Payload, submit: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await store.TransitionWorkItemAsync(
                        claimed.Id,
                        DeskWorkItemStatus.Failed,
                        $"Dispatch failed: {exception.Message}",
                        cancellationToken).ConfigureAwait(false);
                    warnings.Add($"{desk.SessionName}/{pane.Name}: queued work dispatch failed: {exception.Message}");
                    failed++;
                    continue;
                }

                busyPanes.Add(pane.Name);
                if (item.Expensive)
                {
                    activeExpensive++;
                }

                dispatched++;
            }
        }

        return new DeskDispatchResult(completed, failed, dispatched, remaining, warnings);
    }
}
