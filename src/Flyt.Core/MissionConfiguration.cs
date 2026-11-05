namespace Flyt.Core;

public sealed record MissionConfiguration(
    Guid MissionId,
    string Name,
    AgentProvider BuilderProvider,
    AgentProvider? ReviewerProvider,
    int TimeoutMinutes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static MissionConfiguration Create(
        Guid missionId,
        string name,
        AgentProvider builderProvider = AgentProvider.Claude,
        AgentProvider? reviewerProvider = null,
        int timeoutMinutes = 90,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (builderProvider is not (AgentProvider.Claude or AgentProvider.Codex))
        {
            throw new ArgumentException("Mission builders must use Claude or Codex.", nameof(builderProvider));
        }

        if (reviewerProvider is not null
            && reviewerProvider is not (AgentProvider.Claude or AgentProvider.Codex))
        {
            throw new ArgumentException("Mission reviewers must use Claude or Codex.", nameof(reviewerProvider));
        }

        if (timeoutMinutes is < 1 or > 720)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMinutes),
                "Mission timeout must be between 1 and 720 minutes.");
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length > 80
            || Guid.TryParse(normalizedName, out _)
            || !char.IsLetterOrDigit(normalizedName[0])
            || normalizedName.Any(value => !char.IsLetterOrDigit(value) && value is not ('-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Mission names must contain 1-80 letters, digits, dots, dashes, or underscores, start with a letter or digit, and not be a GUID.",
                nameof(name));
        }

        var timestamp = now ?? DateTimeOffset.UtcNow;
        return new MissionConfiguration(
            missionId,
            normalizedName,
            builderProvider,
            reviewerProvider,
            timeoutMinutes,
            timestamp,
            timestamp);
    }
}
