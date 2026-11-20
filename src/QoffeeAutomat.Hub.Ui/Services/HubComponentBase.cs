using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// Base class of the hub pages: the localized texts of the component library (<c>L</c>) and of the hub (<c>H</c>), the hub API, the session, and
/// <see cref="TryAsync"/>, which runs a call the way every hub page wants it run: a 401 ends the session (the guard then shows the login page), any
/// other error is kept in <see cref="Error"/> for the <c>ErrorDisplay</c> of the page.
/// </summary>
public abstract class HubComponentBase : LocalizedComponentBase
{
    private bool _sessionSubscribed;

    /// <summary>The hub texts.</summary>
    [Inject]
    protected HubText H { get; set; } = null!;

    /// <summary>The hub API.</summary>
    [Inject]
    protected IHubApi Api { get; set; } = null!;

    /// <summary>The session of the browser at the hub.</summary>
    [Inject]
    protected HubSessionState HubSession { get; set; } = null!;

    /// <summary>The browser: clipboard and storage.</summary>
    [Inject]
    protected IBrowserInterop Browser { get; set; } = null!;

    /// <summary>The navigation.</summary>
    [Inject]
    protected NavigationManager Navigation { get; set; } = null!;

    /// <summary>The error of the last call that failed; <c>null</c> when the last call worked.</summary>
    protected ApiException? Error { get; set; }

    /// <summary>True while a call started by <see cref="TryAsync"/> runs; buttons use it to avoid a second click.</summary>
    protected bool Busy { get; private set; }

    /// <inheritdoc />
    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_sessionSubscribed)
        {
            // The injected services are set before the first call. A page shows what its role may use, so it follows a change of the session.
            _sessionSubscribed = true;
            HubSession.Changed += OnSessionChanged;
        }

        return base.SetParametersAsync(parameters);
    }

    /// <summary>Tells whether the signed-in user has at least the role.</summary>
    /// <param name="role">One of <see cref="HubRoleNames"/>.</param>
    /// <returns>True when the user may use what the role may use.</returns>
    protected bool Can(string role) => HubSession.Can(role);

    /// <summary>Runs a call and keeps its error.</summary>
    /// <param name="action">The call.</param>
    /// <returns>True when the call worked; false when it failed (<see cref="Error"/> says why, or the session ended).</returns>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Error = null;
        Busy = true;
        try
        {
            await action();
            return true;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            HubSession.MarkSignedOut();
            return false;
        }
        catch (ApiException exception)
        {
            Error = exception;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync()
    {
        if (_sessionSubscribed)
        {
            HubSession.Changed -= OnSessionChanged;
        }

        return base.OnDisposeAsync();
    }

    /// <summary>Copies a text to the clipboard.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True when the browser copied it.</returns>
    protected async Task<bool> CopyAsync(string text)
    {
        return await Browser.CopyTextAsync(text);
    }

    /// <summary>Formats a time for the user.</summary>
    /// <param name="time">The time; <c>null</c> shows "never".</param>
    /// <returns>The local time in the language of the UI.</returns>
    protected string Time(DateTimeOffset? time)
    {
        return time is { } value
            ? DashboardFormat.LocalTime(value, L.Culture)
            : L["Common.Never"];
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }
}
