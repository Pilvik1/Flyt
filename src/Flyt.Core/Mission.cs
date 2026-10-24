namespace Flyt.Core;

public sealed record Mission(
    Guid Id,
    string Goal,
    string WorkspacePath,
    string Profile,
    MissionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    private static readonly Dictionary<MissionState, HashSet<MissionState>> AllowedTransitions =
        new()
        {
            [MissionState.Briefing] = States(MissionState.PlanApproval, MissionState.Blocked, MissionState.Cancelled),
            [MissionState.PlanApproval] = States(MissionState.Planning, MissionState.Briefing, MissionState.Cancelled),
            [MissionState.Planning] = ActiveStates(MissionState.Executing, MissionState.Blocked),
            [MissionState.Executing] = ActiveStates(MissionState.Reviewing, MissionState.Verifying, MissionState.Blocked),
            [MissionState.Reviewing] = ActiveStates(MissionState.Verifying, MissionState.Fixing, MissionState.Blocked),
            [MissionState.Verifying] = ActiveStates(MissionState.Integrating, MissionState.Fixing, MissionState.Blocked),
            [MissionState.Fixing] = ActiveStates(MissionState.Reviewing, MissionState.Verifying, MissionState.Blocked),
            [MissionState.Integrating] = ActiveStates(MissionState.Completed, MissionState.Fixing, MissionState.Blocked),
            [MissionState.Paused] = States(
                MissionState.Planning,
                MissionState.Executing,
                MissionState.Reviewing,
                MissionState.Verifying,
                MissionState.Fixing,
                MissionState.Integrating,
                MissionState.Blocked,
                MissionState.Cancelled),
            [MissionState.Blocked] = States(
                MissionState.Planning,
                MissionState.Executing,
                MissionState.Reviewing,
                MissionState.Verifying,
                MissionState.Fixing,
                MissionState.Integrating,
                MissionState.Cancelled),
            [MissionState.Completed] = States(),
            [MissionState.Cancelled] = States(),
        };

    public static Mission Create(
        string goal,
        string workspacePath,
        AutonomyPolicy? policy = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var timestamp = now ?? DateTimeOffset.UtcNow;
        var selectedPolicy = policy ?? AutonomyPolicy.Balanced;

        return new Mission(
            Guid.NewGuid(),
            goal.Trim(),
            Path.GetFullPath(workspacePath),
            selectedPolicy.Name,
            MissionState.Briefing,
            timestamp,
            timestamp);
    }

    public bool CanTransitionTo(MissionState nextState) =>
        AllowedTransitions.TryGetValue(State, out var allowed) && allowed.Contains(nextState);

    public Mission TransitionTo(MissionState nextState, DateTimeOffset? now = null)
    {
        if (!CanTransitionTo(nextState))
        {
            throw new InvalidOperationException($"Mission cannot transition from {State} to {nextState}.");
        }

        return this with
        {
            State = nextState,
            UpdatedAt = now ?? DateTimeOffset.UtcNow,
        };
    }

    private static HashSet<MissionState> ActiveStates(params MissionState[] states)
    {
        var result = new HashSet<MissionState>(states)
        {
            MissionState.Paused,
            MissionState.Cancelled,
        };

        return result;
    }

    private static HashSet<MissionState> States(params MissionState[] states) =>
        new HashSet<MissionState>(states);
}
