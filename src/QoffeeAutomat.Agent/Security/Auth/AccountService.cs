namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>How a setup, sign-in or password change attempt ended.</summary>
public enum AccountOutcome
{
    /// <summary>The operation succeeded.</summary>
    Done,

    /// <summary>Setup is not available (a password exists and no setup token file does).</summary>
    SetupUnavailable,

    /// <summary>The setup token is wrong.</summary>
    InvalidSetupToken,

    /// <summary>The password or current password is wrong.</summary>
    InvalidCredentials,

    /// <summary>The new password does not satisfy the policy; see <see cref="AccountResult.Message"/>.</summary>
    WeakPassword,

    /// <summary>Too many failed attempts; see <see cref="AccountResult.RetryAfter"/>.</summary>
    LockedOut,
}

/// <summary>The result of an account operation.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="SessionId">The new session id after a successful setup or sign-in; null otherwise.</param>
/// <param name="RetryAfter">How long the lockout lasts when <paramref name="Outcome"/> is <see cref="AccountOutcome.LockedOut"/>.</param>
/// <param name="Message">A message for <see cref="AccountOutcome.WeakPassword"/>.</param>
public sealed record AccountResult(AccountOutcome Outcome, string? SessionId = null, TimeSpan? RetryAfter = null, string? Message = null)
{
    /// <summary>True when the operation succeeded.</summary>
    public bool Succeeded => Outcome == AccountOutcome.Done;
}

/// <summary>
/// The account logic of the management API (PLAN.md S08, S09 item 1): first-run setup with the setup
/// token, sign-in with rate limiting and lockout, and password change. The setup is available while the
/// setup token file exists; the agent writes it on the first run and <c>reset-password</c> writes it
/// again. Setting a password deletes the file and ends all sessions.
/// </summary>
public sealed class AccountService : IDisposable
{
    /// <summary>The limiter scope of sign-in and password change attempts.</summary>
    public const string LoginScope = "login";

    /// <summary>The limiter scope of setup attempts.</summary>
    public const string SetupScope = "setup";

    private readonly PasswordService _passwords;
    private readonly SetupTokenService _setupToken;
    private readonly SessionStore _sessions;
    private readonly AttemptLimiter _limiter;
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    private readonly SemaphoreSlim _passwordGate = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="passwords">The password hash.</param>
    /// <param name="setupToken">The setup token file.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="limiter">The attempt limiter.</param>
    public AccountService(PasswordService passwords, SetupTokenService setupToken, SessionStore sessions, AttemptLimiter limiter)
    {
        _passwords = passwords;
        _setupToken = setupToken;
        _sessions = sessions;
        _limiter = limiter;
    }

    /// <summary>
    /// Tells whether first-run setup is possible. If no password exists and the token file is missing
    /// (it was deleted by hand), a new token file is written, so the agent can always be set up.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the setup token file exists.</returns>
    public async Task<bool> IsSetupAvailableAsync(CancellationToken cancellationToken)
    {
        if (_setupToken.Exists())
        {
            return true;
        }

        if (await _passwords.HasPasswordAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _setupToken.CreateAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sets the password with the setup token and signs in.</summary>
    /// <param name="caller">The remote address, for the attempt limit.</param>
    /// <param name="setupToken">The token from the token file.</param>
    /// <param name="password">The new password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome; on success it carries the new session id.</returns>
    public async Task<AccountResult> SetupAsync(string caller, string setupToken, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(setupToken);
        ArgumentNullException.ThrowIfNull(password);

        // One setup at a time: the second of two parallel requests finds the token file gone.
        await _setupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await IsSetupAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return new AccountResult(AccountOutcome.SetupUnavailable);
            }

            if (_limiter.GetLockout(SetupScope, caller) is { } lockout)
            {
                return new AccountResult(AccountOutcome.LockedOut, RetryAfter: lockout);
            }

            if (!await _setupToken.VerifyAsync(setupToken, cancellationToken).ConfigureAwait(false))
            {
                _limiter.RecordFailure(SetupScope, caller);
                return _limiter.GetLockout(SetupScope, caller) is { } started
                    ? new AccountResult(AccountOutcome.LockedOut, RetryAfter: started)
                    : new AccountResult(AccountOutcome.InvalidSetupToken);
            }

            string? problem = _passwords.ValidatePolicy(password);
            if (problem is not null)
            {
                return new AccountResult(AccountOutcome.WeakPassword, Message: problem);
            }

            await _passwords.SetPasswordAsync(password, cancellationToken).ConfigureAwait(false);
            _setupToken.Delete();
            _sessions.RevokeAll();
            _limiter.Reset(SetupScope, caller);
            return new AccountResult(AccountOutcome.Done, SessionId: _sessions.Create());
        }
        finally
        {
            _setupGate.Release();
        }
    }

    /// <summary>Signs in with the password.</summary>
    /// <param name="caller">The remote address, for the attempt limit.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome; on success it carries the new session id.</returns>
    public async Task<AccountResult> LoginAsync(string caller, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(password);

        // One password check at a time (security review T606): the lockout is checked before and recorded after the
        // slow hash, so without this gate a burst of parallel requests would all pass the check before the first
        // failure is counted and could try far more than the allowed number of passwords.
        await _passwordGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_limiter.GetLockout(LoginScope, caller) is { } lockout)
            {
                return new AccountResult(AccountOutcome.LockedOut, RetryAfter: lockout);
            }

            if (!await _passwords.VerifyAsync(password, cancellationToken).ConfigureAwait(false))
            {
                return Failed(caller);
            }

            _limiter.Reset(LoginScope, caller);
            return new AccountResult(AccountOutcome.Done, SessionId: _sessions.Create());
        }
        finally
        {
            _passwordGate.Release();
        }
    }

    /// <summary>Changes the password; all other sessions end.</summary>
    /// <param name="caller">The remote address, for the attempt limit.</param>
    /// <param name="currentSessionId">The caller's session, which stays valid.</param>
    /// <param name="currentPassword">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public async Task<AccountResult> ChangePasswordAsync(
        string caller,
        string? currentSessionId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        // The same gate as the sign-in: both check the current password against the same lockout counter.
        await _passwordGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_limiter.GetLockout(LoginScope, caller) is { } lockout)
            {
                return new AccountResult(AccountOutcome.LockedOut, RetryAfter: lockout);
            }

            if (!await _passwords.VerifyAsync(currentPassword, cancellationToken).ConfigureAwait(false))
            {
                return Failed(caller);
            }

            string? problem = _passwords.ValidatePolicy(newPassword);
            if (problem is not null)
            {
                return new AccountResult(AccountOutcome.WeakPassword, Message: problem);
            }

            await _passwords.SetPasswordAsync(newPassword, cancellationToken).ConfigureAwait(false);
            _sessions.RevokeAllExcept(currentSessionId);
            _limiter.Reset(LoginScope, caller);
            return new AccountResult(AccountOutcome.Done);
        }
        finally
        {
            _passwordGate.Release();
        }
    }

    /// <summary>Releases the locks of the setup and of the password checks.</summary>
    public void Dispose()
    {
        _setupGate.Dispose();
        _passwordGate.Dispose();
    }

    private AccountResult Failed(string caller)
    {
        _limiter.RecordFailure(LoginScope, caller);
        return _limiter.GetLockout(LoginScope, caller) is { } started
            ? new AccountResult(AccountOutcome.LockedOut, RetryAfter: started)
            : new AccountResult(AccountOutcome.InvalidCredentials);
    }
}
