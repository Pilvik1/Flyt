using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;

namespace Flyt.Cli;

internal sealed record MissionRunOutcome(
    Mission Mission,
    MissionWorkspace Workspace,
    MissionAttempt Attempt,
    MissionVerificationReceipt? Verification);

internal sealed class MissionExecutionCoordinator(
    FlytStateStore store,
    IMissionWorkspaceManager workspaceManager,
    ICommandExecutor executor)
{
    public async Task<MissionRunOutcome> RunAsync(
        Guid missionId,
        AgentProvider provider,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Mission timeout must be positive.");
        }

        var approved = await store.RequireApprovedMissionPlanAsync(missionId, cancellationToken)
            .ConfigureAwait(false);
        var workspace = await workspaceManager.EnsureBuilderWorktreeAsync(
            approved.Mission,
            cancellationToken).ConfigureAwait(false);
        workspace = await store.SaveMissionWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        var fingerprint = approved.Plan.ComputeFingerprint();
        var attempt = await store.StartMissionAttemptAsync(
            missionId,
            provider,
            approved.Plan.Version,
            fingerprint,
            cancellationToken).ConfigureAwait(false);
        var executing = await store.TransitionMissionAsync(
            missionId,
            MissionState.Executing,
            $"Builder attempt {attempt.Number} started with {provider}.",
            cancellationToken).ConfigureAwait(false);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        CommandResult providerResult;
        try
        {
            var providerCommand = MissionProviderCommandBuilder.Build(
                provider,
                new MissionProviderRequest(executing, approved.Plan, workspace, ReadOnly: false));
            providerResult = await executor.RunAsync(providerCommand, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timedOut = await FinishAttemptAndBlockAsync(
                attempt,
                MissionAttemptStatus.TimedOut,
                exitCode: null,
                string.Empty,
                $"Provider exceeded the {timeout} Mission timeout.",
                "Provider execution timed out.").ConfigureAwait(false);
            return new MissionRunOutcome(timedOut.Mission, workspace, timedOut.Attempt, Verification: null);
        }
        catch (OperationCanceledException)
        {
            await store.CompleteMissionAttemptAsync(
                attempt.Id,
                MissionAttemptStatus.Cancelled,
                exitCode: null,
                string.Empty,
                "Provider execution was cancelled.",
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var failed = await FinishAttemptAndBlockAsync(
                attempt,
                MissionAttemptStatus.Failed,
                exitCode: null,
                string.Empty,
                exception.Message,
                "Provider process could not be started.").ConfigureAwait(false);
            return new MissionRunOutcome(failed.Mission, workspace, failed.Attempt, Verification: null);
        }

        if (!providerResult.IsSuccess)
        {
            var failed = await FinishAttemptAndBlockAsync(
                attempt,
                MissionAttemptStatus.Failed,
                providerResult.ExitCode,
                providerResult.StandardOutput,
                providerResult.StandardError,
                $"Provider exited with code {providerResult.ExitCode}.").ConfigureAwait(false);
            return new MissionRunOutcome(failed.Mission, workspace, failed.Attempt, Verification: null);
        }

        attempt = await store.CompleteMissionAttemptAsync(
            attempt.Id,
            MissionAttemptStatus.Succeeded,
            providerResult.ExitCode,
            providerResult.StandardOutput,
            providerResult.StandardError,
            CancellationToken.None).ConfigureAwait(false);
        var changedPaths = await workspaceManager.GetChangedPathsAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        if (changedPaths.Count == 0)
        {
            var blocked = await store.TransitionMissionAsync(
                missionId,
                MissionState.Blocked,
                "Builder completed without producing a Git diff.",
                CancellationToken.None).ConfigureAwait(false);
            return new MissionRunOutcome(blocked, workspace, attempt, Verification: null);
        }

        await store.TransitionMissionAsync(
            missionId,
            MissionState.Verifying,
            $"Builder attempt {attempt.Number} produced {changedPaths.Count} changed path(s).",
            cancellationToken).ConfigureAwait(false);
        var verificationStartedAt = DateTimeOffset.UtcNow;
        var verificationCommand = DeterministicCommandParser.Parse(
            approved.Plan.VerificationCommand,
            workspace.WorktreePath);
        CommandResult verificationResult;
        try
        {
            verificationResult = await executor.RunAsync(verificationCommand, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            verificationResult = new CommandResult(127, string.Empty, exception.Message);
        }

        var headCommit = await workspaceManager.GetHeadCommitAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        var receipt = new MissionVerificationReceipt(
            Guid.NewGuid(),
            missionId,
            attempt.Id,
            approved.Plan.VerificationCommand,
            verificationResult.ExitCode,
            verificationResult.IsSuccess,
            workspace.BaseCommit,
            headCommit,
            changedPaths,
            verificationResult.StandardOutput,
            verificationResult.StandardError,
            verificationStartedAt,
            DateTimeOffset.UtcNow);
        await store.SaveMissionVerificationReceiptAsync(receipt, CancellationToken.None).ConfigureAwait(false);

        var nextState = receipt.Passed ? MissionState.Integrating : MissionState.Blocked;
        var detail = receipt.Passed
            ? "Deterministic verification passed; waiting for operator integration approval."
            : $"Deterministic verification failed with exit code {receipt.ExitCode}.";
        var finalMission = await store.TransitionMissionAsync(
            missionId,
            nextState,
            detail,
            CancellationToken.None).ConfigureAwait(false);
        return new MissionRunOutcome(finalMission, workspace, attempt, receipt);
    }

    private async Task<(Mission Mission, MissionAttempt Attempt)> FinishAttemptAndBlockAsync(
        MissionAttempt attempt,
        MissionAttemptStatus status,
        int? exitCode,
        string standardOutput,
        string standardError,
        string detail)
    {
        var completed = await store.CompleteMissionAttemptAsync(
            attempt.Id,
            status,
            exitCode,
            standardOutput,
            standardError,
            CancellationToken.None).ConfigureAwait(false);
        var mission = await store.TransitionMissionAsync(
            attempt.MissionId,
            MissionState.Blocked,
            detail,
            CancellationToken.None).ConfigureAwait(false);
        return (mission, completed);
    }
}
