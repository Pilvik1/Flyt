namespace Flyt.Core;

public sealed record MissionPlan(
    Guid MissionId,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Steps,
    string VerificationCommand,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static MissionPlan Create(
        Guid missionId,
        IEnumerable<string> acceptanceCriteria,
        IEnumerable<string> steps,
        string verificationCommand,
        DateTimeOffset? now = null)
    {
        var criteria = Normalize(acceptanceCriteria, nameof(acceptanceCriteria));
        var normalizedSteps = Normalize(steps, nameof(steps));
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationCommand);

        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new MissionPlan(
            missionId,
            criteria,
            normalizedSteps,
            verificationCommand.Trim(),
            Version: 1,
            timestamp,
            timestamp);
    }

    private static string[] Normalize(IEnumerable<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var normalized = values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return normalized.Length > 0
            ? normalized
            : throw new ArgumentException("At least one non-empty value is required.", parameterName);
    }
}
