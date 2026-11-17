using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>Where the user stands towards the agent.</summary>
public enum SignInStatus
{
    /// <summary>Not asked yet.</summary>
    Unknown = 0,

    /// <summary>No password exists yet: the first-run setup page is next.</summary>
    SetupRequired,

    /// <summary>A password exists and this browser has no valid session: the login page is next.</summary>
    SignedOut,

    /// <summary>The browser has a valid session.</summary>
    SignedIn,
}

/// <summary>
/// The session of the browser as the pages see it (PLAN.md S08, S09 item 1). The cookie itself is invisible to the
/// UI (<c>HttpOnly</c>); this class asks the agent and remembers the answer. <c>SessionGuard</c> reads it to keep
/// anonymous visitors away from the pages, and any page that gets a 401 calls <see cref="MarkSignedOut"/>.
/// </summary>
public sealed class SessionState
{
    private readonly IAgentApi _api;

    /// <summary>Creates the state.</summary>
    /// <param name="api">The agent.</param>
    public SessionState(IAgentApi api)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
    }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>What is known about the session.</summary>
    public SignInStatus Status { get; private set; }

    /// <summary>The name of the signed-in account.</summary>
    public string? UserName { get; private set; }

    /// <summary>
    /// Asks the agent. A valid session wins; without one, setup is offered while the agent still has no password (or a
    /// reset wrote a new setup token), else the login page.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The new status.</returns>
    /// <exception cref="ApiException">The agent refused or could not be reached (for example <c>remote_ui_disabled</c>).</exception>
    public async Task<SignInStatus> RefreshAsync(CancellationToken cancellationToken = default)
    {
        AuthMeResponse? me = await _api.GetMeAsync(cancellationToken);
        if (me is { Authenticated: true })
        {
            Set(SignInStatus.SignedIn, me.Name);
            return Status;
        }

        SetupState? setup = await _api.GetSetupStateAsync(cancellationToken);
        Set(setup is { Available: true } ? SignInStatus.SetupRequired : SignInStatus.SignedOut, null);
        return Status;
    }

    /// <summary>Sets the first password and signs in.</summary>
    /// <param name="setupToken">The content of <c>secrets/setup-token.txt</c>.</param>
    /// <param name="password">The new password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session exists.</returns>
    /// <exception cref="ApiException">The agent refused, for example because the token is wrong.</exception>
    public async Task SetUpAsync(string setupToken, string password, CancellationToken cancellationToken = default)
    {
        AuthMeResponse me = await _api.SetUpAsync(new SetupRequest(setupToken, password), cancellationToken);
        Set(SignInStatus.SignedIn, me.Name);
    }

    /// <summary>Signs in with the password.</summary>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session exists.</returns>
    /// <exception cref="ApiException">The agent refused, for example because the password is wrong or the address is locked out.</exception>
    public async Task LogInAsync(string password, CancellationToken cancellationToken = default)
    {
        AuthMeResponse me = await _api.LogInAsync(new LoginRequest(password), cancellationToken);
        Set(SignInStatus.SignedIn, me.Name);
    }

    /// <summary>Ends the session at the agent and forgets it.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session is gone.</returns>
    public async Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.LogOutAsync(cancellationToken);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            // The session was gone already.
        }

        Set(SignInStatus.SignedOut, null);
    }

    /// <summary>Records that the agent answered 401: the session ended while the page was open.</summary>
    public void MarkSignedOut()
    {
        if (Status == SignInStatus.SignedIn)
        {
            Set(SignInStatus.SignedOut, null);
        }
    }

    private void Set(SignInStatus status, string? userName)
    {
        bool changed = Status != status;
        Status = status;
        UserName = userName;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
