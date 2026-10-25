namespace Flyt.Core;

public interface IMissionWorkspaceManager
{
    Task<MissionWorkspace> EnsureBuilderWorktreeAsync(
        Mission mission,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetChangedPathsAsync(
        MissionWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task<string> GetHeadCommitAsync(
        MissionWorkspace workspace,
        CancellationToken cancellationToken = default);

    Task<string> ComputeDiffFingerprintAsync(
        MissionWorkspace workspace,
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken = default);
}
