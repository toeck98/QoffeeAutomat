using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>A pairing code of the hub with what the token that it will produce is allowed to do.</summary>
/// <param name="Code">The six-digit code.</param>
/// <param name="ExpiresAt">The time the code stops working.</param>
/// <param name="Scope">The scope of the fleet token that the code turns into.</param>
/// <param name="Name">The label of the token; empty when the administrator gave none and the name of the client is used.</param>
/// <param name="CreatedBy">The user that created the code.</param>
public sealed record FleetPairingCode(string Code, DateTimeOffset ExpiresAt, FleetScope Scope, string Name, string CreatedBy)
{
    /// <summary>Hides the code from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the code.</returns>
    public override string ToString() => nameof(FleetPairingCode) + " { ExpiresAt = " + ExpiresAt.ToString("O", CultureInfo.InvariantCulture) + " }";
}

/// <summary>The result of presenting a pairing code.</summary>
public enum FleetPairingAttempt
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

/// <summary>The result of <see cref="FleetPairingService.PairAsync"/>.</summary>
/// <param name="Attempt">How the code was judged.</param>
/// <param name="Token">The issued token with its secret; null unless <paramref name="Attempt"/> is <see cref="FleetPairingAttempt.Accepted"/>.</param>
public sealed record FleetPairingResult(FleetPairingAttempt Attempt, IssuedFleetToken? Token)
{
    /// <summary>True when the code was accepted and a token was issued.</summary>
    public bool Succeeded => Token is not null;
}

/// <summary>
/// The pairing of Home Assistant with the hub (PLAN.md S07 pairing, S20 "Fleet link"): the same code flow as the agent's, one active code of six digits, valid for five
/// minutes, single-use, invalidated after five wrong attempts, where a new code replaces the old one. The code carries the scope of the fleet token that a successful
/// pairing issues, so an administrator decides what a client may see while creating the code, and the client cannot widen it. The code lives in memory only; a hub that
/// restarts forgets it, which is as good as the five minutes that it would have had left.
/// </summary>
public sealed class FleetPairingService
{
    /// <summary>The number of digits.</summary>
    public const int CodeLength = 6;

    /// <summary>The number of wrong attempts after which the active code is invalidated.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>How long a code works.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The client kinds that may pair with the hub (the hub itself is not one of them).</summary>
    public static readonly IReadOnlyList<string> ClientKinds = ["homeassistant", "tool"];

    /// <summary>The longest accepted client name.</summary>
    public const int MaxClientNameLength = 100;

    private readonly FleetTokenService _tokens;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ActiveCode? _active;

    /// <summary>Creates the service.</summary>
    /// <param name="tokens">Issues the token of a successful pairing.</param>
    /// <param name="time">The clock.</param>
    public FleetPairingService(FleetTokenService tokens, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(time);

        _tokens = tokens;
        _time = time;
    }

    /// <summary>Checks the kind and the name of a client.</summary>
    /// <param name="kind">The client kind of the request.</param>
    /// <param name="name">The client name of the request.</param>
    /// <returns>A message that says what is wrong, or null when both are valid.</returns>
    public static string? ValidateClient(string? kind, string? name)
    {
        if (kind is null || !ClientKinds.Contains(kind, StringComparer.Ordinal))
        {
            return "The client kind must be homeassistant or tool.";
        }

        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxClientNameLength || trimmed.Any(char.IsControl))
        {
            return "The client name must have 1 to 100 characters and no control characters.";
        }

        return null;
    }

    /// <summary>Creates a pairing code and makes it the only active one.</summary>
    /// <param name="scope">The scope of the token that the code produces.</param>
    /// <param name="name">The label for the token, or empty to use the name of the client.</param>
    /// <param name="createdBy">The user that creates the code.</param>
    /// <returns>The code.</returns>
    public FleetPairingCode Start(FleetScope scope, string name, string createdBy)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(createdBy);

        lock (_gate)
        {
            // Six digits: 000000 to 999999.
            string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            var created = new FleetPairingCode(code, _time.GetUtcNow() + CodeLifetime, scope, name, createdBy);
            _active = new ActiveCode(created);
            return created;
        }
    }

    /// <summary>Returns the active code.</summary>
    /// <returns>The code, or null when there is none or it expired.</returns>
    public FleetPairingCode? GetActive()
    {
        lock (_gate)
        {
            DropIfExpired();
            return _active?.Code;
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
    /// Presents a code. A right code is accepted exactly once and turns into a fleet token with the scope of the code; a wrong code counts as a failed attempt, and the
    /// <see cref="MaxFailedAttempts"/>th wrong attempt invalidates the active code.
    /// </summary>
    /// <param name="candidate">The code from the client.</param>
    /// <param name="clientKind">The validated client kind; see <see cref="ValidateClient"/>.</param>
    /// <param name="clientName">The validated client name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token, or the reason the code was refused.</returns>
    public async Task<FleetPairingResult> PairAsync(string? candidate, string clientKind, string clientName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientKind);
        ArgumentNullException.ThrowIfNull(clientName);

        (FleetPairingAttempt attempt, FleetPairingCode? redeemed) = TryRedeem(candidate);
        if (attempt != FleetPairingAttempt.Accepted || redeemed is null)
        {
            return new FleetPairingResult(attempt, null);
        }

        string client = clientName.Trim();
        string label = string.IsNullOrWhiteSpace(redeemed.Name) ? client : redeemed.Name;
        IssuedFleetToken issued = await _tokens.IssueAsync(label, redeemed.Scope, clientKind, client, redeemed.CreatedBy, cancellationToken).ConfigureAwait(false);
        return new FleetPairingResult(FleetPairingAttempt.Accepted, issued);
    }

    private (FleetPairingAttempt Attempt, FleetPairingCode? Code) TryRedeem(string? candidate)
    {
        lock (_gate)
        {
            DropIfExpired();
            if (_active is null)
            {
                return (FleetPairingAttempt.NoActiveCode, null);
            }

            if (Matches(_active.Code.Code, candidate))
            {
                FleetPairingCode redeemed = _active.Code;
                _active = null;
                return (FleetPairingAttempt.Accepted, redeemed);
            }

            _active.Failures++;
            if (_active.Failures >= MaxFailedAttempts)
            {
                _active = null;
                return (FleetPairingAttempt.Invalidated, null);
            }

            return (FleetPairingAttempt.WrongCode, null);
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
        if (_active is not null && _time.GetUtcNow() >= _active.Code.ExpiresAt)
        {
            _active = null;
        }
    }

    private sealed class ActiveCode(FleetPairingCode code)
    {
        public FleetPairingCode Code { get; } = code;

        public int Failures { get; set; }
    }
}
