using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages;
using QoffeeAutomat.Ui.Components.Services;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Shell;

/// <summary>The first-run setup page and the login page (T401, PLAN.md S08, S09 item 1).</summary>
[Trait("Category", "Ui")]
public sealed class SetupAndLoginTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new() { Setup = new SetupState(true) };
    private readonly FakeBrowserInterop _browser = new();

    public SetupAndLoginTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    private BunitNavigationManager Navigation => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void Setup_sets_the_password_with_the_setup_token_and_signs_in()
    {
        Navigation.NavigateTo("/setup");
        IRenderedComponent<Setup> cut = Render<Setup>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "  token-123 ", "a long enough password", "a long enough password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Single(_api.SetupRequests), Patience);
        Assert.Equal("token-123", _api.SetupRequests[0].SetupToken);
        Assert.Equal("a long enough password", _api.SetupRequests[0].Password);
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/", Navigation.Uri), Patience);
        Assert.Equal(SignInStatus.SignedIn, Services.GetRequiredService<SessionState>().Status);
    }

    [Fact]
    public void Setup_does_not_call_the_agent_when_the_passwords_differ_or_a_field_is_empty()
    {
        IRenderedComponent<Setup> cut = Render<Setup>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "token", "one password", "another password");
        cut.Find("form").Submit();
        Assert.Equal("The passwords do not match.", cut.Find("[data-testid=validation-message]").TextContent);

        FillSetup(cut, " ", "one password", "one password");
        cut.Find("form").Submit();
        Assert.Equal("Enter the setup token.", cut.Find("[data-testid=validation-message]").TextContent);

        FillSetup(cut, "token", "", "");
        cut.Find("form").Submit();
        Assert.Equal("Enter a password.", cut.Find("[data-testid=validation-message]").TextContent);

        Assert.Empty(_api.SetupRequests);
    }

    [Fact]
    public void Setup_shows_the_error_of_the_agent_for_a_wrong_token_and_stays_on_the_page()
    {
        _api.SetupFailure = new ApiException(401, Samples.Problem(401, "invalid_setup_token", "The setup token is wrong."));
        Navigation.NavigateTo("/setup");
        IRenderedComponent<Setup> cut = Render<Setup>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "wrong", "a long enough password", "a long enough password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("The setup token is wrong.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Equal("http://localhost/setup", Navigation.Uri);
        Assert.Equal(SignInStatus.SetupRequired, Services.GetRequiredService<SessionState>().Status);
    }

    [Fact]
    public void Setup_shows_the_reason_of_the_agent_for_a_weak_password()
    {
        _api.SetupFailure = new ApiException(400, Samples.Problem(400, "weak_password", "The password must have at least 12 characters."));
        IRenderedComponent<Setup> cut = Render<Setup>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("input").Count), Patience);

        FillSetup(cut, "token", "short", "short");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("The password must have at least 12 characters.", cut.Find("[data-testid=error-message]").TextContent), Patience);
    }

    [Fact]
    public void Setup_sends_the_user_to_the_login_page_when_a_password_exists_already()
    {
        _api.Setup = null;

        IRenderedComponent<Setup> cut = Render<Setup>();

        cut.WaitForState(() => Navigation.Uri.EndsWith("/login", StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void Login_signs_in_with_the_password_and_goes_to_the_dashboard()
    {
        _api.Setup = null;
        Navigation.NavigateTo("/login");
        IRenderedComponent<Login> cut = Render<Login>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input")), Patience);

        cut.Find("input[type=password]").Input("correct horse battery staple");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Single(_api.LoginRequests), Patience);
        Assert.Equal("correct horse battery staple", _api.LoginRequests[0].Password);
        cut.WaitForAssertion(() => Assert.Equal("http://localhost/", Navigation.Uri), Patience);
        Assert.Equal(SignInStatus.SignedIn, Services.GetRequiredService<SessionState>().Status);
    }

    [Fact]
    public void Login_goes_back_to_the_page_the_user_wanted()
    {
        _api.Setup = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("returnUrl", "/commands"));
        IRenderedComponent<Login> cut = Render<Login>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input")), Patience);

        cut.Find("input[type=password]").Input("pw");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/commands", Navigation.Uri), Patience);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    public void Login_never_follows_a_return_address_that_leaves_the_site(string returnUrl)
    {
        _api.Setup = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("returnUrl", returnUrl));
        IRenderedComponent<Login> cut = Render<Login>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input")), Patience);

        cut.Find("input[type=password]").Input("pw");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/", Navigation.Uri), Patience);
    }

    [Fact]
    public void Login_shows_the_error_for_a_wrong_password_and_clears_the_field()
    {
        _api.Setup = null;
        _api.LoginFailure = new ApiException(401, Samples.Problem(401, "invalid_credentials", "The password is wrong."));
        IRenderedComponent<Login> cut = Render<Login>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input")), Patience);

        cut.Find("input[type=password]").Input("wrong");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("The password is wrong.", cut.Find("[data-testid=error-message]").TextContent), Patience);
        Assert.Equal(string.Empty, cut.Find("input[type=password]").GetAttribute("value") ?? string.Empty);
        Assert.Equal(SignInStatus.SignedOut, Services.GetRequiredService<SessionState>().Status);
    }

    [Fact]
    public void Login_tells_how_long_a_locked_out_address_has_to_wait()
    {
        _api.Setup = null;
        _api.LoginFailure = new ApiException(429, Samples.Problem(429, "locked_out", "Too many failed attempts. Try again later."), retryAfterSeconds: 840);
        IRenderedComponent<Login> cut = Render<Login>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input")), Patience);

        cut.Find("input[type=password]").Input("x");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("Too many failed attempts. Try again in 840 seconds.", cut.Find("[data-testid=error-message]").TextContent), Patience);
    }

    [Fact]
    public void Login_asks_for_a_password_before_it_calls_the_agent()
    {
        _api.Setup = null;
        IRenderedComponent<Login> cut = Render<Login>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("input")), Patience);

        cut.Find("form").Submit();

        Assert.Equal("Enter the password.", cut.Find("[data-testid=validation-message]").TextContent);
        Assert.Empty(_api.LoginRequests);
    }

    [Fact]
    public void Login_offers_the_setup_page_while_a_setup_token_is_waiting()
    {
        // A password exists and `reset-password` wrote a new setup token: the old password still works.
        _api.Setup = new SetupState(true);

        IRenderedComponent<Login> cut = Render<Login>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=setup-hint] a")), Patience);
        Assert.Equal("setup", cut.Find("[data-testid=setup-hint] a").GetAttribute("href"));
    }

    [Fact]
    public async Task Login_shows_why_the_agent_cannot_be_used_from_this_address()
    {
        // A context of its own: the services of this one are fixed once the constructor has resolved the first of them.
        await using var context = new BunitContext();
        var denying = new DenyingApi(new FakeAgentApi(), new ApiException(403, Samples.Problem(403, "remote_ui_disabled", "English detail.")));
        UiTestSetup.Register(context, denying, new FakeBrowserInterop());

        IRenderedComponent<Login> cut = context.Render<Login>();

        cut.WaitForAssertion(() => Assert.Contains("only available on the PC itself", cut.Find("[data-testid=error-message]").TextContent, StringComparison.Ordinal), Patience);
    }

    private static void FillSetup(IRenderedComponent<Setup> cut, string token, string password, string confirmation)
    {
        IReadOnlyList<AngleSharp.Dom.IElement> inputs = cut.FindAll("input");
        inputs[0].Input(token);
        inputs[1].Input(password);
        inputs[2].Input(confirmation);
    }

    /// <summary>An agent that refuses the first question, like the HTTPS listener while remote UI is off.</summary>
    private sealed class DenyingApi : IAgentApi
    {
        private readonly FakeAgentApi _inner;
        private readonly ApiException _failure;

        public DenyingApi(FakeAgentApi inner, ApiException failure)
        {
            _inner = inner;
            _failure = failure;
        }

        public Task<AuthMeResponse?> GetMeAsync(CancellationToken cancellationToken = default) => Task.FromException<AuthMeResponse?>(_failure);

        public Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default) => _inner.GetSetupStateAsync(cancellationToken);

        public Task<AuthMeResponse> SetUpAsync(SetupRequest request, CancellationToken cancellationToken = default) => _inner.SetUpAsync(request, cancellationToken);

        public Task<AuthMeResponse> LogInAsync(LoginRequest request, CancellationToken cancellationToken = default) => _inner.LogInAsync(request, cancellationToken);

        public Task LogOutAsync(CancellationToken cancellationToken = default) => _inner.LogOutAsync(cancellationToken);

        public Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default) => _inner.GetStatusAsync(cancellationToken);

        public Task<HubConfigState> GetHubConfigAsync(CancellationToken cancellationToken = default) => _inner.GetHubConfigAsync(cancellationToken);

        public Task<HotkeyStatusResponse> GetHotkeyStatusAsync(CancellationToken cancellationToken = default) => _inner.GetHotkeyStatusAsync(cancellationToken);

        public Task<HardwareSensorStatus> GetHardwareSensorStatusAsync(CancellationToken cancellationToken = default) => _inner.GetHardwareSensorStatusAsync(cancellationToken);

        public Task<LinkClientList> GetLinkClientsAsync(CancellationToken cancellationToken = default) => _inner.GetLinkClientsAsync(cancellationToken);

        public Task<MetricList> GetMetricsAsync(CancellationToken cancellationToken = default) => _inner.GetMetricsAsync(cancellationToken);

        public Task<MetricHistoryResponse> GetMetricHistoryAsync(IReadOnlyList<string> metricIds, int seconds, CancellationToken cancellationToken = default)
            => _inner.GetMetricHistoryAsync(metricIds, seconds, cancellationToken);

        public IAsyncEnumerable<MetricStreamUpdate> StreamMetricsAsync(IReadOnlyList<string> metricIds, int intervalSeconds, CancellationToken cancellationToken = default)
            => _inner.StreamMetricsAsync(metricIds, intervalSeconds, cancellationToken);

        public Task<RunList> GetRunsAsync(int limit, CancellationToken cancellationToken = default) => _inner.GetRunsAsync(limit, cancellationToken);

        public Task<EventList> GetEventsAsync(int limit, CancellationToken cancellationToken = default) => _inner.GetEventsAsync(limit, cancellationToken);

        public Task<CommandList> GetCommandsAsync(CancellationToken cancellationToken = default) => _inner.GetCommandsAsync(cancellationToken);

        public Task<CommandDocument> PatchCommandAsync(string id, CommandPatch patch, CancellationToken cancellationToken = default)
            => _inner.PatchCommandAsync(id, patch, cancellationToken);

        public Task<CommandDocument> CreateCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
            => _inner.CreateCommandAsync(command, cancellationToken);

        public Task<CommandDocument> GetCommandAsync(string id, CancellationToken cancellationToken = default)
            => _inner.GetCommandAsync(id, cancellationToken);

        public Task<CommandDocument> ReplaceCommandAsync(CommandDocument command, CancellationToken cancellationToken = default)
            => _inner.ReplaceCommandAsync(command, cancellationToken);

        public Task<ValidationReport> ValidateCommandAsync(CommandDocument draft, CancellationToken cancellationToken = default)
            => _inner.ValidateCommandAsync(draft, cancellationToken);

        public Task<RunStarted> TestCommandAsync(CommandDocument draft, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
            => _inner.TestCommandAsync(draft, variables, cancellationToken);

        public Task<ScriptSummaryList> GetScriptsAsync(CancellationToken cancellationToken = default) => _inner.GetScriptsAsync(cancellationToken);

        public Task DeleteCommandAsync(string id, CancellationToken cancellationToken = default) => _inner.DeleteCommandAsync(id, cancellationToken);

        public Task<RunStarted> StartRunAsync(string commandId, IReadOnlyDictionary<string, JsonElement> variables, CancellationToken cancellationToken = default)
            => _inner.StartRunAsync(commandId, variables, cancellationToken);

        public IAsyncEnumerable<RunStreamItem> StreamRunAsync(string runId, CancellationToken cancellationToken = default)
            => _inner.StreamRunAsync(runId, cancellationToken);

        public Task<MetricOverridesDocument> PutMetricOverrideAsync(string id, MetricOverride change, CancellationToken cancellationToken = default)
            => _inner.PutMetricOverrideAsync(id, change, cancellationToken);

        public Task<SensorList> GetSensorsAsync(CancellationToken cancellationToken = default) => _inner.GetSensorsAsync(cancellationToken);

        public Task<SensorDocument> CreateSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
            => _inner.CreateSensorAsync(sensor, cancellationToken);

        public Task<SensorDocument> ReplaceSensorAsync(SensorDocument sensor, CancellationToken cancellationToken = default)
            => _inner.ReplaceSensorAsync(sensor, cancellationToken);

        public Task DeleteSensorAsync(string id, CancellationToken cancellationToken = default) => _inner.DeleteSensorAsync(id, cancellationToken);

        public Task<SensorTestResult> TestSensorAsync(SensorDocument draft, CancellationToken cancellationToken = default)
            => _inner.TestSensorAsync(draft, cancellationToken);

        public Task<SensorValue> GetSensorValueAsync(string id, CancellationToken cancellationToken = default)
            => _inner.GetSensorValueAsync(id, cancellationToken);

        public Task<SensorValue> SetSensorValueAsync(string id, string value, CancellationToken cancellationToken = default)
            => _inner.SetSensorValueAsync(id, value, cancellationToken);

        public Task<ModeGroupList> GetModeGroupsAsync(CancellationToken cancellationToken = default) => _inner.GetModeGroupsAsync(cancellationToken);

        public Task<ModeGroupDocument> CreateModeGroupAsync(ModeGroupDocument group, CancellationToken cancellationToken = default)
            => _inner.CreateModeGroupAsync(group, cancellationToken);

        public Task<ModeGroupDocument> ReplaceModeGroupAsync(ModeGroupDocument group, CancellationToken cancellationToken = default)
            => _inner.ReplaceModeGroupAsync(group, cancellationToken);

        public Task DeleteModeGroupAsync(string id, CancellationToken cancellationToken = default) => _inner.DeleteModeGroupAsync(id, cancellationToken);

        public Task<ModeState> GetModeStateAsync(string id, CancellationToken cancellationToken = default) => _inner.GetModeStateAsync(id, cancellationToken);

        public Task<ModeSwitchStarted> SwitchModeAsync(string id, string mode, CancellationToken cancellationToken = default)
            => _inner.SwitchModeAsync(id, mode, cancellationToken);

        public Task<TemplateList> GetTemplatesAsync(CancellationToken cancellationToken = default)
            => _inner.GetTemplatesAsync(cancellationToken);

        public Task<TemplateDetail> GetTemplateAsync(string id, CancellationToken cancellationToken = default)
            => _inner.GetTemplateAsync(id, cancellationToken);

        public Task<TemplateInstantiation> InstantiateTemplateAsync(string id, CancellationToken cancellationToken = default)
            => _inner.InstantiateTemplateAsync(id, cancellationToken);

        public Task<SecretList> GetSecretsAsync(CancellationToken cancellationToken = default)
            => _inner.GetSecretsAsync(cancellationToken);

        public Task<SecretSummary> PutSecretAsync(string name, string value, CancellationToken cancellationToken = default)
            => _inner.PutSecretAsync(name, value, cancellationToken);

        public Task DeleteSecretAsync(string name, CancellationToken cancellationToken = default)
            => _inner.DeleteSecretAsync(name, cancellationToken);

        public Task<PairingInfo> StartPairingAsync(CancellationToken cancellationToken = default) => _inner.StartPairingAsync(cancellationToken);

        public Task<PairingInfo?> GetPairingAsync(CancellationToken cancellationToken = default) => _inner.GetPairingAsync(cancellationToken);

        public Task CancelPairingAsync(CancellationToken cancellationToken = default) => _inner.CancelPairingAsync(cancellationToken);

        public Task RevokeLinkClientAsync(string id, CancellationToken cancellationToken = default) => _inner.RevokeLinkClientAsync(id, cancellationToken);

        public Task<AgentSettingsDocument> GetSettingsAsync(CancellationToken cancellationToken = default) => _inner.GetSettingsAsync(cancellationToken);

        public Task<AgentSettingsDocument> PutSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken = default)
            => _inner.PutSettingsAsync(settings, cancellationToken);

        public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
            => _inner.ChangePasswordAsync(request, cancellationToken);

        public Task RestartAgentAsync(CancellationToken cancellationToken = default) => _inner.RestartAgentAsync(cancellationToken);

        public Task<RunList> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken = default) => _inner.QueryRunsAsync(query, cancellationToken);

        public Task<RunDetail> GetRunAsync(string id, CancellationToken cancellationToken = default) => _inner.GetRunAsync(id, cancellationToken);

        public Task<EventList> QueryEventsAsync(int limit, string? type, CancellationToken cancellationToken = default)
            => _inner.QueryEventsAsync(limit, type, cancellationToken);

        public Task<LogList> GetLogsAsync(string level, int limit, CancellationToken cancellationToken = default)
            => _inner.GetLogsAsync(level, limit, cancellationToken);

        public Task<DownloadedFile> ExportConfigAsync(CancellationToken cancellationToken = default) => _inner.ExportConfigAsync(cancellationToken);

        public Task<ConfigImportPreview> PreviewImportAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default)
            => _inner.PreviewImportAsync(archive, includeSettings, cancellationToken);

        public Task<ConfigImportResult> ImportConfigAsync(byte[] archive, bool includeSettings, CancellationToken cancellationToken = default)
            => _inner.ImportConfigAsync(archive, includeSettings, cancellationToken);

        public Task<RevisionList> GetRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default)
            => _inner.GetRevisionsAsync(kind, id, cancellationToken);

        public Task RestoreRevisionAsync(string kind, string id, int revision, int? expectedRevision, CancellationToken cancellationToken = default)
            => _inner.RestoreRevisionAsync(kind, id, revision, expectedRevision, cancellationToken);

        public Task<DownloadedFile> DownloadDiagnosticsAsync(CancellationToken cancellationToken = default) => _inner.DownloadDiagnosticsAsync(cancellationToken);
    }
}
