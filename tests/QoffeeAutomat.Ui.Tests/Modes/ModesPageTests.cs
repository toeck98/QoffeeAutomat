using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Modes;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Modes;

/// <summary>
/// The mode groups page (T717) against a fake agent: every group with its real current mode, switching it, following a switch
/// until the agent says it is over, a second switch that is refused, and deleting after a confirmation.
/// </summary>
[Trait("Category", "Modes")]
public sealed class ModesPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public ModesPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
        Services.GetRequiredService<ModeOptions>().PollInterval = TimeSpan.FromMilliseconds(20);
        _api.ModeGroups =
        [
            Samples.ModeGroup("pc", "PC modes", true, "normal", "study"),
            Samples.ModeGroup("lights", "Lights", true, "day", "evening"),
        ];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void Every_group_is_listed_with_its_current_mode()
    {
        _api.SetModeState(new ModeState("pc", "study"));

        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.Equal(2, cut.FindAll("[data-testid=mode-groups] > div").Count);
        Assert.Equal("study", cut.Find("[data-testid=mode-current-pc]").GetAttribute("data-value"));
        Assert.Contains("Study", cut.Find("[data-testid=mode-current-pc]").TextContent, StringComparison.Ordinal);
        Assert.Equal("day", cut.Find("[data-testid=mode-current-lights]").GetAttribute("data-value"));
        Assert.Equal("true", cut.Find("[data-testid=mode-switch-pc-study]").GetAttribute("data-active"));
        Assert.Equal("false", cut.Find("[data-testid=mode-switch-pc-normal]").GetAttribute("data-active"));
        Assert.NotNull(cut.Find("[data-testid=mode-new]"));
    }

    [Fact]
    public void An_agent_without_groups_says_so()
    {
        _api.ModeGroups = [];

        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=modes-none]"));
        Assert.Empty(cut.FindAll("[data-testid=mode-groups]"));
    }

    [Fact]
    public void A_list_that_cannot_be_read_shows_the_error_instead_of_the_groups()
    {
        _api.ModeGroupsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=mode-groups]"));
        Assert.Empty(cut.FindAll("[data-testid=modes-none]"));
    }

    [Fact]
    public void A_state_that_is_unknown_is_shown_as_unknown()
    {
        _api.SetModeState(new ModeState("pc", null));

        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.Equal(string.Empty, cut.Find("[data-testid=mode-current-pc]").GetAttribute("data-value"));
        Assert.Contains("unknown", cut.Find("[data-testid=mode-current-pc]").TextContent, StringComparison.Ordinal);
        Assert.Equal("false", cut.Find("[data-testid=mode-switch-pc-normal]").GetAttribute("data-active"));
        Assert.Equal("false", cut.Find("[data-testid=mode-switch-pc-study]").GetAttribute("data-active"));
    }

    [Fact]
    public void Switching_starts_the_switch_and_shows_the_new_mode_only_when_the_agent_says_it_is_over()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        cut.Find("[data-testid=mode-switch-pc-study]").Click();

        Assert.Equal(("pc", "study"), Assert.Single(_api.ModeSwitches));
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching")), Patience);
        Assert.NotNull(cut.Find("[data-testid=mode-switching-pc]"));
        Assert.Empty(cut.FindAll("[data-testid=mode-current-pc]"));
        Assert.True(IsDisabled(cut.Find("[data-testid=mode-switch-pc-normal]")));
        Assert.True(IsDisabled(cut.Find("[data-testid=mode-switch-pc-study]")));
        Assert.False(IsDisabled(cut.Find("[data-testid=mode-switch-lights-evening]")));

        _api.FinishModeSwitch("pc");

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching")), Patience);
        Assert.Equal("study", cut.Find("[data-testid=mode-current-pc]").GetAttribute("data-value"));
        Assert.Equal("true", cut.Find("[data-testid=mode-switch-pc-study]").GetAttribute("data-active"));
        Assert.False(IsDisabled(cut.Find("[data-testid=mode-switch-pc-normal]")));
    }

    [Fact]
    public void A_switch_that_failed_leaves_the_state_unknown()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out _);
        cut.Find("[data-testid=mode-switch-pc-study]").Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching")), Patience);

        _api.FinishModeSwitch("pc", succeeded: false);

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching")), Patience);
        Assert.Equal(string.Empty, cut.Find("[data-testid=mode-current-pc]").GetAttribute("data-value"));
    }

    [Fact]
    public async Task The_state_is_read_only_while_a_switch_is_running()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out _);
        await Task.Delay(150);
        Assert.Equal(0, _api.ModeStateCalls);

        cut.Find("[data-testid=mode-switch-pc-study]").Click();
        cut.WaitForAssertion(() => Assert.True(_api.ModeStateCalls >= 2), Patience);

        _api.FinishModeSwitch("pc");
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching")), Patience);
        await Task.Delay(100);
        int calls = _api.ModeStateCalls;
        await Task.Delay(150);

        Assert.Equal(calls, _api.ModeStateCalls);
    }

    [Fact]
    public async Task Leaving_the_page_stops_following_the_switch()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out _);
        cut.Find("[data-testid=mode-switch-pc-study]").Click();
        cut.WaitForAssertion(() => Assert.True(_api.ModeStateCalls >= 1), Patience);

        await DisposeComponentsAsync();
        await Task.Delay(100);
        int calls = _api.ModeStateCalls;
        await Task.Delay(150);

        Assert.Equal(calls, _api.ModeStateCalls);
    }

    [Fact]
    public void A_switch_that_was_started_elsewhere_is_followed_from_the_start()
    {
        _api.SetModeState(new ModeState("pc", "normal", true, "study"));

        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.Equal("true", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching"));
        Assert.Contains("Study", cut.Find("[data-testid=mode-switching-pc]").TextContent, StringComparison.Ordinal);
        Assert.True(IsDisabled(cut.Find("[data-testid=mode-switch-pc-normal]")));
        Assert.Empty(_api.ModeSwitches);

        _api.FinishModeSwitch("pc");

        cut.WaitForAssertion(() => Assert.Equal("study", cut.Find("[data-testid=mode-current-pc]").GetAttribute("data-value")), Patience);
    }

    [Fact]
    public void A_second_switch_that_the_agent_refuses_as_busy_shows_the_running_one()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out _);
        _api.SetModeState(new ModeState("pc", "normal", true, "study"));
        _api.ModeSwitchFailure = new ApiException(409, Samples.Problem(409, "busy", "A switch of this group is running."));

        cut.Find("[data-testid=mode-switch-pc-study]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=mode-busy]")), Patience);
        Assert.Equal("true", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching"));
        Assert.Empty(cut.FindAll("[data-testid=action-failed]"));
    }

    [Fact]
    public void A_switch_that_cannot_be_started_shows_the_error_and_keeps_the_state()
    {
        _api.ModeSwitchFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        cut.Find("[data-testid=mode-switch-pc-study]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Equal("normal", cut.Find("[data-testid=mode-current-pc]").GetAttribute("data-value"));
        Assert.Equal("false", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-switching"));
        Assert.Empty(cut.FindAll("[data-testid=mode-busy]"));
    }

    [Fact]
    public void A_disabled_group_cannot_be_switched()
    {
        _api.ModeGroups = [Samples.ModeGroup("pc", "PC modes", enabled: false)];

        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.Equal("false", cut.Find("[data-testid=mode-group-pc]").GetAttribute("data-enabled"));
        Assert.True(IsDisabled(cut.Find("[data-testid=mode-switch-pc-normal]")));
        Assert.True(IsDisabled(cut.Find("[data-testid=mode-switch-pc-study]")));
    }

    [Fact]
    public void A_group_with_many_modes_is_switched_with_a_select()
    {
        _api.ModeGroups = [Samples.ModeGroup("tv", "TV", true, "normal", "study", "gaming", "entertainment", "racing")];
        IRenderedComponent<ModesPage> cut = RenderPage(out _);
        Assert.Empty(cut.FindAll("[data-testid=mode-switch-tv-normal]"));

        cut.Find("select[data-testid=mode-select-tv]").Change("racing");

        Assert.Equal(("tv", "racing"), Assert.Single(_api.ModeSwitches));
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=mode-group-tv]").GetAttribute("data-switching")), Patience);
    }

    [Fact]
    public void The_edit_button_leads_to_the_editor_of_the_group()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out _);

        Assert.EndsWith("/modes/pc", cut.Find("[data-testid=edit-pc]").GetAttribute("href"), StringComparison.Ordinal);
        Assert.EndsWith("/modes/new", cut.Find("[data-testid=mode-new]").GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_asks_first_and_nothing_is_deleted_when_the_user_cancels()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-pc]").Click();
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Empty(_api.DeletedModeGroups);
        Assert.NotNull(cut.Find("[data-testid=mode-group-pc]"));
    }

    [Fact]
    public void Deleting_after_the_confirmation_removes_the_group_and_says_so()
    {
        IRenderedComponent<ModesPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-pc]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=mode-group-pc]")), Patience);
        Assert.Equal(["pc"], _api.DeletedModeGroups.ToArray());
        Assert.Contains("PC modes", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=mode-group-lights]"));
    }

    [Fact]
    public void A_refused_delete_keeps_the_group_and_shows_the_error()
    {
        _api.ModeDeleteFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<ModesPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-pc]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=mode-group-pc]"));
        Assert.Empty(cut.FindAll("[data-testid=notice]"));
    }

    private static bool IsDisabled(IElement element)
    {
        return element.HasAttribute("disabled")
            || element.ClassList.Contains("mud-disabled")
            || string.Equals(element.GetAttribute("aria-disabled"), "true", StringComparison.Ordinal);
    }

    private IRenderedComponent<ModesPage> RenderPage(out IRenderedComponent<MudDialogProvider> dialogs)
    {
        Render<MudPopoverProvider>();
        dialogs = Render<MudDialogProvider>();
        IRenderedComponent<ModesPage> cut = Render<ModesPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=mode-groups], [data-testid=modes-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
