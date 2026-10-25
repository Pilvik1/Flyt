using Flyt.Core;
using Flyt.Terminal;

namespace Flyt.Tests;

public sealed class GitWorktreeManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flyt-worktree-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureBuilderWorktree_IsIsolatedAndIdempotent()
    {
        var repository = Path.Combine(_directory, "source repo");
        var worktreeRoot = Path.Combine(_directory, "mission worktrees");
        Directory.CreateDirectory(repository);
        await GitAsync(repository, "init", "-b", "main");
        await GitAsync(repository, "config", "user.name", "Flyt Tests");
        await GitAsync(repository, "config", "user.email", "flyt-tests@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Test\n");
        await GitAsync(repository, "add", "README.md");
        await GitAsync(repository, "commit", "-m", "Initial");

        var mission = Mission.Create("Build", repository);
        var manager = new GitWorktreeManager(new SystemCommandExecutor(), worktreeRoot);

        var created = await manager.EnsureBuilderWorktreeAsync(mission);
        var reopened = await manager.EnsureBuilderWorktreeAsync(mission);

        Assert.Equal(created.WorktreePath, reopened.WorktreePath);
        Assert.Equal(created.BranchName, reopened.BranchName);
        Assert.Equal(created.BaseCommit, reopened.BaseCommit);
        Assert.StartsWith(Path.GetFullPath(worktreeRoot), created.WorktreePath, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(created.WorktreePath, "README.md")));

        await File.WriteAllTextAsync(Path.Combine(created.WorktreePath, "change.txt"), "changed\n");
        var changedPaths = await manager.GetChangedPathsAsync(created);

        Assert.Equal(["change.txt"], changedPaths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await new SystemCommandExecutor().RunAsync(
            new CommandInvocation("git", arguments, workingDirectory));
        Assert.True(result.IsSuccess, result.StandardError);
    }
}
