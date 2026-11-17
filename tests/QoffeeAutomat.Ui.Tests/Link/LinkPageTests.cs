using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Link;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Link;

/// <summary>
/// The Home Assistant link page (T406) against a fake agent: the pairing dialog with the code, its countdown and the fingerprint,
/// the code that disappears when it expires or was used, the dialog that closes when a client has paired, the list of clients
/// with revoke after a confirmation, and the zeroconf switch.
/// </summary>
[Trait("Category", "Link")]
public sealed class LinkPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public LinkPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_paired_clients_are_listed_with_kind_dates_and_connections()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out _);

        Assert.Equal(2, cut.FindAll("[data-testid=client-table] tbody tr").Count);
        Assert.Contains("Home Assistant", cut.Find("[data-testid=client-c1]").TextContent, StringComparison.Ordinal);
        Assert.Equal("homeassistant", cut.Find("[data-testid=kind-c1]").GetAttribute("data-value"));
        Assert.Equal("tool", cut.Find("[data-testid=kind-c2]").GetAttribute("data-value"));
        Assert.NotEmpty(cut.Find("[data-testid=created-c1]").TextContent.Trim());
        Assert.NotEmpty(cut.Find("[data-testid=lastseen-c1]").TextContent.Trim());
        Assert.Contains("never", cut.Find("[data-testid=lastseen-c2]").TextContent, StringComparison.Ordinal);
        Assert.Equal("1", cut.Find("[data-testid=connected-c1]").GetAttribute("data-connections"));
        Assert.Equal("true", cut.Find("[data-testid=client-c1]").GetAttribute("data-connected"));
        Assert.Equal("false", cut.Find("[data-testid=client-c2]").GetAttribute("data-connected"));
    }

    [Fact]
    public void Without_clients_the_page_says_so_and_still_offers_pairing()
    {
        _api.Clients = new LinkClientList([]);

        IRenderedComponent<LinkPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=clients-none]"));
        Assert.Empty(cut.FindAll("[data-testid=client-table]"));
        Assert.False(cut.Find("[data-testid=link-pair]").HasAttribute("disabled"));
    }

    [Fact]
    public void Short_instructions_for_home_assistant_are_shown_with_the_link_port()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out _);

        Assert.Equal(4, cut.FindAll("[data-testid=link-instructions] li").Count);
        Assert.Contains("8481", cut.Find("[data-testid=link-port]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pair_button_shows_the_code_with_the_countdown_and_the_fingerprint()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=link-pair]").Click();

        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);
        Assert.Equal("123456", dialogs.Find("[data-testid=pairing-code]").TextContent.Trim());
        Assert.Equal("AB:CD:EF:01:23:45:67:89", dialogs.Find("[data-testid=pairing-fingerprint]").TextContent.Trim());
        Assert.Equal("active", dialogs.Find("[data-testid=pairing-panel]").GetAttribute("data-state"));
        int seconds = int.Parse(dialogs.Find("[data-testid=pairing-countdown]").GetAttribute("data-seconds")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 290, 300);
        Assert.Contains(":", dialogs.Find("[data-testid=pairing-countdown]").TextContent, StringComparison.Ordinal);
        Assert.Equal(4, dialogs.FindAll("[data-testid=pairing-instructions] li").Count);
        Assert.Equal(1, _api.PairingStarts);
    }

    [Fact]
    public void The_countdown_runs_down_while_the_dialog_is_open()
    {
        _api.PairingLifetime = TimeSpan.FromSeconds(30);
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=link-pair]").Click();
        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);

        dialogs.WaitForAssertion(
            () => Assert.True(int.Parse(dialogs.Find("[data-testid=pairing-countdown]").GetAttribute("data-seconds")!, System.Globalization.CultureInfo.InvariantCulture) < 30),
            Patience);
    }

    [Fact]
    public void The_dialog_closes_by_itself_when_a_client_has_paired()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=link-pair]").Click();
        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);

        _api.CompletePairing("Living room HA");

        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll("[data-testid=pairing-panel]")), Patience);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=client-new-client]")), Patience);
        Assert.Contains("Living room HA", cut.Find("[data-testid=client-new-client]").TextContent, StringComparison.Ordinal);
        Assert.Equal(3, cut.FindAll("[data-testid=client-table] tbody tr").Count);
        Assert.NotNull(cut.Find("[data-testid=notice]"));

        // The code was used up by the client: there is nothing left to cancel.
        Assert.Equal(0, _api.PairingCancels);
    }

    [Fact]
    public void The_code_disappears_when_it_expires_and_a_new_one_can_be_asked_for()
    {
        _api.PairingLifetime = TimeSpan.FromMilliseconds(400);
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=link-pair]").Click();
        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);

        dialogs.WaitForAssertion(() => Assert.NotNull(dialogs.Find("[data-testid=pairing-expired]")), Patience);
        Assert.Empty(dialogs.FindAll("[data-testid=pairing-code]"));
        Assert.Empty(dialogs.FindAll("[data-testid=pairing-fingerprint]"));
        Assert.Equal("expired", dialogs.Find("[data-testid=pairing-panel]").GetAttribute("data-state"));

        _api.PairingLifetime = TimeSpan.FromMinutes(5);
        _api.PairingCode = "654321";
        dialogs.Find("[data-testid=pairing-restart]").Click();

        dialogs.WaitForAssertion(() => Assert.Equal("654321", dialogs.Find("[data-testid=pairing-code]").TextContent.Trim()), Patience);
        Assert.Equal(2, _api.PairingStarts);
        Assert.Empty(dialogs.FindAll("[data-testid=pairing-expired]"));
    }

    [Fact]
    public void The_code_disappears_when_the_agent_withdraws_it_without_a_client_having_paired()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=link-pair]").Click();
        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);

        _api.Pairing = null;

        dialogs.WaitForAssertion(() => Assert.NotNull(dialogs.Find("[data-testid=pairing-ended]")), Patience);
        Assert.Empty(dialogs.FindAll("[data-testid=pairing-code]"));
        Assert.Equal(2, cut.FindAll("[data-testid=client-table] tbody tr").Count);
    }

    [Fact]
    public void Closing_the_dialog_withdraws_the_code()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=link-pair]").Click();
        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);

        dialogs.Find("[data-testid=pairing-close]").Click();

        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll("[data-testid=pairing-panel]")), Patience);
        Assert.Equal(1, _api.PairingCancels);
        Assert.Null(_api.Pairing);
    }

    [Fact]
    public void A_pairing_that_cannot_start_shows_the_error_and_offers_another_try()
    {
        _api.PairingStartFailure = new ApiException(503, Samples.Problem(503, "no_certificate", "The agent has no TLS certificate, so clients cannot pair."));
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=link-pair]").Click();

        dialogs.WaitForAssertion(() => Assert.Equal("failed", dialogs.Find("[data-testid=pairing-panel]").GetAttribute("data-state")), Patience);
        Assert.Contains("no TLS certificate", dialogs.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal);
        Assert.Empty(dialogs.FindAll("[data-testid=pairing-code]"));
        Assert.NotNull(dialogs.Find("[data-testid=pairing-restart]"));
    }

    [Fact]
    public async Task Leaving_the_page_with_the_dialog_open_withdraws_the_code()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        cut.Find("[data-testid=link-pair]").Click();
        dialogs.WaitForElement("[data-testid=pairing-code]", Patience);

        await DisposeComponentsAsync();

        for (int attempt = 0; attempt < 100 && _api.PairingCancels == 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, _api.PairingCancels);
    }

    [Fact]
    public void Revoking_asks_first_and_nothing_is_revoked_when_the_user_cancels()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=revoke-c1]").Click();
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Empty(_api.Revoked);
        Assert.NotNull(cut.Find("[data-testid=client-c1]"));
    }

    [Fact]
    public void Revoking_after_the_confirmation_removes_the_client_from_the_list()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=revoke-c1]").Click();
        IElement confirm = dialogs.WaitForElement(".mud-message-box__yes-button", Patience);
        Assert.Contains("Revoke Home Assistant?", dialogs.Markup, StringComparison.Ordinal);
        confirm.Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=client-c1]")), Patience);
        Assert.Equal(["c1"], _api.Revoked);
        Assert.Single(cut.FindAll("[data-testid=client-table] tbody tr"));
        Assert.Contains("Home Assistant", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_revoke_keeps_the_client_and_shows_the_error()
    {
        _api.RevokeFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=revoke-c1]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=client-c1]"));
        Assert.Empty(cut.FindAll("[data-testid=notice]"));
    }

    [Fact]
    public void A_client_that_was_revoked_elsewhere_disappears_without_an_error()
    {
        _api.RevokeFailure = new ApiException(404, Samples.Problem(404, "unknown_client", "There is no paired client with this id."));
        IRenderedComponent<LinkPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);
        _api.Clients = new LinkClientList([.. _api.Clients.Clients.Where(client => client.Id != "c1")]);

        cut.Find("[data-testid=revoke-c1]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=client-c1]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=action-failed]"));
    }

    [Fact]
    public void The_list_is_read_again_while_the_page_is_open()
    {
        Services.GetRequiredService<LinkOptions>().ClientRefreshInterval = TimeSpan.FromMilliseconds(30);
        IRenderedComponent<LinkPage> cut = RenderPage(out _);
        _api.Clients = new LinkClientList([.. _api.Clients.Clients, new LinkClientInfo("c3", "Hub", "hub", Samples.Now, null, 0)]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=client-c3]")), Patience);
    }

    [Fact]
    public void The_zeroconf_switch_is_on_by_default_and_saves_at_once()
    {
        IRenderedComponent<LinkPage> cut = RenderPage(out _);
        Assert.Equal("true", cut.Find("[data-testid=zeroconf-switch]").GetAttribute("data-checked"));

        cut.Find("[data-testid=zeroconf-switch] input").Change(false);

        cut.WaitForAssertion(() => Assert.Single(_api.SettingsPuts), Patience);
        AgentSettingsDocument put = _api.SettingsPuts[0];
        Assert.False(put.Zeroconf);
        Assert.Equal(3, put.Revision);
        Assert.Equal("Study PC", put.Name);
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=zeroconf-switch]").GetAttribute("data-checked")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=action-failed]"));
    }

    [Fact]
    public void The_zeroconf_switch_shows_the_saved_state_of_the_agent()
    {
        _api.Settings = _api.Settings with { Zeroconf = false };

        IRenderedComponent<LinkPage> cut = RenderPage(out _);

        Assert.Equal("false", cut.Find("[data-testid=zeroconf-switch]").GetAttribute("data-checked"));
    }

    [Fact]
    public void A_refused_zeroconf_change_springs_back_and_shows_the_error()
    {
        _api.SettingsPutFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<LinkPage> cut = RenderPage(out _);

        cut.Find("[data-testid=zeroconf-switch] input").Change(false);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Equal("true", cut.Find("[data-testid=zeroconf-switch]").GetAttribute("data-checked"));
        Assert.Null(_api.Settings.Zeroconf);
    }

    [Fact]
    public void Settings_that_cannot_be_read_show_the_error_and_no_switch()
    {
        _api.SettingsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<LinkPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=link-zeroconf] [data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=zeroconf-switch]"));
        Assert.NotNull(cut.Find("[data-testid=client-table]"));
    }

    private IRenderedComponent<LinkPage> RenderPage(out IRenderedComponent<MudDialogProvider> dialogs)
    {
        Render<MudPopoverProvider>();
        dialogs = Render<MudDialogProvider>();
        IRenderedComponent<LinkPage> cut = Render<LinkPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=client-table], [data-testid=clients-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
