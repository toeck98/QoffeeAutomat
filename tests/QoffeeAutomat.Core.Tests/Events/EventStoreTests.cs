using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Events;

/// <summary>The recent-events table: persistence and retention.</summary>
[Trait("Category", "Events")]
public sealed class EventStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SimulatedClock _clock = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Events_survive_closing_and_reopening_the_database()
    {
        string path = EventTestSupport.DatabasePath(_temp);
        using (var store = new SqliteEventStore(path, _clock))
        {
            await store.AddAsync(EventTypes.AgentStarted, _clock.GetUtcNow(), EventTestSupport.Json("""{"version":"1.2.3"}"""), CancellationToken.None);
            await store.AddAsync(EventTypes.Resumed, _clock.GetUtcNow(), null, CancellationToken.None);
        }

        using var reopened = new SqliteEventStore(path, _clock);
        IReadOnlyList<AgentEvent> events = await reopened.GetRecentAsync(10, null, CancellationToken.None);

        Assert.Equal([EventTypes.Resumed, EventTypes.AgentStarted], events.Select(e => e.Type));
        Assert.Null(events[0].Data);
        Assert.Equal("1.2.3", events[1].Data!.Value.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Recent_events_are_newest_first_and_can_be_limited_and_filtered()
    {
        using var store = new SqliteEventStore(EventTestSupport.DatabasePath(_temp), _clock);
        for (int index = 0; index < 5; index++)
        {
            await store.AddAsync(index % 2 == 0 ? EventTypes.Custom : EventTypes.Resumed, _clock.GetUtcNow(), EventData.Create(("n", index)), CancellationToken.None);
        }

        IReadOnlyList<AgentEvent> latestTwo = await store.GetRecentAsync(2, null, CancellationToken.None);
        IReadOnlyList<AgentEvent> customOnly = await store.GetRecentAsync(10, EventTypes.Custom, CancellationToken.None);

        Assert.Equal([4, 3], latestTwo.Select(e => e.Data!.Value.GetProperty("n").GetInt32()));
        Assert.Equal([4, 2, 0], customOnly.Select(e => e.Data!.Value.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task The_oldest_events_are_removed_when_there_are_too_many()
    {
        var options = new EventStoreOptions { MaxEvents = 5, PruneEvery = 1 };
        using var store = new SqliteEventStore(EventTestSupport.DatabasePath(_temp), _clock, options);

        for (int index = 0; index < 12; index++)
        {
            await store.AddAsync(EventTypes.Custom, _clock.GetUtcNow(), EventData.Create(("n", index)), CancellationToken.None);
        }

        IReadOnlyList<AgentEvent> events = await store.GetRecentAsync(100, null, CancellationToken.None);
        Assert.Equal([11, 10, 9, 8, 7], events.Select(e => e.Data!.Value.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task Events_older_than_the_maximum_age_are_removed()
    {
        var options = new EventStoreOptions { MaxAge = TimeSpan.FromDays(7), PruneEvery = 1 };
        using var store = new SqliteEventStore(EventTestSupport.DatabasePath(_temp), _clock, options);
        await store.AddAsync(EventTypes.Custom, _clock.GetUtcNow(), EventData.Create(("n", 1)), CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(8));
        await store.AddAsync(EventTypes.Custom, _clock.GetUtcNow(), EventData.Create(("n", 2)), CancellationToken.None);

        IReadOnlyList<AgentEvent> events = await store.GetRecentAsync(10, null, CancellationToken.None);
        AgentEvent only = Assert.Single(events);
        Assert.Equal(2, only.Data!.Value.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Old_events_are_pruned_when_the_database_is_opened()
    {
        string path = EventTestSupport.DatabasePath(_temp);
        using (var store = new SqliteEventStore(path, _clock))
        {
            await store.AddAsync(EventTypes.Custom, _clock.GetUtcNow(), null, CancellationToken.None);
        }

        _clock.Advance(TimeSpan.FromDays(40));

        using var reopened = new SqliteEventStore(path, _clock);
        Assert.Empty(await reopened.GetRecentAsync(10, null, CancellationToken.None));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        string path = EventTestSupport.DatabasePath(_temp);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteEventStore(path, _clock, new EventStoreOptions { MaxEvents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteEventStore(path, _clock, new EventStoreOptions { MaxAge = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteEventStore(path, _clock, new EventStoreOptions { PruneEvery = 0 }));
    }

    [Fact]
    public async Task Hostile_text_in_type_and_data_is_stored_as_data_not_executed()
    {
        using var store = new SqliteEventStore(EventTestSupport.DatabasePath(_temp), _clock);
        const string type = "x'); DROP TABLE events; --";

        await store.AddAsync(type, _clock.GetUtcNow(), EventTestSupport.Json("""{"a":"'; DROP TABLE events; --"}"""), CancellationToken.None);

        IReadOnlyList<AgentEvent> events = await store.GetRecentAsync(10, type, CancellationToken.None);
        Assert.Equal(type, Assert.Single(events).Type);
    }
}
