using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QoffeeAutomat.Agent.Link.Pairing;

/// <summary>A pairing code and the time it stops working.</summary>
/// <param name="Code">The six-digit code.</param>
/// <param name="ExpiresAt">The expiry time.</param>
public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>The result of presenting a code.</summary>
public enum PairingAttempt
{
    /// <summary>The code was right; it has been used up.</summary>
    Accepted,

    /// <summary>There is no active code, or it had expired.</summary>
    NoActiveCode,

    /// <summary>The code was wrong; the active code is still valid.</summary>
    WrongCode,

    /// <summary>The code was wrong and it was the last attempt allowed; the active code is gone.</summary>
    Invalidated,
}

/// <summary>
/// Holds the one active pairing code (PLAN.md S07, D-016): six digits, valid for five minutes,
/// single-use, invalidated after five wrong attempts. Starting a new code replaces the old one, so only
/// one code is ever active. All timing uses <see cref="TimeProvider"/>.
/// </summary>
public sealed class PairingCodeManager
{
    /// <summary>The number of digits.</summary>
    public const int CodeLength = 6;

    /// <summary>The number of wrong attempts after which the active code is invalidated.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>How long a code works.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ActiveCode? _active;

    /// <summary>Creates a manager without an active code.</summary>
    /// <param name="time">The clock.</param>
    public PairingCodeManager(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _time = time;
    }

    /// <summary>Creates a new code and makes it the only active one.</summary>
    /// <returns>The code and its expiry time.</returns>
    public PairingCode Start()
    {
        lock (_gate)
        {
            // Six digits: 000000 to 999999.
            string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            DateTimeOffset expiresAt = _time.GetUtcNow() + CodeLifetime;
            _active = new ActiveCode(code, expiresAt);
            return new PairingCode(code, expiresAt);
        }
    }

    /// <summary>Returns the active code.</summary>
    /// <returns>The code, or null when there is none or it expired.</returns>
    public PairingCode? GetActive()
    {
        lock (_gate)
        {
            DropIfExpired();
            return _active is null ? null : new PairingCode(_active.Code, _active.ExpiresAt);
        }
    }

    /// <summary>Discards the active code.</summary>
    /// <returns>True when a code was active.</returns>
    public bool Cancel()
    {
        lock (_gate)
        {
            DropIfExpired();
            bool wasActive = _active is not null;
            _active = null;
            return wasActive;
        }
    }

    /// <summary>
    /// Presents a code. A right code is accepted exactly once; a wrong code counts as a failed attempt,
    /// and the <see cref="MaxFailedAttempts"/>th wrong attempt invalidates the active code.
    /// </summary>
    /// <param name="candidate">The code from the client.</param>
    /// <returns>What happened.</returns>
    public PairingAttempt TryRedeem(string? candidate)
    {
        lock (_gate)
        {
            DropIfExpired();
            if (_active is null)
            {
                return PairingAttempt.NoActiveCode;
            }

            if (Matches(_active.Code, candidate))
            {
                _active = null;
                return PairingAttempt.Accepted;
            }

            _active.Failures++;
            if (_active.Failures >= MaxFailedAttempts)
            {
                _active = null;
                return PairingAttempt.Invalidated;
            }

            return PairingAttempt.WrongCode;
        }
    }

    private static bool Matches(string code, string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(code);
        byte[] presented = Encoding.UTF8.GetBytes(candidate.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    private void DropIfExpired()
    {
        if (_active is not null && _time.GetUtcNow() >= _active.ExpiresAt)
        {
            _active = null;
        }
    }

    private sealed class ActiveCode
    {
        public ActiveCode(string code, DateTimeOffset expiresAt)
        {
            Code = code;
            ExpiresAt = expiresAt;
        }

        public string Code { get; }

        public DateTimeOffset ExpiresAt { get; }

        public int Failures { get; set; }
    }
}
