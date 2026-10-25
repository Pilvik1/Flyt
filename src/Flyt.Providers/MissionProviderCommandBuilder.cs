using System.Text;
using Flyt.Core;

namespace Flyt.Providers;

public sealed record MissionProviderRequest(
    Mission Mission,
    MissionPlan Plan,
    MissionWorkspace Workspace,
    bool ReadOnly,
    int MaximumTurns = 20,
    string? AdditionalContext = null);

public static class MissionProviderCommandBuilder
{
    public static CommandInvocation Build(AgentProvider provider, MissionProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumTurns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum turns must be positive.");
        }

        var prompt = BuildPrompt(request);
        return provider switch
        {
            AgentProvider.Claude => new CommandInvocation(
                "claude",
                [
                    "-p",
                    "--output-format",
                    "json",
                    "--max-turns",
                    request.MaximumTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--permission-mode",
                    request.ReadOnly ? "plan" : "acceptEdits",
                    prompt,
                ],
                request.Workspace.WorktreePath),
            AgentProvider.Codex => new CommandInvocation(
                "codex",
                [
                    "exec",
                    "--json",
                    "--ephemeral",
                    "--sandbox",
                    request.ReadOnly ? "read-only" : "workspace-write",
                    "-c",
                    "approval_policy=\"never\"",
                    prompt,
                ],
                request.Workspace.WorktreePath),
            _ => throw new NotSupportedException(
                $"{provider} is not supported for structured Mission execution."),
        };
    }

    private static string BuildPrompt(MissionProviderRequest request)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("You are executing one bounded Flyt Mission.");
        prompt.AppendLine("Goal: " + request.Mission.Goal);
        prompt.AppendLine("Acceptance criteria:");
        foreach (var criterion in request.Plan.AcceptanceCriteria)
        {
            prompt.AppendLine("- " + criterion);
        }

        prompt.AppendLine("Approved steps:");
        foreach (var step in request.Plan.Steps)
        {
            prompt.AppendLine("- " + step);
        }

        prompt.AppendLine(
            "Deterministic verification (Flyt runs this later): " + request.Plan.VerificationCommand);
        prompt.AppendLine(request.ReadOnly
            ? "Review the current diff only. Do not edit files, commit, push, or change Git state."
            : "Work only inside the current worktree. Edit the minimum necessary files. Do not commit, push, merge, or create additional worktrees.");
        prompt.AppendLine("Do not claim success based on prose. Summarize changed files, remaining risks, and any blocker.");
        if (request.ReadOnly)
        {
            prompt.AppendLine(
                "End with exactly one verdict marker: "
                + MissionReviewParser.PassMarker
                + " or "
                + MissionReviewParser.BlockMarker
                + ".");
        }

        else if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
        {
            prompt.AppendLine("Review feedback from the prior attempt:");
            prompt.AppendLine(SensitiveDataRedactor.Redact(request.AdditionalContext));
        }

        return prompt.ToString();
    }
}
