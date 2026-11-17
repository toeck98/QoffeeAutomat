using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Settings;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Settings;

/// <summary>
/// The settings page (T407) against a fake agent: the form shows the saved settings, a save sends the draft against the
/// revision that was read, a changed port shows that a restart is required and offers it, the remote UI switch explains itself,
/// and the password is changed with its own form.
/// </summary>
[Trait("Category", "Settings")]
public sealed class SettingsPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public SettingsPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_form_shows_the_saved_settings()
    {
        _api.Settings = new AgentSettingsDocument(
            DocumentSchema.CurrentVersion,
            "Study PC",
            UiPort: 9000,
            LinkPort: 9001,
            RemoteUi: true,
            TrayIcon: false,
            Logging: new LoggingSettings("warning"),
            Retention: new RetentionSettings(30, 90),
            Revision: 3);

        IRenderedComponent<SettingsPage> cut = RenderPage();

        Assert.Equal("Study PC", cut.Find("[data-testid=setting-name] input").GetAttribute("value"));
        Assert.Equal("9000", cut.Find("[data-testid=setting-uiport] input").GetAttribute("value"));
        Assert.Equal("9001", cut.Find("[data-testid=setting-linkport] input").GetAttribute("value"));
        Assert.Equal("30", cut.Find("[data-testid=setting-runhistorydays] input").GetAttribute("value"));
        Assert.Equal("90", cut.Find("[data-testid=setting-auditlogdays] input").GetAttribute("value"));
        Assert.Equal("true", cut.Find("[data-testid=setting-remoteui]").GetAttribute("data-checked"));
        Assert.Equal("false", cut.Find("[data-testid=setting-tray]").GetAttribute("data-checked"));
        Assert.Equal("warning", cut.Find("[data-testid=setting-loglevel] option[selected]").GetAttribute("value"));
        Assert.Empty(cut.FindAll("[data-testid=restart-required]"));
    }

    [Fact]
    public void Saving_sends_the_draft_against_the_revision_that_was_read()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=setting-name] input").Change("Office PC");
        cut.Find("[data-testid=setting-loglevel]").Change("debug");
        cut.Find("[data-testid=setting-runhistorydays] input").Change("14");
        cut.Find("[data-testid=setting-tray] input").Change(false);
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.SettingsPuts), Patience);
        AgentSettingsDocument put = _api.SettingsPuts[0];
        Assert.Equal("Office PC", put.Name);
        Assert.Equal("debug", put.Logging?.Level);
        Assert.Equal(14, put.Retention?.RunHistoryDays);
        Assert.False(put.TrayIcon);
        Assert.Equal(3, put.Revision);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=notice]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=restart-required]"));
    }

    [Fact]
    public void Changing_a_port_shows_that_a_restart_is_required_and_offers_to_restart_the_agent()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=setting-linkport] input").Change("9443");
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restart-required]")), Patience);
        Assert.Equal(9443, _api.SettingsPuts[0].LinkPort);
        Assert.Equal(0, _api.Restarts);

        cut.Find("[data-testid=restart-agent]").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _api.Restarts), Patience);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restarting]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=restart-required]"));
    }

    [Fact]
    public void Switching_remote_ui_on_also_requires_a_restart()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=setting-remoteui] input").Change(true);
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restart-required]")), Patience);
        Assert.True(_api.SettingsPuts[0].RemoteUi);
    }

    [Fact]
    public void The_remote_ui_switch_explains_what_it_does_and_warns_when_it_is_on()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        Assert.Contains("link port", cut.Find("[data-testid=remoteui-explanation]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=remoteui-warning]"));

        cut.Find("[data-testid=setting-remoteui] input").Change(true);

        Assert.Contains("strong password", cut.Find("[data-testid=remoteui-warning]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hardware_sensors_are_off_by_default_and_the_driver_warning_is_always_shown()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        Assert.Equal("false", cut.Find("[data-testid=setting-hardware]").GetAttribute("data-checked"));
        Assert.Contains("PawnIO", cut.Find("[data-testid=hardware-warning]").TextContent, StringComparison.Ordinal);
        Assert.Equal(HardwareSensorStates.Off, cut.Find("[data-testid=hardware-status]").GetAttribute("data-state"));
    }

    [Fact]
    public void Switching_the_hardware_sensors_on_is_saved_and_the_status_is_shown()
    {
        _api.HardwareSensors = new HardwareSensorStatus(true, true, HardwareSensorStates.DriverMissing, "PawnIO", false, 4, "The driver is missing.");
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=setting-hardware] input").Change(true);
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.SettingsPuts), Patience);
        Assert.True(_api.SettingsPuts[0].EffectiveHardwareSensorsEnabled);
        Assert.Equal(HardwareSensorStates.DriverMissing, cut.Find("[data-testid=hardware-status]").GetAttribute("data-state"));
        Assert.Contains("4", cut.Find("[data-testid=hardware-count]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_the_hardware_sensors_off_removes_the_setting()
    {
        _api.Settings = new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Study PC", HardwareSensors: new HardwareSensorSettings(true), Revision: 3);
        IRenderedComponent<SettingsPage> cut = RenderPage();
        Assert.Equal("true", cut.Find("[data-testid=setting-hardware]").GetAttribute("data-checked"));

        cut.Find("[data-testid=setting-hardware] input").Change(false);
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Assert.Single(_api.SettingsPuts), Patience);
        Assert.False(_api.SettingsPuts[0].EffectiveHardwareSensorsEnabled);
    }

    [Fact]
    public void A_restart_that_the_agent_refuses_shows_the_error_and_keeps_the_offer()
    {
        _api.RestartFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Cannot restart."));
        IRenderedComponent<SettingsPage> cut = RenderPage();
        cut.Find("[data-testid=setting-uiport] input").Change("9100");
        cut.Find("[data-testid=settings-save]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=restart-agent]")), Patience);

        cut.Find("[data-testid=restart-agent]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Contains("Cannot restart.", cut.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=restart-agent]"));
        Assert.Empty(cut.FindAll("[data-testid=restarting]"));
    }

    [Fact]
    public void A_refused_save_shows_the_validation_errors_and_no_restart_offer()
    {
        _api.SettingsPutFailure = new ApiException(
            422,
            Samples.Problem(422, "validation_failed", "Invalid.", new ApiValidationError("/linkPort", "conflict", "uiPort and linkPort must differ.")));
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=setting-linkport] input").Change("8480");
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Contains("/linkPort", cut.Find(".qa-error-list").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=restart-required]"));
    }

    [Fact]
    public void Settings_that_cannot_be_read_show_the_error_and_no_form()
    {
        _api.SettingsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<SettingsPage> cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid=error-display]"));
        Assert.Empty(cut.FindAll("[data-testid=settings-form]"));
        Assert.NotNull(cut.Find("[data-testid=password-form]"));
    }

    [Fact]
    public void The_password_is_changed_with_the_current_and_the_new_password()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=password-current] input").Change("old-secret");
        cut.Find("[data-testid=password-new] input").Change("new-secret");
        cut.Find("[data-testid=password-repeat] input").Change("new-secret");
        cut.Find("[data-testid=password-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=password-changed]")), Patience);
        ChangePasswordRequest request = Assert.Single(_api.PasswordChanges);
        Assert.Equal("old-secret", request.CurrentPassword);
        Assert.Equal("new-secret", request.NewPassword);
        Assert.Equal(string.Empty, cut.Find("[data-testid=password-new] input").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void Passwords_that_differ_are_not_sent()
    {
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=password-current] input").Change("old-secret");
        cut.Find("[data-testid=password-new] input").Change("new-secret");
        cut.Find("[data-testid=password-repeat] input").Change("other");
        cut.Find("[data-testid=password-save]").Click();

        Assert.NotNull(cut.Find("[data-testid=password-failed]"));
        Assert.Empty(_api.PasswordChanges);
    }

    [Fact]
    public void A_wrong_current_password_shows_the_error()
    {
        _api.PasswordFailure = new ApiException(403, Samples.Problem(403, "invalid_credentials", "The password is wrong."));
        IRenderedComponent<SettingsPage> cut = RenderPage();

        cut.Find("[data-testid=password-current] input").Change("nope");
        cut.Find("[data-testid=password-new] input").Change("new-secret");
        cut.Find("[data-testid=password-repeat] input").Change("new-secret");
        cut.Find("[data-testid=password-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=password-failed]")), Patience);
        Assert.Contains("wrong", cut.Find("[data-testid=password-form] [data-testid=error-message]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=password-changed]"));
    }

    private IRenderedComponent<SettingsPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<SettingsPage> cut = Render<SettingsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=settings-form], [data-testid=error-display]")), Patience);
        return cut;
    }
}
