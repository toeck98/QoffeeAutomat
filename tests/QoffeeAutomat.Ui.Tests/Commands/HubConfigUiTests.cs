using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Controls;
using QoffeeAutomat.Ui.Components.Pages.Commands;
using QoffeeAutomat.Ui.Components.Pages.Link;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Commands;

/// <summary>
/// What the agent UI shows when a hub manages the PC (PLAN.md S20, T908): the origin of a document, local documents that are kept but not used,
/// the lock of the local configuration and the denial of direct links.
/// </summary>
[Trait("Category", "ConfigSync")]
public sealed class HubConfigUiTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public HubConfigUiTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private static HubConfigState Managed(bool locked = false, bool directDenied = false, IReadOnlyList<HubShadowedItem>? shadowed = null)
    {
        return new HubConfigState(
            true,
            3,
            Samples.Now,
            new HubConfigPolicy(locked ? "locked" : "allow", directDenied ? "deny" : "allow"),
            locked,
            directDenied,
            [new HubConfigItem("command", "from_hub", new string('a', 64), HubConfigItemStates.Active)],
            shadowed ?? [],
            ["SITE_NAME"]);
    }

    private static CommandDocument HubCommand(string id, string name)
    {
        return Samples.Command(id, name) with { Origin = DocumentOrigins.Hub };
    }

    [Fact]
    public void Without_a_hub_the_list_shows_no_origin_and_nothing_is_read_only()
    {
        IRenderedComponent<CommandsPage> cut = RenderCommands();

        Assert.Empty(cut.FindAll("[data-testid^=origin-]"));
        Assert.Empty(cut.FindAll("[data-testid=commands-shadowed]"));
        Assert.False(IsDisabled(cut.Find("[data-testid=command-new]")));
        Assert.False(cut.Find("[data-testid=delete-lock_screen]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_hub_command_shows_the_origin_hub_and_cannot_be_deleted_or_switched_while_a_local_one_is_labelled_local()
    {
        _api.HubConfig = Managed();
        _api.Commands = [HubCommand("from_hub", "From the hub"), Samples.Command("mine", "Mine")];

        IRenderedComponent<CommandsPage> cut = RenderCommands();

        Assert.Equal("hub", cut.Find("[data-testid=origin-from_hub]").GetAttribute("data-origin"));
        Assert.Equal("local", cut.Find("[data-testid=origin-mine]").GetAttribute("data-origin"));
        Assert.True(cut.Find("[data-testid=delete-from_hub]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=delete-mine]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=enabled-from_hub] input").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=enabled-mine] input").HasAttribute("disabled"));

        // A hub command can still be looked at and run.
        Assert.False(cut.Find("[data-testid=edit-from_hub]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=run-from_hub]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_local_command_that_a_hub_command_shadows_is_listed_as_not_in_use()
    {
        _api.HubConfig = Managed(shadowed: [new HubShadowedItem("command", "shared", HubShadowReasons.HubDocument)]);
        _api.Commands = [HubCommand("shared", "Hub version")];

        IRenderedComponent<CommandsPage> cut = RenderCommands();

        Assert.Equal(HubShadowReasons.HubDocument, cut.Find("[data-testid=shadowed-shared]").GetAttribute("data-reason"));
    }

    [Fact]
    public void While_the_local_configuration_is_locked_every_command_is_read_only_and_nothing_can_be_created()
    {
        _api.HubConfig = Managed(locked: true, shadowed: [new HubShadowedItem("command", "mine", HubShadowReasons.LocalConfigLocked)]);
        _api.Commands = [HubCommand("from_hub", "From the hub")];

        IRenderedComponent<CommandsPage> cut = RenderCommands();

        Assert.True(IsDisabled(cut.Find("[data-testid=command-new]")));
        Assert.True(cut.Find("[data-testid=delete-from_hub]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=duplicate-from_hub]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=enabled-from_hub] input").HasAttribute("disabled"));
        Assert.Equal(HubShadowReasons.LocalConfigLocked, cut.Find("[data-testid=shadowed-mine]").GetAttribute("data-reason"));
    }

    [Fact]
    public void The_banner_says_that_a_hub_manages_the_pc_and_what_it_locked()
    {
        _api.HubConfig = Managed(locked: true, directDenied: true);
        Render<MudPopoverProvider>();

        IRenderedComponent<HubConfigBanner> cut = Render<HubConfigBanner>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=hub-banner]")), Patience);
        Assert.Contains("3", cut.Find("[data-testid=hub-banner]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=hub-banner-locked]"));
        Assert.NotNull(cut.Find("[data-testid=hub-banner-direct-denied]"));
    }

    [Fact]
    public void The_banner_shows_why_the_last_generation_was_rejected()
    {
        _api.HubConfig = Managed() with { LastError = "1 document(s) of generation 4 are not valid." };
        Render<MudPopoverProvider>();

        IRenderedComponent<HubConfigBanner> cut = Render<HubConfigBanner>();

        cut.WaitForAssertion(() => Assert.Contains("generation 4", cut.Find("[data-testid=hub-banner-error]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void The_banner_is_silent_without_a_hub_and_with_an_agent_that_has_no_such_endpoint()
    {
        Render<MudPopoverProvider>();
        IRenderedComponent<HubConfigBanner> without = Render<HubConfigBanner>();
        Assert.Empty(without.FindAll("[data-testid=hub-banner]"));

        _api.HubConfigFailure = new ApiException(404, Samples.Problem(404, "not_found", "No such endpoint."));
        IRenderedComponent<HubConfigBanner> older = Render<HubConfigBanner>();

        Assert.Empty(older.FindAll("[data-testid=hub-banner]"));
    }

    [Fact]
    public void With_direct_links_denied_the_link_page_switches_pairing_off_and_says_why()
    {
        _api.HubConfig = Managed(directDenied: true);
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();

        IRenderedComponent<LinkPage> cut = Render<LinkPage>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=link-direct-denied]")), Patience);
        Assert.True(IsDisabled(cut.Find("[data-testid=link-pair]")));
    }

    /// <summary>A MudBlazor button is switched off by the attribute <c>disabled</c>, or, when it is a link, by the class <c>mud-disabled</c> and <c>aria-disabled</c>.</summary>
    private static bool IsDisabled(AngleSharp.Dom.IElement element)
    {
        return element.HasAttribute("disabled")
            || element.ClassList.Contains("mud-disabled")
            || string.Equals(element.GetAttribute("aria-disabled"), "true", StringComparison.Ordinal);
    }

    private IRenderedComponent<CommandsPage> RenderCommands()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<CommandsPage> cut = Render<CommandsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=command-count], [data-testid=commands-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
