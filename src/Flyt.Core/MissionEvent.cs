namespace Flyt.Core;

public sealed record MissionEvent(
    long Sequence,
    Guid MissionId,
    string EventType,
    MissionState? FromState,
    MissionState ToState,
    string Detail,
    DateTimeOffset OccurredAt);

