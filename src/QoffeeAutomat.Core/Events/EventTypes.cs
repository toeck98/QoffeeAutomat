using System.Collections.Frozen;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Core.Events;

/// <summary>
/// The event types of PLAN.md S07 and S10. The catalogued types (<see cref="EventCatalog"/>) are
/// announced in the manifest, stored in the recent-events table and delivered to Home Assistant;
/// <see cref="CommandStarted"/> is an in-process notification only.
/// </summary>
public static class EventTypes
{
    /// <summary>The agent finished starting.</summary>
    public const string AgentStarted = "agent_started";

    /// <summary>A command run ended; the data carries <c>command</c>, <c>run_id</c>, <c>source</c>, <c>exit_code</c>, <c>error</c> and <c>duration_ms</c>.</summary>
    public const string CommandFinished = "command_finished";

    /// <summary>The session was locked.</summary>
    public const string SessionLocked = "session_locked";

    /// <summary>The session was unlocked.</summary>
    public const string SessionUnlocked = "session_unlocked";

    /// <summary>A user logged in.</summary>
    public const string UserLoggedIn = "user_logged_in";

    /// <summary>The user logged out.</summary>
    public const string UserLoggedOut = "user_logged_out";

    /// <summary>The session helper became ready: commands that run as the user can be executed.</summary>
    public const string SessionReady = "session_ready";

    /// <summary>The computer woke up from sleep or hibernation.</summary>
    public const string Resumed = "resumed";

    /// <summary>An event raised by a script or the command line (<c>qoffeeautomat emit</c>); the data carries <c>name</c> and optional <c>data</c>.</summary>
    public const string Custom = "custom";

    /// <summary>
    /// A command run started. Delivered to in-process subscribers (the live view of the UI) but neither
    /// stored nor part of the catalog, so Home Assistant does not see it.
    /// </summary>
    public const string CommandStarted = "command_started";
}

/// <summary>An entry of the event catalog (<c>events</c> in the manifest, PLAN.md S07).</summary>
/// <param name="Type">The event type.</param>
/// <param name="Name">The English display name.</param>
public sealed record EventTypeInfo(string Type, string Name);

/// <summary>The events that Home Assistant can be told about.</summary>
public static class EventCatalog
{
    private static readonly EventTypeInfo[] Entries =
    [
        new(EventTypes.AgentStarted, "Agent started"),
        new(EventTypes.CommandFinished, "Command finished"),
        new(EventTypes.SessionLocked, "Session locked"),
        new(EventTypes.SessionUnlocked, "Session unlocked"),
        new(EventTypes.UserLoggedIn, "User logged in"),
        new(EventTypes.UserLoggedOut, "User logged out"),
        new(EventTypes.SessionReady, "Session ready"),
        new(EventTypes.Resumed, "Resumed"),
        new(EventTypes.Custom, "Custom event"),
    ];

    private static readonly FrozenSet<string> Known = Entries
        .Select(entry => entry.Type)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The catalogued event types in a stable order.</summary>
    public static IReadOnlyList<EventTypeInfo> Types => Entries;

    /// <summary>Tells whether a type is part of the catalog.</summary>
    /// <param name="type">The event type.</param>
    /// <returns>True when the type is catalogued.</returns>
    public static bool Contains(string type)
    {
        return type is not null && Known.Contains(type);
    }
}

/// <summary>An event that happened on the PC.</summary>
/// <param name="Id">The id in the recent-events table; zero for events that are not stored.</param>
/// <param name="Type">The event type, see <see cref="EventTypes"/>.</param>
/// <param name="Timestamp">When the event happened.</param>
/// <param name="Data">Optional data (a JSON object).</param>
public sealed record AgentEvent(long Id, string Type, DateTimeOffset Timestamp, JsonElement? Data = null)
{
    /// <summary>Converts the event to the <c>event</c> frame of QoffeeLink.</summary>
    /// <returns>The message.</returns>
    public EventMessage ToMessage()
    {
        return new EventMessage(Type, Timestamp.ToUnixTimeMilliseconds(), Data);
    }
}
