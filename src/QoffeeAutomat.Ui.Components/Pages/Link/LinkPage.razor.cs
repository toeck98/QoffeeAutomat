using System.Globalization;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Link;

/// <summary>
/// The Home Assistant link page (PLAN.md T406): the Pair button that opens the pairing dialog, the list of paired clients
/// with revoke (after a confirmation), and the switch for the zeroconf announcement, which saves at once and springs back when
/// the agent refuses. The list is read again while the page is open, so that connections and the last seen time stay current.
/// </summary>
public partial class LinkPage
{
    private readonly List<LinkClientInfo> _clients = [];
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private LiveFeed? _refresh;
    private HubConfigState? _hub;
    private AgentSettingsDocument? _settings;
    private bool? _pendingZeroconf;
    private int _zeroconfVersion;
    private int? _linkPort;
    private ApiException? _listError;
    private ApiException? _settingsError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private bool _loading = true;
    private bool _savingSettings;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private IDialogService Dialogs { get; set; } = null!;

    [Inject]
    private LinkOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    private bool IsZeroconf => _pendingZeroconf ?? _settings?.EffectiveZeroconf ?? true;

    private string ZeroconfKey => "zeroconf-" + _zeroconfVersion.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(LoadClientsAsync(), LoadSettingsAsync(), LoadPortAsync(), LoadHubAsync());
        _loading = false;
        if (!IsDisposed)
        {
            _refresh = new LiveFeed(RunRefreshAsync);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        LiveFeed? refresh = _refresh;
        _refresh = null;
        if (refresh is not null)
        {
            await refresh.DisposeAsync();
        }
    }

    /// <summary>True while the hub denies direct links (T908): the agent refuses pairing, so the Pair button is off.</summary>
    private bool IsDirectLinkDenied => HubConfigAccess.IsDirectLinkDenied(_hub);

    private async Task LoadHubAsync()
    {
        try
        {
            _hub = await Api.GetHubConfigAsync();
        }
        catch (ApiException)
        {
            // An agent without the endpoint, or a failed read: pairing is offered as it always was; the agent refuses it if the hub denies it.
            _hub = null;
        }
    }

    private string KindText(string kind) => L.Find("Link.Kind." + kind) ?? kind;

    private bool IsBusy(LinkClientInfo client) => _busy.Contains(client.Id);

    private async Task ReloadAsync()
    {
        _loading = true;
        await LoadClientsAsync();
        _loading = false;
    }

    private async Task ReloadSettingsAsync() => await LoadSettingsAsync();

    private async Task LoadClientsAsync()
    {
        try
        {
            LinkClientList list = await Api.GetLinkClientsAsync();
            _listError = null;
            _clients.Clear();
            _clients.AddRange(list.Clients.OrderBy(client => client.CreatedAt));
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _listError = exception;
        }
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await Api.GetSettingsAsync();
            _settingsError = null;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _settingsError = exception;
        }
    }

    private async Task LoadPortAsync()
    {
        try
        {
            _linkPort = (await Api.GetStatusAsync()).Link.Port;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException)
        {
            // The port is a hint in the instructions; they are complete without it.
        }
    }

    private async Task RunRefreshAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Options.ClientRefreshInterval, Time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshClientsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
    }

    private async Task RefreshClientsAsync()
    {
        await InvokeAsync(async () =>
        {
            if (IsDisposed)
            {
                return;
            }

            await LoadClientsAsync();
            StateHasChanged();
        });
    }

    private async Task PairAsync()
    {
        _actionError = null;
        _notice = null;
        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseOnEscapeKey = true };
        IDialogReference reference = await Dialogs.ShowAsync<PairingDialog>(L["Link.Pairing.Title"], options);
        DialogResult? result = await reference.Result;
        if (IsDisposed)
        {
            return;
        }

        if (result is { Canceled: false })
        {
            _notice = L["Link.Pairing.Done"];
        }

        // A client may have paired even when the dialog was closed by hand a moment before.
        await LoadClientsAsync();
    }

    private async Task RevokeAsync(LinkClientInfo client)
    {
        bool? confirmed = await Dialogs.ShowMessageBox(
            L["Link.Revoke.Title"],
            L.Format("Link.Revoke.Message", client.Name),
            yesText: L["Link.Revoke"],
            cancelText: L["Common.Cancel"]);
        if (confirmed != true || IsDisposed || !_busy.Add(client.Id))
        {
            return;
        }

        _actionError = null;
        _notice = null;
        try
        {
            await Api.RevokeLinkClientAsync(client.Id);
            _clients.RemoveAll(existing => string.Equals(existing.Id, client.Id, StringComparison.Ordinal));
            _notice = L.Format("Link.Revoked", client.Name);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            // Somebody revoked it in the meantime: the list is the truth.
            await LoadClientsAsync();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L.Format("Link.RevokeFailed", client.Name);
        }
        finally
        {
            _busy.Remove(client.Id);
        }
    }

    /// <summary>Saves the switch at once. It shows the new value while the agent answers and the answer decides.</summary>
    private async Task SetZeroconfAsync(bool value)
    {
        if (_settings is null || _savingSettings)
        {
            return;
        }

        _savingSettings = true;
        _actionError = null;
        _notice = null;
        _pendingZeroconf = value;
        try
        {
            _settings = await Api.PutSettingsAsync(_settings with { Zeroconf = value });
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            // Nothing was saved: the switch goes back to the saved state below, and the error says why.
            _actionError = exception;
            _actionFailure = L["Link.Zeroconf.SaveFailed"];
            if (exception.StatusCode == 412)
            {
                // The settings changed elsewhere; the next try starts from the current revision.
                await LoadSettingsAsync();
            }
        }
        finally
        {
            _pendingZeroconf = null;
            _savingSettings = false;
            _zeroconfVersion++;
        }
    }
}
