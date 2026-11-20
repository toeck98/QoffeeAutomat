using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace QoffeeAutomat.Hub.Security;

/// <summary>The account behind a valid session cookie.</summary>
/// <param name="UserId">The id of the account.</param>
/// <param name="Username">The sign-in name.</param>
/// <param name="Role">The role at the time of sign-in; a change of the role ends the sessions of the account.</param>
public sealed record HubSession(string UserId, string Username, string Role);

/// <summary>
/// The cookie sessions of the hub (PLAN.md S09 item 1, S20), kept in memory: a session ends when the hub restarts, after
/// <see cref="HubAuthOptions.SessionIdleTimeout"/> without requests and <see cref="HubAuthOptions.SessionLifetime"/> after sign-in. The
/// session id is 32 random bytes; the store only keeps its SHA-256 hash. When the role, the disabled flag or the password of an account
/// changes, or the account is deleted, its sessions end (<see cref="RevokeUser"/>). All timing uses <see cref="TimeProvider"/>.
/// </summary>
public sealed class HubSessionStore
{
    private const int IdBytes = 32;
    private const int MaxIdLength = 128;

    private readonly TimeProvider _time;
    private readonly HubAuthOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    /// <summary>Creates an empty store.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="options">The session limits.</param>
    public HubSessionStore(TimeProvider time, HubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _time = time;
        _options = options;
    }

    /// <summary>The number of live sessions.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(_time.GetUtcNow());
                return _sessions.Count;
            }
        }
    }

    /// <summary>Starts a session.</summary>
    /// <param name="session">The account behind it.</param>
    /// <returns>The session id for the cookie.</returns>
    public string Create(HubSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        string id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(IdBytes));
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            RemoveExpired(now);
            while (_sessions.Count > 0 && _sessions.Count >= _options.MaxSessions)
            {
                string oldest = _sessions.MinBy(pair => pair.Value.CreatedAt).Key;
                _sessions.Remove(oldest);
            }

            _sessions[Key(id)] = new Entry(session, now);
        }

        return id;
    }

    /// <summary>Checks a session id and, when it is valid, counts the request as activity.</summary>
    /// <param name="id">The id from the cookie.</param>
    /// <returns>The account, or null when the session does not exist or has expired.</returns>
    public HubSession? TryValidate(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return null;
        }

        string key = Key(id);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out Entry? entry))
            {
                return null;
            }

            if (IsExpired(entry, now))
            {
                _sessions.Remove(key);
                return null;
            }

            entry.LastSeenAt = now;
            return entry.Session;
        }
    }

    /// <summary>Ends a session.</summary>
    /// <param name="id">The id from the cookie; an unknown id is ignored.</param>
    public void Revoke(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return;
        }

        lock (_gate)
        {
            _sessions.Remove(Key(id));
        }
    }

    /// <summary>Ends the sessions of an account, optionally keeping one.</summary>
    /// <param name="userId">The id of the account.</param>
    /// <param name="keepId">The id of the session to keep (the one that changed the password); null keeps none.</param>
    /// <returns>The number of sessions that ended.</returns>
    public int RevokeUser(string userId, string? keepId = null)
    {
        ArgumentNullException.ThrowIfNull(userId);

        string? keepKey = string.IsNullOrEmpty(keepId) || keepId.Length > MaxIdLength ? null : Key(keepId);
        lock (_gate)
        {
            string[] doomed =
            [
                .. _sessions
                    .Where(pair => string.Equals(pair.Value.Session.UserId, userId, StringComparison.Ordinal)
                        && !string.Equals(pair.Key, keepKey, StringComparison.Ordinal))
                    .Select(pair => pair.Key),
            ];
            foreach (string key in doomed)
            {
                _sessions.Remove(key);
            }

            return doomed.Length;
        }
    }

    private static string Key(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private bool IsExpired(Entry entry, DateTimeOffset now)
    {
        return now - entry.LastSeenAt > _options.SessionIdleTimeout
            || now - entry.CreatedAt > _options.SessionLifetime;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string key in _sessions.Where(pair => IsExpired(pair.Value, now)).Select(pair => pair.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }

    private sealed class Entry
    {
        public Entry(HubSession session, DateTimeOffset now)
        {
            Session = session;
            CreatedAt = now;
            LastSeenAt = now;
        }

        public HubSession Session { get; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset LastSeenAt { get; set; }
    }
}
