using Flyt.Core;
using Flyt.Persistence;
using Flyt.Providers;
using Flyt.Terminal;

namespace Flyt.Cli;

internal sealed record MissionRunOutcome(
    Mission Mission,
    MissionWorkspace Workspace,
    MissionAttempt Attempt,
    MissionReviewReceipt? Review,
    MissionVerificationReceipt? Verification);

internal sealed class MissionExecutionCoordinator(
    FlytStateStore store,
    IMissionWorkspaceManager workspaceManager,
    IMissionSafetyScanner safetyScanner,
    ICommandExecutor executor)
{
    public async Task<MissionRunOutcome> RunAsync(
        Guid missionId,
        AgentProvider provider,
        TimeSpan timeout,
        AgentProvider? reviewer = null,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Mission timeout must be positive.");
        }

        var approved = await store.RequireApprovedMissionPlanAsync(missionId, cancellationToken)
            .ConfigureAwait(false);
        var policy = AutonomyPolicy.Parse(approved.Mission.Profile);
        var priorAttempts = await store.GetMissionAttemptsAsync(missionId, cancellationToken).ConfigureAwait(false);
        if (priorAttempts.Count >= policy.MaxFixLoops + 1)
        {
            throw new InvalidOperationException(
                $"Mission reached the {policy.Name} profile limit of {policy.MaxFixLoops + 1} provider attempt(s).");
        }

        var priorReview = await store.GetLatestMissionReviewReceiptAsync(missionId, cancellationToken)
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
                new MissionProviderRequest(
                    executing,
                    approved.Plan,
                    workspace,
                    ReadOnly: false,
                    AdditionalContext: priorReview?.Verdict == MissionReviewVerdict.Pass
                        ? null
                        : priorReview?.StandardOutput));
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
            return new MissionRunOutcome(
                timedOut.Mission,
                workspace,
                timedOut.Attempt,
                Review: null,
                Verification: null);
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
            return new MissionRunOutcome(
                failed.Mission,
                workspace,
                failed.Attempt,
                Review: null,
                Verification: null);
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
            return new MissionRunOutcome(
                failed.Mission,
                workspace,
                failed.Attempt,
                Review: null,
                Verification: null);
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
            return new MissionRunOutcome(blocked, workspace, attempt, Review: null, Verification: null);
        }

        var safetyFindings = await safetyScanner.ScanAsync(workspace, changedPaths, cancellationToken)
            .ConfigureAwait(false);
        if (safetyFindings.Count > 0)
        {
            var blocked = await store.TransitionMissionAsync(
                missionId,
                MissionState.Blocked,
                $"Safety scan blocked {safetyFindings.Count} changed path(s): "
                + string.Join(", ", safetyFindings.Select(finding => finding.Path)),
                CancellationToken.None).ConfigureAwait(false);
            return new MissionRunOutcome(blocked, workspace, attempt, Review: null, Verification: null);
        }

        var selectedReviewer = reviewer ?? (provider == AgentProvider.Claude
            ? AgentProvider.Codex
            : AgentProvider.Claude);
        var reviewing = await store.TransitionMissionAsync(
            missionId,
            MissionState.Reviewing,
            $"Builder attempt {attempt.Number} produced {changedPaths.Count} changed path(s).",
            cancellationToken).ConfigureAwait(false);
        var reviewStartedAt = DateTimeOffset.UtcNow;
        CommandResult reviewResult;
        try
        {
            var reviewCommand = MissionProviderCommandBuilder.Build(
                selectedReviewer,
                new MissionProviderRequest(reviewing, approved.Plan, workspace, ReadOnly: true, MaximumTurns: 10));
            reviewResult = await executor.RunAsync(reviewCommand, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            reviewResult = new CommandResult(124, string.Empty, "Independent review exceeded the Mission timeout.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reviewResult = new CommandResult(127, string.Empty, exception.Message);
        }

        var verdict = reviewResult.IsSuccess
            ? MissionReviewParser.Parse(reviewResult.StandardOutput + Environment.NewLine + reviewResult.StandardError)
            : MissionReviewVerdict.Invalid;
        var reviewReceipt = new MissionReviewReceipt(
            Guid.NewGuid(),
            missionId,
            attempt.Id,
            selectedReviewer,
            verdict,
            reviewResult.ExitCode,
            reviewResult.StandardOutput,
            reviewResult.StandardError,
            reviewStartedAt,
            DateTimeOffset.UtcNow);
        await store.SaveMissionReviewReceiptAsync(reviewReceipt, CancellationToken.None).ConfigureAwait(false);
        if (reviewReceipt.Verdict != MissionReviewVerdict.Pass)
        {
            var blocked = await store.TransitionMissionAsync(
                missionId,
                MissionState.Blocked,
                $"Independent {selectedReviewer} review returned {reviewReceipt.Verdict}.",
                CancellationToken.None).ConfigureAwait(false);
            return new MissionRunOutcome(blocked, workspace, attempt, reviewReceipt, Verification: null);
        }

        await store.TransitionMissionAsync(
            missionId,
            MissionState.Verifying,
            $"Independent {selectedReviewer} review passed.",
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            verificationResult = new CommandResult(
                124,
                string.Empty,
                "Deterministic verification exceeded the Mission timeout.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            verificationResult = new CommandResult(127, string.Empty, exception.Message);
        }

        var headCommit = await workspaceManager.GetHeadCommitAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        var diffFingerprint = await workspaceManager.ComputeDiffFingerprintAsync(
            workspace,
            changedPaths,
            cancellationToken).ConfigureAwait(false);
        var receipt = new MissionVerificationReceipt(
            Guid.NewGuid(),
            missionId,
            attempt.Id,
            approved.Plan.VerificationCommand,
            verificationResult.ExitCode,
            verificationResult.IsSuccess,
            workspace.BaseCommit,
            headCommit,
            diffFingerprint,
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
        return new MissionRunOutcome(finalMission, workspace, attempt, reviewReceipt, receipt);
    }

    public async Task<MissionRunOutcome> RecoverAsync(
        Guid missionId,
        CancellationToken cancellationToken = default)
    {
        var mission = await store.GetMissionAsync(missionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Mission {missionId:D} was not found.");
        if (mission.State is not (
            MissionState.Executing
            or MissionState.Reviewing
            or MissionState.Verifying
            or MissionState.Fixing))
        {
            throw new InvalidOperationException(
                $"Mission recovery requires an interrupted active state; current state is {mission.State}.");
        }

        var workspace = await store.GetMissionWorkspaceAsync(missionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mission recovery requires a persisted workspace.");
        var attempt = await store.GetLatestMissionAttemptAsync(missionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mission recovery requires a persisted attempt.");
        if (attempt.Status == MissionAttemptStatus.Running)
        {
            attempt = await store.CompleteMissionAttemptAsync(
                attempt.Id,
                MissionAttemptStatus.Interrupted,
                exitCode: null,
                string.Empty,
                "Flyt restarted before the provider attempt recorded completion.",
                CancellationToken.None).ConfigureAwait(false);
        }

        var changedPaths = await workspaceManager.GetChangedPathsAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
        var blocked = await store.TransitionMissionAsync(
            missionId,
            MissionState.Blocked,
            $"Recovered interrupted {mission.State} state with {changedPaths.Count} changed path(s); operator review required.",
            CancellationToken.None).ConfigureAwait(false);
        return new MissionRunOutcome(
            blocked,
            workspace,
            attempt,
            await store.GetLatestMissionReviewReceiptAsync(missionId, cancellationToken).ConfigureAwait(false),
            await store.GetLatestMissionVerificationReceiptAsync(missionId, cancellationToken).ConfigureAwait(false));
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
