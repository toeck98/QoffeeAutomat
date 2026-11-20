using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Security;

/// <summary>The outcome of the first-run setup.</summary>
public enum SetupOutcome
{
    /// <summary>The administrator was created.</summary>
    Done,

    /// <summary>The hub already has an account; setup is not available any more.</summary>
    Unavailable,

    /// <summary>The setup token is wrong.</summary>
    InvalidToken,

    /// <summary>The user name does not follow the rules.</summary>
    InvalidUsername,

    /// <summary>The password does not follow the rules.</summary>
    WeakPassword,

    /// <summary>Too many failed attempts; try again later.</summary>
    LockedOut,
}

/// <summary>The result of <see cref="SetupService.SetupAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="User">The new administrator, when <paramref name="Outcome"/> is <see cref="SetupOutcome.Done"/>.</param>
/// <param name="Message">A message for the user, for a rule that was not followed.</param>
/// <param name="RetryAfter">The time left of a lockout.</param>
public sealed record SetupResult(SetupOutcome Outcome, HubUser? User = null, string? Message = null, TimeSpan? RetryAfter = null);

/// <summary>
/// First-run setup (PLAN.md S09 item 1): while the hub has no account, the one-time setup token creates the first administrator, and
/// that ends the setup. Setup is rate limited like a sign-in, and two setups at the same time cannot both create an account.
/// </summary>
public sealed class SetupService
{
    /// <summary>The scope of the attempt limiter for setup attempts.</summary>
    public const string LimiterScope = "setup";

    private readonly UserService _users;
    private readonly SetupTokenService _token;
    private readonly LoginRateLimiter _limiter;
    private readonly ILogger<SetupService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="users">The accounts.</param>
    /// <param name="token">The setup token file.</param>
    /// <param name="limiter">The limiter of failed attempts.</param>
    /// <param name="logger">The logger.</param>
    public SetupService(UserService users, SetupTokenService token, LoginRateLimiter limiter, ILogger<SetupService> logger)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(logger);

        _users = users;
        _token = token;
        _limiter = limiter;
        _logger = logger;
    }

    /// <summary>Tells whether setup is possible: the hub has no account yet.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True while there is no account.</returns>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        return await _users.CountAsync(cancellationToken).ConfigureAwait(false) == 0;
    }

    /// <summary>Creates the first administrator.</summary>
    /// <param name="caller">The client address, for the attempt limit.</param>
    /// <param name="setupToken">The token from <c>setup-token.txt</c>.</param>
    /// <param name="username">The name of the administrator.</param>
    /// <param name="password">The password of the administrator.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public async Task<SetupResult> SetupAsync(string caller, string setupToken, string username, string password, CancellationToken cancellationToken)
    {
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SetupResult(SetupOutcome.Unavailable);
        }

        if (_limiter.GetLockout(LimiterScope, caller) is { } lockout)
        {
            return new SetupResult(SetupOutcome.LockedOut, RetryAfter: lockout);
        }

        if (!await _token.VerifyAsync(setupToken, cancellationToken).ConfigureAwait(false))
        {
            _limiter.RecordFailure(LimiterScope, caller);
            return new SetupResult(SetupOutcome.InvalidToken);
        }

        if (UserService.ValidateUsername(username) is { } usernameProblem)
        {
            return new SetupResult(SetupOutcome.InvalidUsername, Message: usernameProblem);
        }

        if (_users.ValidatePassword(password, username) is { } passwordProblem)
        {
            return new SetupResult(SetupOutcome.WeakPassword, Message: passwordProblem);
        }

        HubUser? administrator = await _users.CreateFirstAdministratorAsync(username, password, cancellationToken).ConfigureAwait(false);
        if (administrator is null)
        {
            return new SetupResult(SetupOutcome.Unavailable);
        }

        _limiter.Reset(LimiterScope, caller);
        try
        {
            _token.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Setup is closed now (an account exists), so a token file that stays is harmless; it is removed at the next start.
            SecurityLog.SetupTokenNotDeleted(_logger, _token.FilePath);
        }

        return new SetupResult(SetupOutcome.Done, administrator);
    }
}
