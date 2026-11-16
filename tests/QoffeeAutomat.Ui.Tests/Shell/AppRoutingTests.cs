using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components;
using QoffeeAutomat.Ui.Components.Controls;
using QoffeeAutomat.Ui.Components.Services;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Shell;

/// <summary>The whole app (router, layouts, session guard) against a fake agent: where the user lands (T401).</summary>
[Trait("Category", "Ui")]
public sealed class AppRoutingTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new();
    private readonly FakeBrowserInterop _browser = new();

    public AppRoutingTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void On_first_run_the_setup_page_is_shown()
    {
        _api.Setup = new SetupState(true);

        IRenderedComponent<App> cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Contains("First-run setup", cut.Markup, StringComparison.Ordinal), Patience);
        Assert.Equal("http://localhost/setup", Navigation.Uri);
        Assert.Equal(3, cut.FindAll("input").Count);
    }

    [Fact]
    public void Once_a_password_exists_the_login_page_is_shown()
    {
        _api.Setup = null;

        IRenderedComponent<App> cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Contains("Sign in to manage this PC.", cut.Markup, StringComparison.Ordinal), Patience);
        Assert.Equal("http://localhost/login", Navigation.Uri);
        Assert.Single(cut.FindAll("input[type=password]"));
    }

    [Fact]
    public void An_anonymous_visitor_of_another_page_comes_back_to_it_after_signing_in()
    {
        _api.Setup = null;
        Navigation.NavigateTo("/commands/abc");

        IRenderedComponent<App> cut = Render<App>();

        cut.WaitForState(() => Navigation.Uri == "http://localhost/login?returnUrl=%2Fcommands%2Fabc", Patience);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input[type=password]")), Patience);
        cut.Find("input[type=password]").Input("pw");
        cut.Find("form").Submit();

        cut.WaitForState(() => Navigation.Uri == "http://localhost/commands/abc", Patience);
    }

    [Fact]
    public void A_signed_in_user_sees_the_dashboard_inside_the_layout()
    {
        _api.SignedIn();

        IRenderedComponent<App> cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Equal("1.2.3", cut.Find("[data-testid=agent-version]").TextContent), Patience);
        Assert.Equal("http://localhost/", Navigation.Uri);
        Assert.NotNull(cut.Find("button[aria-label='Menu']"));
        Assert.NotNull(cut.Find("button[aria-label='Sign out']"));
    }

    [Fact]
    public void An_address_without_a_page_shows_the_not_found_page_to_a_signed_in_user()
    {
        _api.SignedIn();
        Navigation.NavigateTo("/no/such/page");

        IRenderedComponent<App> cut = Render<App>();

        cut.WaitForAssertion(() => Assert.Contains("Page not found", cut.Markup, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public async Task Navigating_away_from_the_dashboard_closes_the_live_stream()
    {
        _api.SignedIn();
        IRenderedComponent<App> cut = Render<App>();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        await cut.InvokeAsync(() => Navigation.NavigateTo("/no/such/page"));

        cut.WaitForAssertion(() => Assert.Contains("Page not found", cut.Markup, StringComparison.Ordinal), Patience);
        var deadline = DateTime.UtcNow + Patience;
        while ((_api.StreamsActive != 0 || _api.StreamsClosed != 1) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, _api.StreamsActive);
        Assert.Equal(1, _api.StreamsClosed);
    }

    [Fact]
    public void Signing_out_ends_the_session_closes_the_stream_and_shows_the_login_page()
    {
        _api.SignedIn();
        IRenderedComponent<App> cut = Render<App>();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.StreamsActive), Patience);

        cut.Find("button[aria-label='Sign out']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Sign in to manage this PC.", cut.Markup, StringComparison.Ordinal), Patience);
        Assert.Equal(1, _api.LogOutCalls);
        Assert.Equal("http://localhost/login", Navigation.Uri);
        cut.WaitForAssertion(() => Assert.Equal(0, _api.StreamsActive), Patience);
    }

    [Fact]
    public async Task The_language_the_user_picked_is_remembered_and_applied_on_the_next_visit()
    {
        if (Globalization.IsInvariant)
        {
            return;
        }

        _api.SignedIn();
        IRenderedComponent<App> cut = Render<App>();
        cut.WaitForAssertion(() => Assert.Contains("Recent runs", cut.Markup, StringComparison.Ordinal), Patience);

        cut.Find("[data-language=de]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Letzte Ausführungen", cut.Markup, StringComparison.Ordinal), Patience);
        Assert.Equal("de", _browser.Storage[LanguageSwitch.StorageKey]);

        // The next visit: a new app in a new context starts in the stored language.
        await using var next = new BunitContext();
        var nextBrowser = new FakeBrowserInterop();
        nextBrowser.Storage[LanguageSwitch.StorageKey] = "de";
        UiTestSetup.Register(next, new FakeAgentApi().SignedIn(), nextBrowser);

        IRenderedComponent<App> visit = next.Render<App>();

        visit.WaitForAssertion(() => Assert.Contains("Letzte Ausführungen", visit.Markup, StringComparison.Ordinal), Patience);
    }
}
