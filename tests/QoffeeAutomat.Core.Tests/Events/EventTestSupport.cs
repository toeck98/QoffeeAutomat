using System.Text.Json;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Core.Tests.Events;

/// <summary>Helpers shared by the event tests.</summary>
internal static class EventTestSupport
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Reads the next event or fails the test after a generous timeout.</summary>
    public static async Task<AgentEvent> NextAsync(EventSubscription subscription)
    {
        using var timeout = new CancellationTokenSource(Patience);
        try
        {
            return await subscription.Events.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException("No event arrived within " + Patience.TotalSeconds + " seconds.");
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> events.</summary>
    public static async Task<List<AgentEvent>> NextAsync(EventSubscription subscription, int count)
    {
        var events = new List<AgentEvent>();
        for (int index = 0; index < count; index++)
        {
            events.Add(await NextAsync(subscription));
        }

        return events;
    }

    /// <summary>Tells whether another event is already waiting (after the ones that were read).</summary>
    public static bool HasPending(EventSubscription subscription)
    {
        return subscription.Events.TryPeek(out _);
    }

    public static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string DatabasePath(TempDirectory temp) => temp.Combine("data", "events.db");
}

/// <summary>An event store that fails, to show that a broken database does not silence the bus.</summary>
internal sealed class FailingEventStore : IEventStore
{
    public Task<long> AddAsync(string type, DateTimeOffset timestamp, JsonElement? data, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("The disk is full.");
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit, string? type, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<AgentEvent>>([]);
    }
}
