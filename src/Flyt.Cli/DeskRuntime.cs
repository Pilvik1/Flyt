using System.Diagnostics;
using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;

namespace Flyt.Cli;

internal sealed record DeskRuntimeSnapshot(
    IReadOnlyList<Desk> Desks,
    IReadOnlyList<string> ExternalSessions,
    IReadOnlyDictionary<string, PaneResourceSnapshot> Resources,
    IReadOnlyList<string> Warnings);

internal sealed record PaneResourceSnapshot(int? ProcessId, long? WorkingSetBytes)
{
    public string Display => WorkingSetBytes is null
        ? "-"
        : $"{WorkingSetBytes.Value / 1024d / 1024d:N0} MB";
}

internal static class DeskRuntime
{
    public static async Task<DeskRuntimeSnapshot> ReconcileAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        bool applyStyles,
        bool captureFailures,
        CancellationToken cancellationToken)
    {
        var liveSessions = await psmux.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        var liveSet = liveSessions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = await store.ListDesksAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = new List<Desk>();
        var resources = new Dictionary<string, PaneResourceSnapshot>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var desk in registered)
        {
            if (!liveSet.Contains(desk.SessionName))
            {
                reconciled.Add(await store.UpdateDeskRuntimeAsync(
                    desk.SessionName,
                    desk.Status == DeskStatus.Stopped ? DeskStatus.Stopped : DeskStatus.Missing,
                    Array.Empty<DeskPaneRuntime>(),
                    cancellationToken).ConfigureAwait(false));
                continue;
            }

            var previousByName = desk.Panes.ToDictionary(pane => pane.Name, StringComparer.OrdinalIgnoreCase);
            var snapshots = await psmux.ListPanesAsync(desk.SessionName, cancellationToken).ConfigureAwait(false);
            var runtimes = new List<DeskPaneRuntime>(snapshots.Count);

            foreach (var snapshot in snapshots)
            {
                var resource = ProbeResources(snapshot.ProcessId);
                resources[ResourceKey(desk.SessionName, snapshot.Name)] = resource;
                var runtime = ToRuntime(snapshot);
                if (runtime.Status == DeskPaneStatus.Running
                    && previousByName.TryGetValue(snapshot.Name, out var registeredPane)
                    && registeredPane.Provider != AgentProvider.Shell)
                {
                    try
                    {
                        var output = await psmux.CapturePaneAsync(snapshot.PaneId, 40, cancellationToken)
                            .ConfigureAwait(false);
                        if (ProviderAttentionDetector.Detect(registeredPane.Provider, output) is { } detected)
                        {
                            runtime = runtime with { Status = detected, Source = DeskStateSource.Provider };
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        warnings.Add($"{desk.SessionName}/{snapshot.Name}: attention probe failed: {exception.Message}");
                    }
                }

                runtimes.Add(runtime);
                if (applyStyles)
                {
                    if (previousByName.TryGetValue(snapshot.Name, out var presentation))
                    {
                        var presentationSession = AgentSessionSpec.Create(
                            presentation.Provider,
                            presentation.Name,
                            presentation.WorkingDirectory,
                            presentation.LaunchCommand,
                            presentation.Role,
                            presentation.Task,
                            presentation.Color,
                            presentation.Tags,
                            presentation.ProviderSessionId);
                        var expectedTitle = PsmuxLayoutPlanner.PaneTitle(presentationSession, runtime.Status);
                        if (!string.Equals(snapshot.Title, expectedTitle, StringComparison.Ordinal))
                        {
                            await psmux.SetPanePresentationAsync(
                                snapshot.PaneId,
                                presentationSession,
                                runtime.Status,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (captureFailures
                    && runtime.Status is DeskPaneStatus.Failed or DeskPaneStatus.Exited
                    && previousByName.TryGetValue(snapshot.Name, out var previous)
                    && previous.Status is not (DeskPaneStatus.Failed or DeskPaneStatus.Exited))
                {
                    await CaptureFailureAsync(
                        psmux,
                        store,
                        desk,
                        previous,
                        runtime,
                        resource,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var updated = await store.UpdateDeskRuntimeAsync(
                desk.SessionName,
                DeskStatus.Running,
                runtimes,
                cancellationToken).ConfigureAwait(false);
            reconciled.Add(updated);

            var memory = updated.Panes
                .Select(pane => resources.GetValueOrDefault(ResourceKey(updated.SessionName, pane.Name))?.WorkingSetBytes)
                .Where(value => value.HasValue)
                .Sum(value => value!.Value);
            if (memory > (long)updated.MemoryWarningMegabytes * 1024 * 1024)
            {
                warnings.Add(
                    $"{updated.SessionName}: tracked pane processes use {memory / 1024d / 1024d:N0} MB "
                    + $"(warning threshold {updated.MemoryWarningMegabytes:N0} MB).");
            }

            var expensive = updated.Panes.Count(pane =>
                pane.Tags.Contains("expensive", StringComparer.OrdinalIgnoreCase)
                && pane.Status == DeskPaneStatus.Working);
            if (expensive > updated.MaxConcurrentExpensiveTasks)
            {
                warnings.Add(
                    $"{updated.SessionName}: {expensive} expensive panes are active; "
                    + $"configured ceiling is {updated.MaxConcurrentExpensiveTasks}.");
            }
        }

        var external = liveSessions
            .Where(session => registered.All(desk => !string.Equals(
                desk.SessionName,
                session,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new DeskRuntimeSnapshot(reconciled, external, resources, warnings);
    }

    public static string ResourceKey(string deskName, string paneName) => $"{deskName}\u001f{paneName}";

    private static DeskPaneRuntime ToRuntime(PsmuxPaneSnapshot pane)
    {
        var status = PsmuxStatusMapper.Map(pane);
        var source = string.IsNullOrWhiteSpace(pane.ProgramState)
            ? DeskStateSource.Process
            : DeskStateSource.ProgramStatus;
        return new DeskPaneRuntime(
            pane.Name,
            pane.PaneId,
            status,
            source,
            pane.ProcessId,
            pane.ExitCode);
    }

    private static PaneResourceSnapshot ProbeResources(int? processId)
    {
        if (processId is null)
        {
            return new PaneResourceSnapshot(null, null);
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return new PaneResourceSnapshot(processId, process.WorkingSet64);
        }
        catch (ArgumentException)
        {
            return new PaneResourceSnapshot(processId, null);
        }
        catch (InvalidOperationException)
        {
            return new PaneResourceSnapshot(processId, null);
        }
    }

    private static async Task CaptureFailureAsync(
        PsmuxClient psmux,
        FlytStateStore store,
        Desk desk,
        DeskPane pane,
        DeskPaneRuntime runtime,
        PaneResourceSnapshot resources,
        CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await psmux.CapturePaneAsync(runtime.PaneId, 200, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            output = $"Log capture failed: {exception.Message}";
        }

        var reason = runtime.ExitCode is null
            ? "Pane exited without an exit code."
            : $"Pane exited with code {runtime.ExitCode}.";
        await store.AddFailureReceiptAsync(
            new DeskFailureReceipt(
                Guid.NewGuid(),
                desk.Id,
                pane.Id,
                pane.Name,
                pane.Provider,
                pane.WorkingDirectory,
                pane.LaunchCommand,
                runtime.ExitCode,
                reason,
                output,
                resources.WorkingSetBytes,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }
}
