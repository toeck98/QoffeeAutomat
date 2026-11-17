using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>What became of a presented one-time login token.</summary>
public enum OneTimeLoginOutcome
{
    /// <summary>The token was valid; it is used up now and a session may start.</summary>
    Accepted,

    /// <summary>The token is unknown, malformed or was used before.</summary>
    Unknown,

    /// <summary>The token is known but its 30 seconds are over. It is gone now.</summary>
    Expired,

    /// <summary>The token was presented from an address that is not loopback. It is gone now: a token that left this PC is spent.</summary>
    NotLoopback,
}

/// <summary>
/// Single-use login tokens for the tray entry Open UI (PLAN.md S09 item 12, T711). When the interactive user is a local
/// administrator, the tray asks the agent over the session pipe for a token and opens the browser with it, so the user does not
/// type the password of the management UI. A token is 32 random bytes, kept only as its SHA-256 hash and in memory, valid for
/// <see cref="Lifetime"/>, redeemable once and only from a loopback address. Whether a caller may get a token is decided
/// before <see cref="Issue"/> is called; this class only issues and redeems. All timing uses <see cref="TimeProvider"/>.
/// </summary>
public sealed class OneTimeLoginService : ILoginTokenIssuer
{
    /// <summary>How long a token is valid after it was issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    /// <summary>The most tokens that wait to be redeemed; the oldest is dropped beyond that, so a loop cannot grow the memory.</summary>
    public const int MaxPending = 8;

    private const int TokenBytes = 32;
    private const int MaxTokenLength = 128;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    /// <summary>Creates the service.</summary>
    /// <param name="time">The clock.</param>
    public OneTimeLoginService(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
    }

    /// <summary>The number of tokens that have been issued and neither used nor expired.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(_time.GetUtcNow());
                return _pending.Count;
            }
        }
    }

    /// <inheritdoc />
    public string Issue()
    {
        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            RemoveExpired(now);
            while (_pending.Count >= MaxPending)
            {
                string oldest = _pending.MinBy(pair => pair.Value).Key;
                _pending.Remove(oldest);
            }

            _pending[Key(token)] = now + Lifetime;
        }

        return token;
    }

    /// <summary>Uses a token up and tells whether a session may start.</summary>
    /// <param name="token">The token from the address that the browser opened.</param>
    /// <param name="remote">The address of the caller as the web server saw it; null (unknown) never counts as loopback.</param>
    /// <returns>The outcome. Every known token is removed whatever the outcome, so it can never be tried twice.</returns>
    public OneTimeLoginOutcome Redeem(string? token, IPAddress? remote)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return OneTimeLoginOutcome.Unknown;
        }

        string key = Key(token);
        DateTimeOffset now = _time.GetUtcNow();

        DateTimeOffset expiresAt;
        lock (_gate)
        {
            RemoveExpiredExcept(now, key);
            if (!_pending.Remove(key, out expiresAt))
            {
                return OneTimeLoginOutcome.Unknown;
            }
        }

        if (now > expiresAt)
        {
            return OneTimeLoginOutcome.Expired;
        }

        return remote is not null && IPAddress.IsLoopback(remote)
            ? OneTimeLoginOutcome.Accepted
            : OneTimeLoginOutcome.NotLoopback;
    }

    private static string Key(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string key in _pending.Where(pair => now > pair.Value).Select(pair => pair.Key).ToList())
        {
            _pending.Remove(key);
        }
    }

    // The token that is being redeemed stays so that a late presentation is reported as expired, not as unknown.
    private void RemoveExpiredExcept(DateTimeOffset now, string keep)
    {
        foreach (string key in _pending.Where(pair => now > pair.Value && !string.Equals(pair.Key, keep, StringComparison.Ordinal)).Select(pair => pair.Key).ToList())
        {
            _pending.Remove(key);
        }
    }
}
