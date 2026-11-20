using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Ui.Pages;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Tests.Shell;

/// <summary>The first-run setup and the sign-in of the hub UI (PLAN.md S09 item 1, S20, T915), and where the user lands afterwards.</summary>
[Trait("Category", "HubUi")]
public sealed class SetupAndLoginTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeHubApi _hub = new() { Setup = new SetupState(true) };
    private readonly FakeBrowserInterop _browser = new();

    public SetupAndLoginTests()
    {
        HubUiTestSetup.Register(this, _hub, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    private static void FillSetup(IRenderedComponent<HubSetup> cut, string token, string username, string password, string confirm)
    {
        IReadOnlyList<AngleSharp.Dom.IElement> inputs = cut.FindAll("input");
        inputs[0].Input(token);
        inputs[1].Input(username);
        inputs[2].Input(password);
        inputs[3].Input(confirm);
    }

    [Fact]
    public void Setup_creates_the_first_administrator_with_the_setup_token_and_signs_in()
    {
        Navigation.NavigateTo("/setup");
        IRenderedComponent<HubSetup> cut = Render<HubSetup>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "  token-123 ", " admin ", "a long enough password", "a long enough password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Single(_hub.SetupRequests), Patience);
        Assert.Equal("token-123", _hub.SetupRequests[0].SetupToken);
        Assert.Equal("admin", _hub.SetupRequests[0].Username);
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/agents", Navigation.Uri), Patience);
        Assert.Equal(SignInStatus.SignedIn, Services.GetRequiredService<HubSessionState>().Status);
        Assert.Equal(HubRoleNames.Admin, Services.GetRequiredService<HubSessionState>().Role);
    }

    [Fact]
    public void Setup_does_not_call_the_hub_when_the_passwords_differ_or_a_field_is_empty()
    {
        IRenderedComponent<HubSetup> cut = Render<HubSetup>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "token", "admin", "one password", "another password");
        cut.Find("form").Submit();
        Assert.Equal("The two passwords are not the same.", cut.Find("[data-testid=validation-message]").TextContent);

        FillSetup(cut, " ", "admin", "one password", "one password");
        cut.Find("form").Submit();
        Assert.Equal("Fill in every field.", cut.Find("[data-testid=validation-message]").TextContent);

        Assert.Empty(_hub.SetupRequests);
    }

    [Fact]
    public void Setup_shows_the_error_of_the_hub_for_a_wrong_token_and_stays_on_the_page()
    {
        _hub.SetupFailure = new ApiException(401, Samples.Problem(401, "invalid_setup_token", "The setup token is wrong."));
        Navigation.NavigateTo("/setup");
        IRenderedComponent<HubSetup> cut = Render<HubSetup>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "wrong", "admin", "a long enough password", "a long enough password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("The setup token is wrong.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Equal("http://localhost/setup", Navigation.Uri);
    }

    [Fact]
    public void Setup_sends_the_user_to_the_login_page_when_the_hub_has_an_account_already()
    {
        _hub.Setup = null;

        IRenderedComponent<HubSetup> cut = Render<HubSetup>();

        cut.WaitForState(() => Navigation.Uri.EndsWith("/login", StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void Login_signs_in_with_name_and_password_and_goes_to_the_fleet()
    {
        _hub.Setup = null;
        Navigation.NavigateTo("/login");
        IRenderedComponent<HubLogin> cut = Render<HubLogin>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input").Count), Patience);

        cut.Find("input[type=text]").Input(" alice ");
        cut.Find("input[type=password]").Input("correct horse battery staple");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Single(_hub.LoginRequests), Patience);
        Assert.Equal("alice", _hub.LoginRequests[0].Username);
        Assert.Equal("correct horse battery staple", _hub.LoginRequests[0].Password);
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/agents", Navigation.Uri), Patience);
        Assert.Equal(SignInStatus.SignedIn, Services.GetRequiredService<HubSessionState>().Status);
    }

    [Fact]
    public void Login_goes_back_to_the_page_the_user_wanted_given_as_a_relative_path()
    {
        _hub.Setup = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("returnUrl", "agents/pc-1/commands"));
        IRenderedComponent<HubLogin> cut = Render<HubLogin>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input").Count), Patience);

        cut.Find("input[type=text]").Input("alice");
        cut.Find("input[type=password]").Input("pw");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/agents/pc-1/commands", Navigation.Uri), Patience);
    }

    [Fact]
    public void Login_never_follows_an_address_that_leaves_the_site()
    {
        _hub.Setup = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("returnUrl", "https://evil.example/x"));
        IRenderedComponent<HubLogin> cut = Render<HubLogin>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input").Count), Patience);

        cut.Find("input[type=text]").Input("alice");
        cut.Find("input[type=password]").Input("pw");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/agents", Navigation.Uri), Patience);
    }

    [Fact]
    public void Login_shows_the_error_of_the_hub_for_a_wrong_password_and_does_not_end_up_signed_in()
    {
        _hub.Setup = null;
        _hub.LoginFailure = new ApiException(401, Samples.Problem(401, "invalid_credentials", "The user name or the password is wrong."));
        Navigation.NavigateTo("/login");
        IRenderedComponent<HubLogin> cut = Render<HubLogin>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input").Count), Patience);

        cut.Find("input[type=text]").Input("alice");
        cut.Find("input[type=password]").Input("wrong");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("The user name or the password is wrong.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Equal("http://localhost/login", Navigation.Uri);
        Assert.NotEqual(SignInStatus.SignedIn, Services.GetRequiredService<HubSessionState>().Status);
    }

    [Fact]
    public void Login_asks_for_both_fields_before_it_calls_the_hub()
    {
        _hub.Setup = null;
        IRenderedComponent<HubLogin> cut = Render<HubLogin>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input").Count), Patience);

        cut.Find("input[type=text]").Input("alice");
        cut.Find("form").Submit();

        Assert.Equal("Enter your user name and your password.", cut.Find("[data-testid=validation-message]").TextContent);
        Assert.Empty(_hub.LoginRequests);
    }
}
