using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Events;

/// <summary>Session and power changes become events on the bus.</summary>
[Trait("Category", "Events")]
public sealed class PlatformEventPublisherTests : IDisposable
{
    private readonly SimulatedClock _clock = new();
    private readonly SimulatedSessionBridge _session;
    private readonly SimulatedPowerEvents _power = new();
    private readonly EventBus _bus;
    private readonly PlatformEventPublisher _publisher;
    private readonly EventSubscription _events;

    public PlatformEventPublisherTests()
    {
        _session = new SimulatedSessionBridge(_clock);
        _bus = new EventBus(_clock);
        _events = _bus.Subscribe();
        _publisher = new PlatformEventPublisher(_bus, _session, _power);
    }

    public void Dispose()
    {
        _publisher.Dispose();
        _events.Dispose();
        _bus.Dispose();
    }

    [Fact]
    public async Task Locking_and_unlocking_the_session_publish_events_with_the_user()
    {
        _session.SetLocked(true);
        _session.SetLocked(false);

        List<AgentEvent> events = await EventTestSupport.NextAsync(_events, 2);

        Assert.Equal([EventTypes.SessionLocked, EventTypes.SessionUnlocked], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Equal("sim-user", e.Data!.Value.GetProperty("user").GetString()));
    }

    [Fact]
    public async Task Logging_off_and_on_publish_logout_login_and_session_ready_in_order()
    {
        _session.SimulateLogoff();
        _session.SimulateLogon("bob", isAdministrator: false);

        List<AgentEvent> events = await EventTestSupport.NextAsync(_events, 3);

        Assert.Equal([EventTypes.UserLoggedOut, EventTypes.UserLoggedIn, EventTypes.SessionReady], events.Select(e => e.Type));
        Assert.Equal("sim-user", events[0].Data!.Value.GetProperty("user").GetString());
        Assert.Equal("bob", events[1].Data!.Value.GetProperty("user").GetString());
        Assert.Equal("bob", events[2].Data!.Value.GetProperty("user").GetString());
    }

    [Fact]
    public async Task Switching_to_another_user_publishes_logout_then_login()
    {
        _session.SetState(new SessionState(true, "carol", false, false));

        List<AgentEvent> events = await EventTestSupport.NextAsync(_events, 2);

        Assert.Equal([EventTypes.UserLoggedOut, EventTypes.UserLoggedIn], events.Select(e => e.Type));
    }

    [Fact]
    public async Task The_session_helper_becoming_ready_publishes_session_ready_without_a_login()
    {
        _session.SetReady(false);
        _session.SetReady(true);

        AgentEvent ready = await EventTestSupport.NextAsync(_events);

        Assert.Equal(EventTypes.SessionReady, ready.Type);
        Assert.False(EventTestSupport.HasPending(_events));
    }

    [Fact]
    public async Task Logging_off_a_locked_session_is_a_logout_not_an_unlock()
    {
        _session.SetLocked(true);
        _session.SimulateLogoff();

        List<AgentEvent> events = await EventTestSupport.NextAsync(_events, 2);

        Assert.Equal([EventTypes.SessionLocked, EventTypes.UserLoggedOut], events.Select(e => e.Type));
    }

    [Fact]
    public async Task Only_resume_becomes_an_event_among_the_power_changes()
    {
        _power.Simulate(PowerEventKind.Sleep);
        _power.Simulate(PowerEventKind.Hibernate);
        _power.Simulate(PowerEventKind.Resume);
        _power.Simulate(PowerEventKind.Shutdown);

        AgentEvent resumed = await EventTestSupport.NextAsync(_events);

        Assert.Equal(EventTypes.Resumed, resumed.Type);
        Assert.False(EventTestSupport.HasPending(_events));
    }

    [Fact]
    public async Task A_state_change_that_changes_nothing_relevant_publishes_nothing()
    {
        _session.SetState(_session.State with { IsUserAdministrator = false });
        _power.Simulate(PowerEventKind.Resume);

        AgentEvent only = await EventTestSupport.NextAsync(_events);

        Assert.Equal(EventTypes.Resumed, only.Type);
    }

    [Fact]
    public async Task After_dispose_nothing_is_published()
    {
        _publisher.Dispose();

        _session.SetLocked(true);
        _power.Simulate(PowerEventKind.Resume);
        await Task.Delay(50);

        Assert.False(EventTestSupport.HasPending(_events));
    }
}
