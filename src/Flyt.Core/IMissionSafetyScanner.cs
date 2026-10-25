namespace Flyt.Core;

public sealed record MissionSafetyFinding(string Path, string Reason);

public interface IMissionSafetyScanner
{
    Task<IReadOnlyList<MissionSafetyFinding>> ScanAsync(
        MissionWorkspace workspace,
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken = default);
}
