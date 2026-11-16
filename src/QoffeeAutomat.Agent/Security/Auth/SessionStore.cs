using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// The sessions of the management API (PLAN.md S09 item 1), kept in memory: a session ends when the
/// agent restarts, after <see cref="AuthOptions.SessionIdleTimeout"/> without requests and
/// <see cref="AuthOptions.SessionLifetime"/> after sign-in. The session id is 32 random bytes; the store
/// only keeps its SHA-256 hash. All timing uses <see cref="TimeProvider"/>.
/// </summary>
public sealed class SessionStore
{
    private const int IdBytes = 32;
    private const int MaxIdLength = 128;

    private readonly TimeProvider _time;
    private readonly AuthOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>Creates an empty store.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="options">The session limits.</param>
    public SessionStore(TimeProvider time, AuthOptions options)
    {
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
    /// <returns>The session id for the cookie.</returns>
    public string Create()
    {
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

            _sessions[Key(id)] = new Session(now);
        }

        return id;
    }

    /// <summary>Checks a session id and, when it is valid, counts the request as activity.</summary>
    /// <param name="id">The id from the cookie.</param>
    /// <returns>True when the session exists and has not expired.</returns>
    public bool TryValidate(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        string key = Key(id);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out Session? session))
            {
                return false;
            }

            if (IsExpired(session, now))
            {
                _sessions.Remove(key);
                return false;
            }

            session.LastSeenAt = now;
            return true;
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

    /// <summary>Ends all sessions, for example after the password was set again.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            _sessions.Clear();
        }
    }

    /// <summary>Ends all sessions except one, for example after the password was changed.</summary>
    /// <param name="keepId">The id of the session to keep; null keeps none.</param>
    /// <returns>The number of sessions that ended.</returns>
    public int RevokeAllExcept(string? keepId)
    {
        string? keepKey = string.IsNullOrEmpty(keepId) || keepId.Length > MaxIdLength ? null : Key(keepId);
        lock (_gate)
        {
            int before = _sessions.Count;
            foreach (string other in _sessions.Keys.Where(candidate => !string.Equals(candidate, keepKey, StringComparison.Ordinal)).ToList())
            {
                _sessions.Remove(other);
            }

            return before - _sessions.Count;
        }
    }

    private static string Key(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private bool IsExpired(Session session, DateTimeOffset now)
    {
        return now - session.LastSeenAt > _options.SessionIdleTimeout
            || now - session.CreatedAt > _options.SessionLifetime;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string key in _sessions.Where(pair => IsExpired(pair.Value, now)).Select(pair => pair.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }

    private sealed class Session
    {
        public Session(DateTimeOffset now)
        {
            CreatedAt = now;
            LastSeenAt = now;
        }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset LastSeenAt { get; set; }
    }
}
