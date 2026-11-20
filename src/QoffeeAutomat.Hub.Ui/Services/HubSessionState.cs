using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// The session of the browser as the hub pages see it (PLAN.md S09 item 1, S20): who is signed in and with which role. The cookie itself is
/// invisible to the UI (<c>HttpOnly</c>); this class asks the hub and remembers the answer. <c>HubSessionGuard</c> reads it to keep
/// anonymous visitors away from the pages, the menu and the buttons read the role to show only what the role may use, and any page that
/// gets a 401 calls <see cref="MarkSignedOut"/>. It reuses <see cref="SignInStatus"/> of the component library.
/// </summary>
public sealed class HubSessionState
{
    private readonly IHubApi _api;

    /// <summary>Creates the state.</summary>
    /// <param name="api">The hub.</param>
    public HubSessionState(IHubApi api)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
    }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>What is known about the session.</summary>
    public SignInStatus Status { get; private set; }

    /// <summary>The signed-in account, or <c>null</c> when nobody is.</summary>
    public HubSessionInfo? Me { get; private set; }

    /// <summary>The name of the signed-in account.</summary>
    public string? UserName => Me?.Name;

    /// <summary>The role of the signed-in account; <c>null</c> when nobody is signed in.</summary>
    public string? Role => Me?.Role;

    /// <summary>Tells whether the signed-in account may do what a role of the given rank may do.</summary>
    /// <param name="required">The role that the action needs at least: one of <see cref="HubRoleNames"/>.</param>
    /// <returns>True when somebody is signed in and has at least that role.</returns>
    public bool Can(string required) => Status == SignInStatus.SignedIn && HubRoleNames.AtLeast(Role, required);

    /// <summary>
    /// Asks the hub. A valid session wins; without one, setup is offered while the hub has no account, else the login page.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The new status.</returns>
    /// <exception cref="ApiException">The hub refused or could not be reached.</exception>
    public async Task<SignInStatus> RefreshAsync(CancellationToken cancellationToken = default)
    {
        HubSessionInfo? me = await _api.GetMeAsync(cancellationToken);
        if (me is { Authenticated: true })
        {
            Set(SignInStatus.SignedIn, me);
            return Status;
        }

        SetupState? setup = await _api.GetSetupStateAsync(cancellationToken);
        Set(setup is { Available: true } ? SignInStatus.SetupRequired : SignInStatus.SignedOut, null);
        return Status;
    }

    /// <summary>Creates the first administrator and signs in.</summary>
    /// <param name="setupToken">The content of <c>setup-token.txt</c> in the data directory of the hub.</param>
    /// <param name="username">The name of the administrator.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session exists.</returns>
    /// <exception cref="ApiException">The hub refused, for example because the token is wrong.</exception>
    public async Task SetUpAsync(string setupToken, string username, string password, CancellationToken cancellationToken = default)
    {
        HubSessionInfo me = await _api.SetUpAsync(new HubSetupRequest(setupToken, username, password), cancellationToken);
        Set(SignInStatus.SignedIn, me);
    }

    /// <summary>Signs in.</summary>
    /// <param name="username">The user name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session exists.</returns>
    /// <exception cref="ApiException">The hub refused, for example because the password is wrong or the address is locked out.</exception>
    public async Task LogInAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        HubSessionInfo me = await _api.LogInAsync(new HubLoginRequest(username, password), cancellationToken);
        Set(SignInStatus.SignedIn, me);
    }

    /// <summary>Ends the session at the hub and forgets it.</summary>
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

    /// <summary>Records that the hub answered 401: the session ended while the page was open.</summary>
    public void MarkSignedOut()
    {
        if (Status == SignInStatus.SignedIn)
        {
            Set(SignInStatus.SignedOut, null);
        }
    }

    private void Set(SignInStatus status, HubSessionInfo? me)
    {
        bool changed = Status != status;
        Status = status;
        Me = me;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
