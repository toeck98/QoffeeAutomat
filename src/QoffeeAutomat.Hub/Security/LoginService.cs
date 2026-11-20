namespace QoffeeAutomat.Hub.Security;

/// <summary>The outcome of a sign-in.</summary>
public enum LoginOutcome
{
    /// <summary>Signed in; the result carries the session.</summary>
    Done,

    /// <summary>The name is unknown, the account is disabled or the password is wrong. The caller is not told which.</summary>
    InvalidCredentials,

    /// <summary>Too many failed attempts; try again later.</summary>
    LockedOut,
}

/// <summary>The result of <see cref="LoginService.LoginAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="User">The account, when signed in.</param>
/// <param name="SessionId">The session id for the cookie, when signed in.</param>
/// <param name="RetryAfter">The time left of a lockout.</param>
public sealed record LoginResult(LoginOutcome Outcome, HubUser? User = null, string? SessionId = null, TimeSpan? RetryAfter = null)
{
    /// <summary>Hides the session id from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the session id.</returns>
    public override string ToString() => nameof(LoginResult) + " { Outcome = " + Outcome + " }";
}

/// <summary>Sign-in with a user name and a password (PLAN.md S09 item 1, S20 "Hub security" items 1 and 6): rate limited, with lockout.</summary>
public sealed class LoginService
{
    /// <summary>The scope of the attempt limiter for sign-ins.</summary>
    public const string LimiterScope = "login";

    private readonly UserService _users;
    private readonly HubSessionStore _sessions;
    private readonly LoginRateLimiter _limiter;

    /// <summary>Creates the service.</summary>
    /// <param name="users">The accounts.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="limiter">The limiter of failed attempts.</param>
    public LoginService(UserService users, HubSessionStore sessions, LoginRateLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(limiter);

        _users = users;
        _sessions = sessions;
        _limiter = limiter;
    }

    /// <summary>Signs in.</summary>
    /// <param name="caller">The client address, for the attempt limit.</param>
    /// <param name="username">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public async Task<LoginResult> LoginAsync(string caller, string username, string password, CancellationToken cancellationToken)
    {
        if (_limiter.GetLockout(LimiterScope, caller) is { } lockout)
        {
            return new LoginResult(LoginOutcome.LockedOut, RetryAfter: lockout);
        }

        HubUser? user = await _users.AuthenticateAsync(username, password, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _limiter.RecordFailure(LimiterScope, caller);
            return new LoginResult(LoginOutcome.InvalidCredentials);
        }

        _limiter.Reset(LimiterScope, caller);
        string sessionId = _sessions.Create(new HubSession(user.Id, user.Username, user.Role));
        return new LoginResult(LoginOutcome.Done, user, sessionId);
    }
}
