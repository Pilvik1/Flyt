using Flyt.Core;

namespace Flyt.Terminal;

public sealed class GitWorktreeManager(ICommandExecutor executor, string worktreeRoot) : IMissionWorkspaceManager
{
    private readonly string _worktreeRoot = Path.GetFullPath(
        string.IsNullOrWhiteSpace(worktreeRoot)
            ? throw new ArgumentException("A worktree root is required.", nameof(worktreeRoot))
            : worktreeRoot);

    public async Task<MissionWorkspace> EnsureBuilderWorktreeAsync(
        Mission mission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mission);

        var repositoryRoot = Path.GetFullPath(await RequireGitValueAsync(
            mission.WorkspacePath,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false));
        var baseCommit = await RequireGitValueAsync(
            repositoryRoot,
            ["rev-parse", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        var missionName = mission.Id.ToString("N");
        var branchName = $"flyt/mission-{missionName[..12]}/builder";
        var worktreePath = Path.GetFullPath(Path.Combine(_worktreeRoot, missionName, "builder"));
        EnsureBelowRoot(worktreePath);

        if (Directory.Exists(worktreePath))
        {
            return await ReadExistingAsync(
                mission.Id,
                repositoryRoot,
                worktreePath,
                branchName,
                baseCommit,
                cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        var branchProbe = await executor.RunAsync(
            Git(repositoryRoot, "show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"),
            cancellationToken).ConfigureAwait(false);
        var arguments = branchProbe.IsSuccess
            ? new[] { "worktree", "add", worktreePath, branchName }
            : new[] { "worktree", "add", "-b", branchName, worktreePath, baseCommit };
        var created = await executor.RunAsync(
            new CommandInvocation("git", arguments, repositoryRoot),
            cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Git could not create the Mission worktree: {Diagnostic(created)}");
        }

        var headCommit = await RequireGitValueAsync(
            worktreePath,
            ["rev-parse", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return new MissionWorkspace(
            mission.Id,
            repositoryRoot,
            worktreePath,
            branchName,
            baseCommit,
            headCommit,
            now,
            now);
    }

    public async Task<IReadOnlyList<string>> GetChangedPathsAsync(
        MissionWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var result = await executor.RunAsync(
            Git(workspace.WorktreePath, "status", "--porcelain=v1", "--untracked-files=all"),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Git status failed: {Diagnostic(result)}");
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Length > 3 ? line[3..] : line)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<string> GetHeadCommitAsync(
        MissionWorkspace workspace,
        CancellationToken cancellationToken = default) =>
        await RequireGitValueAsync(
            workspace.WorktreePath,
            ["rev-parse", "HEAD"],
            cancellationToken).ConfigureAwait(false);

    private async Task<MissionWorkspace> ReadExistingAsync(
        Guid missionId,
        string repositoryRoot,
        string worktreePath,
        string branchName,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        var existingRoot = Path.GetFullPath(await RequireGitValueAsync(
            worktreePath,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false));
        if (!PathsEqual(existingRoot, worktreePath))
        {
            throw new InvalidOperationException(
                $"Existing Mission path is not the expected Git worktree: {worktreePath}");
        }

        var existingBranch = await RequireGitValueAsync(
            worktreePath,
            ["branch", "--show-current"],
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(existingBranch, branchName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Mission worktree uses unexpected branch '{existingBranch}'.");
        }

        var headCommit = await RequireGitValueAsync(
            worktreePath,
            ["rev-parse", "HEAD"],
            cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        return new MissionWorkspace(
            missionId,
            repositoryRoot,
            worktreePath,
            branchName,
            baseCommit,
            headCommit,
            now,
            now);
    }

    private async Task<string> RequireGitValueAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await executor.RunAsync(
            new CommandInvocation("git", arguments, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException($"Git command failed: {Diagnostic(result)}");
        }

        return result.StandardOutput.Trim();
    }

    private static CommandInvocation Git(string workingDirectory, params string[] arguments) =>
        new("git", arguments, workingDirectory);

    private static string Diagnostic(CommandResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {result.ExitCode}"
            : SensitiveDataRedactor.Redact(result.StandardError.Trim());

    private void EnsureBelowRoot(string path)
    {
        var relative = Path.GetRelativePath(_worktreeRoot, path);
        if (relative == "."
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException("Mission worktree path escaped the configured root.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
