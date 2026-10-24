using Flyt.Core;
using Flyt.Persistence;

namespace Flyt.Tests;

public sealed class FlytStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "flytv1-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAndTransition_PersistsMissionAndOrderedEvents()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Test persistence", _directory);

        await store.CreateMissionAsync(mission);
        var updated = await store.TransitionMissionAsync(
            mission.Id,
            MissionState.PlanApproval,
            "Ready for approval.");

        var loaded = await store.GetMissionAsync(mission.Id);
        var events = await store.GetEventsAsync(mission.Id);

        Assert.NotNull(loaded);
        Assert.Equal(MissionState.PlanApproval, loaded.State);
        Assert.Equal(updated.UpdatedAt, loaded.UpdatedAt);
        Assert.Collection(
            events,
            created => Assert.Equal("mission.created", created.EventType),
            transitioned =>
            {
                Assert.Equal("mission.transitioned", transitioned.EventType);
                Assert.Equal(MissionState.Briefing, transitioned.FromState);
                Assert.Equal(MissionState.PlanApproval, transitioned.ToState);
            });
    }

    [Fact]
    public async Task Transition_RejectsInvalidMoveWithoutAppendingEvent()
    {
        var store = new FlytStateStore(Path.Combine(_directory, "state.db"));
        await store.InitializeAsync();
        var mission = Mission.Create("Test invalid transition", _directory);
        await store.CreateMissionAsync(mission);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.TransitionMissionAsync(
                mission.Id,
                MissionState.Completed,
                "This must fail."));

        var events = await store.GetEventsAsync(mission.Id);
        Assert.Single(events);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

