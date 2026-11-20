namespace QoffeeAutomat.Hub.Security;

/// <summary>
/// Counts failed sign-in and setup attempts and locks out (PLAN.md S09 item 1, S20 "Hub security" item 6). Each caller (the client
/// address as the hub sees it, which is the forwarded address when the request came through a trusted proxy) has its own counter, and a
/// second counter over all callers limits attempts that are spread over many addresses. After
/// <see cref="HubAuthOptions.MaxFailedAttempts"/> failures within <see cref="HubAuthOptions.FailureWindow"/> the caller is locked out for
/// <see cref="HubAuthOptions.LockoutDuration"/>; a success clears the caller's counter. All timing uses <see cref="TimeProvider"/>.
/// </summary>
public sealed class LoginRateLimiter
{
    private const string OverallKey = "*";
    private const int MaxEntries = 1024;

    private readonly TimeProvider _time;
    private readonly HubAuthOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates a limiter without any failures.</summary>
    /// <param name="time">The clock.</param>
    /// <param name="options">The limits.</param>
    public LoginRateLimiter(TimeProvider time, HubAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);

        _time = time;
        _options = options;
    }

    /// <summary>Tells whether a caller may try now.</summary>
    /// <param name="scope">What is being attempted, for example <c>login</c> or <c>setup</c>.</param>
    /// <param name="caller">The caller, normally the client address.</param>
    /// <returns>The time left of the lockout, or null when the caller may try.</returns>
    public TimeSpan? GetLockout(string scope, string caller)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            TimeSpan remaining = TimeSpan.Zero;
            foreach (string key in new[] { Key(scope, caller), Key(scope, OverallKey) })
            {
                if (_entries.TryGetValue(key, out Entry? entry) && entry.LockedUntil > now && entry.LockedUntil - now > remaining)
                {
                    remaining = entry.LockedUntil - now;
                }
            }

            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    /// <summary>Records a failed attempt.</summary>
    /// <param name="scope">What was attempted.</param>
    /// <param name="caller">The caller.</param>
    public void RecordFailure(string scope, string caller)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            Count(Key(scope, caller), _options.MaxFailedAttempts, now);
            Count(Key(scope, OverallKey), _options.MaxFailedAttemptsOverall, now);
        }
    }

    /// <summary>Clears the failures of a caller after a success.</summary>
    /// <param name="scope">What was attempted.</param>
    /// <param name="caller">The caller.</param>
    public void Reset(string scope, string caller)
    {
        lock (_gate)
        {
            _entries.Remove(Key(scope, caller));
        }
    }

    private static string Key(string scope, string caller) => scope + "|" + caller;

    private void Count(string key, int maxFailures, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry(now);
            _entries[key] = entry;
        }

        if (entry.LockedUntil > now)
        {
            return;
        }

        if (now - entry.WindowStart > _options.FailureWindow)
        {
            entry.WindowStart = now;
            entry.Failures = 0;
        }

        entry.Failures++;
        if (entry.Failures >= maxFailures)
        {
            entry.LockedUntil = now + _options.LockoutDuration;
            entry.Failures = 0;
            entry.WindowStart = now;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count < MaxEntries)
        {
            return;
        }

        foreach (string key in _entries.Where(pair => pair.Value.LockedUntil <= now && now - pair.Value.WindowStart > _options.FailureWindow).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }

        // Still full: addresses that keep failing; forget the oldest counters so memory stays bounded.
        while (_entries.Count >= MaxEntries)
        {
            string oldest = _entries.MinBy(pair => pair.Value.WindowStart).Key;
            _entries.Remove(oldest);
        }
    }

    private sealed class Entry
    {
        public Entry(DateTimeOffset now)
        {
            WindowStart = now;
        }

        public int Failures { get; set; }

        public DateTimeOffset WindowStart { get; set; }

        public DateTimeOffset LockedUntil { get; set; }
    }
}
