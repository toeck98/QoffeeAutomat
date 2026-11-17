using System.Text.Json;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Events;

/// <summary>Acceptance tests of T217: the in-process bus and the recent-events table.</summary>
[Trait("Category", "Events")]
public sealed class EventBusTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SimulatedClock _clock = new();
    private readonly SqliteEventStore _store;
    private readonly EventBus _bus;

    public EventBusTests()
    {
        _store = new SqliteEventStore(EventTestSupport.DatabasePath(_temp), _clock);
        _bus = new EventBus(_clock, _store);
    }

    public void Dispose()
    {
        _bus.Dispose();
        _store.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task An_event_reaches_every_subscriber_once_and_is_stored()
    {
        using EventSubscription first = _bus.Subscribe();
        using EventSubscription second = _bus.Subscribe();
        using EventSubscription onlyOtherTypes = _bus.Subscribe([EventTypes.Resumed]);

        AgentEvent published = await _bus.PublishAsync(EventTypes.SessionLocked, EventTestSupport.Json("""{"user":"alice"}"""));

        AgentEvent seenByFirst = await EventTestSupport.NextAsync(first);
        AgentEvent seenBySecond = await EventTestSupport.NextAsync(second);
        Assert.Equal(published, seenByFirst);
        Assert.Equal(published, seenBySecond);
        Assert.False(EventTestSupport.HasPending(first));
        Assert.False(EventTestSupport.HasPending(second));
        Assert.False(EventTestSupport.HasPending(onlyOtherTypes));

        IReadOnlyList<AgentEvent> stored = await _bus.GetRecentAsync(10);
        AgentEvent row = Assert.Single(stored);
        Assert.Equal(published.Id, row.Id);
        Assert.True(row.Id > 0);
        Assert.Equal(EventTypes.SessionLocked, row.Type);
        Assert.Equal(_clock.GetUtcNow(), row.Timestamp);
        Assert.Equal("alice", row.Data!.Value.GetProperty("user").GetString());
    }

    [Fact]
    public async Task Events_are_delivered_in_the_order_they_were_published()
    {
        using EventSubscription subscription = _bus.Subscribe();

        for (int index = 0; index < 20; index++)
        {
            await _bus.PublishAsync(EventTypes.Custom, EventData.Create(("n", index)));
        }

        List<AgentEvent> events = await EventTestSupport.NextAsync(subscription, 20);
        Assert.Equal(Enumerable.Range(0, 20), events.Select(e => e.Data!.Value.GetProperty("n").GetInt32()));
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Id < pair.Second.Id));
    }

    [Fact]
    public async Task A_subscription_with_a_filter_receives_only_those_types()
    {
        using EventSubscription filtered = _bus.Subscribe([EventTypes.Resumed, EventTypes.Custom]);

        await _bus.PublishAsync(EventTypes.SessionLocked);
        await _bus.PublishAsync(EventTypes.Resumed);
        await _bus.PublishAsync(EventTypes.UserLoggedIn);
        await _bus.PublishAsync(EventTypes.Custom);

        List<AgentEvent> events = await EventTestSupport.NextAsync(filtered, 2);
        Assert.Equal([EventTypes.Resumed, EventTypes.Custom], events.Select(e => e.Type));
        Assert.False(EventTestSupport.HasPending(filtered));
    }

    [Fact]
    public async Task A_disposed_subscription_receives_nothing_more_and_its_channel_completes()
    {
        EventSubscription subscription = _bus.Subscribe();
        subscription.Dispose();

        await _bus.PublishAsync(EventTypes.Resumed);

        Assert.False(await subscription.Events.WaitToReadAsync());
    }

    [Fact]
    public async Task An_event_type_outside_the_catalog_is_delivered_but_not_stored()
    {
        using EventSubscription subscription = _bus.Subscribe();

        AgentEvent started = await _bus.PublishAsync(EventTypes.CommandStarted, EventTestSupport.Json("""{"command":"x"}"""));

        Assert.Equal(0, started.Id);
        Assert.Equal(started, await EventTestSupport.NextAsync(subscription));
        Assert.Empty(await _bus.GetRecentAsync(10));
        Assert.False(EventCatalog.Contains(EventTypes.CommandStarted));
    }

    [Fact]
    public async Task The_data_is_copied_so_the_caller_may_dispose_its_document()
    {
        using EventSubscription subscription = _bus.Subscribe();
        JsonElement element;
        using (JsonDocument document = JsonDocument.Parse("""{"k":"v"}"""))
        {
            element = document.RootElement;
            await _bus.PublishAsync(EventTypes.Custom, element);
        }

        AgentEvent received = await EventTestSupport.NextAsync(subscription);
        Assert.Equal("v", received.Data!.Value.GetProperty("k").GetString());
    }

    [Fact]
    public async Task A_failing_store_does_not_stop_the_delivery_and_is_reported()
    {
        using var bus = new EventBus(_clock, new FailingEventStore());
        Exception? reported = null;
        string? reportedType = null;
        bus.StoreFailed += (_, failure) =>
        {
            reported = failure.Exception;
            reportedType = failure.Type;
        };
        using EventSubscription subscription = bus.Subscribe();

        AgentEvent published = await bus.PublishAsync(EventTypes.Resumed);

        Assert.Equal(0, published.Id);
        Assert.Equal(published, await EventTestSupport.NextAsync(subscription));
        Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal(EventTypes.Resumed, reportedType);
    }

    [Fact]
    public async Task A_bus_without_a_store_still_delivers()
    {
        using var bus = new EventBus(_clock);
        using EventSubscription subscription = bus.Subscribe();

        await bus.PublishAsync(EventTypes.AgentStarted);

        Assert.Equal(EventTypes.AgentStarted, (await EventTestSupport.NextAsync(subscription)).Type);
        Assert.Empty(await bus.GetRecentAsync(5));
    }

    [Fact]
    public async Task Publishing_to_a_disposed_bus_fails_and_subscriptions_are_completed()
    {
        EventSubscription subscription = _bus.Subscribe();
        _bus.Dispose();

        Assert.False(await subscription.Events.WaitToReadAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _bus.PublishAsync(EventTypes.Resumed));
        using EventSubscription late = _bus.Subscribe();
        Assert.False(await late.Events.WaitToReadAsync());
    }

    [Fact]
    public async Task Concurrent_publishers_deliver_every_event_exactly_once_in_store_order()
    {
        using EventSubscription subscription = _bus.Subscribe();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (int index = 0; index < 10; index++)
            {
                await _bus.PublishAsync(EventTypes.Custom, EventData.Create(("worker", worker), ("n", index)));
            }
        })));

        List<AgentEvent> events = await EventTestSupport.NextAsync(subscription, 80);
        Assert.False(EventTestSupport.HasPending(subscription));
        Assert.Equal(80, events.Select(e => e.Id).Distinct().Count());
        Assert.Equal(events.Select(e => e.Id).Order(), events.Select(e => e.Id));
        Assert.Equal(80, (await _bus.GetRecentAsync(200)).Count);
    }

    [Fact]
    public void The_catalog_lists_the_event_types_of_the_plan()
    {
        Assert.Equal(
            [
                "agent_started",
                "command_finished",
                "session_locked",
                "session_unlocked",
                "user_logged_in",
                "user_logged_out",
                "session_ready",
                "resumed",
                "mode_changed",
                "hotkey_pressed",
                "notification_action",
                "power_action_cancelled",
                "custom",
            ],
            EventCatalog.Types.Select(entry => entry.Type));
        Assert.All(EventCatalog.Types, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Name)));
    }

    [Fact]
    public async Task The_event_converts_to_the_link_frame()
    {
        AgentEvent published = await _bus.PublishAsync(EventTypes.Custom, EventTestSupport.Json("""{"name":"x"}"""));

        QoffeeAutomat.Contracts.Link.EventMessage message = published.ToMessage();

        Assert.Equal(EventTypes.Custom, message.Type);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds(), message.Ts);
        Assert.Equal("x", message.Data!.Value.GetProperty("name").GetString());
    }
}
