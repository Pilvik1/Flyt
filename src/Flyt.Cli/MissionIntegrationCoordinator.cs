using Flyt.Core;
using Flyt.Persistence;
using Flyt.Terminal;

namespace Flyt.Cli;

internal sealed record MissionIntegrationResult(
    Mission Mission,
    string Commit,
    bool Pushed,
    string? PullRequestUrl);

internal sealed class MissionIntegrationCoordinator(
    FlytStateStore store,
    IMissionWorkspaceManager workspaceManager,
    IMissionSafetyScanner safetyScanner,
    ICommandExecutor executor)
{
    public async Task<MissionIntegrationResult> PrepareAsync(
        Guid missionId,
        string commitMessage,
        bool push,
        bool createDraftPullRequest,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        if (createDraftPullRequest && !push)
        {
            throw new ArgumentException("Draft pull request creation requires --push.");
        }

        var approved = await store.RequireApprovedMissionPlanAsync(
            missionId,
            MissionState.Integrating,
            cancellationToken).ConfigureAwait(false);
        var workspace = await store.GetMissionWorkspaceAsync(missionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mission integration requires a persisted workspace.");
        var attempt = await store.GetLatestMissionAttemptAsync(missionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mission integration requires a persisted attempt.");
        var receipt = await store.GetLatestMissionVerificationReceiptAsync(missionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mission integration requires verification evidence.");
        if (!receipt.Passed || receipt.AttemptId != attempt.Id)
        {
            throw new InvalidOperationException("Latest builder attempt does not have passing verification evidence.");
        }

        if (attempt.PlanVersion != approved.Plan.Version
            || !string.Equals(
                attempt.PlanFingerprint,
                approved.Plan.ComputeFingerprint(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Latest builder attempt does not match the approved plan.");
        }

        var changedPaths = await workspaceManager.GetChangedPathsAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        if (!changedPaths.SequenceEqual(receipt.ChangedPaths, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Mission diff paths changed after verification; run verification again.");
        }

        var fingerprint = await workspaceManager.ComputeDiffFingerprintAsync(
            workspace,
            changedPaths,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(fingerprint, receipt.DiffFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mission diff content changed after verification; run verification again.");
        }

        var findings = await safetyScanner.ScanAsync(workspace, changedPaths, cancellationToken)
            .ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new InvalidOperationException(
                "Mission safety scan failed: " + string.Join(", ", findings.Select(finding => finding.Path)));
        }

        await RequireSuccessAsync(
            new CommandInvocation("git", ["add", "--all"], workspace.WorktreePath),
            cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(
            new CommandInvocation("git", ["commit", "-m", commitMessage.Trim()], workspace.WorktreePath),
            cancellationToken).ConfigureAwait(false);
        var commitResult = await RequireSuccessAsync(
            new CommandInvocation("git", ["rev-parse", "HEAD"], workspace.WorktreePath),
            cancellationToken).ConfigureAwait(false);
        var commit = commitResult.StandardOutput.Trim();

        if (push)
        {
            await RequireSuccessAsync(
                new CommandInvocation(
                    "git",
                    ["push", "--set-upstream", "origin", workspace.BranchName],
                    workspace.WorktreePath),
                cancellationToken).ConfigureAwait(false);
        }

        string? pullRequestUrl = null;
        if (createDraftPullRequest)
        {
            var pullRequest = await RequireSuccessAsync(
                new CommandInvocation(
                    "gh",
                    [
                        "pr",
                        "create",
                        "--draft",
                        "--base",
                        baseBranch,
                        "--head",
                        workspace.BranchName,
                        "--title",
                        commitMessage.Trim(),
                        "--body",
                        "Created by an operator-approved Flyt Mission. Review and merge remain manual.",
                    ],
                    workspace.RepositoryRoot),
                cancellationToken).ConfigureAwait(false);
            pullRequestUrl = pullRequest.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
        }

        var completed = await store.TransitionMissionAsync(
            missionId,
            MissionState.Completed,
            createDraftPullRequest
                ? "Operator-approved commit, push, and draft pull request completed."
                : push
                    ? "Operator-approved commit and push completed."
                    : "Operator-approved local commit completed.",
            cancellationToken).ConfigureAwait(false);
        return new MissionIntegrationResult(completed, commit, push, pullRequestUrl);
    }

    private async Task<CommandResult> RequireSuccessAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await executor.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"{invocation.FileName} failed with exit code {result.ExitCode}: "
                + SensitiveDataRedactor.Redact(result.StandardError));
        }

        return result;
    }
}
