using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Secrets;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Secrets;

/// <summary>The secrets page (T706) against a fake agent: names and users are listed, a value is write-only, and deleting asks first.</summary>
[Trait("Category", "Secrets")]
public sealed class SecretsPageTests : BunitContext, IAsyncLifetime
{
    private const string TypedValue = "correct-horse-battery";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public SecretsPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
        _api.Secrets =
        [
            new SecretSummary("api_key", ["backup", "sync"]),
            new SecretSummary("db_password"),
        ];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_list_shows_the_names_the_variables_and_the_commands_that_use_them()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        Assert.Equal(2, cut.FindAll("[data-testid=secret-table] tbody tr").Count);
        Assert.Contains("QA_SECRET_API_KEY", cut.Find("[data-testid=secret-api_key]").TextContent, StringComparison.Ordinal);
        Assert.Contains("backup, sync", cut.Find("[data-testid=used-by-api_key]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("backup", cut.Find("[data-testid=used-by-db_password]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=secret-write-only]"));
    }

    [Fact]
    public void An_agent_without_secrets_says_so()
    {
        _api.Secrets = [];

        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=secrets-none]"));
        Assert.Empty(cut.FindAll("[data-testid=secret-table]"));
    }

    [Fact]
    public void A_list_that_cannot_be_read_shows_the_error_instead_of_a_table()
    {
        _api.SecretsFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        Assert.NotNull(cut.Find("[data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=secret-table]"));
    }

    [Fact]
    public void The_value_field_hides_what_is_typed()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        Assert.Equal("password", cut.Find("[data-testid=field-secret-value] input").GetAttribute("type"));
    }

    [Fact]
    public void Saving_sends_the_value_once_and_the_page_never_shows_it()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=field-secret-name] input").Input("backup_token");
        cut.Find("[data-testid=field-secret-value] input").Input(TypedValue);
        cut.Find("[data-testid=secret-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=secret-backup_token]")), Patience);
        (string Name, string Value) written = Assert.Single(_api.WrittenSecrets);
        Assert.Equal("backup_token", written.Name);
        Assert.Equal(TypedValue, written.Value);
        Assert.Contains("backup_token", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(TypedValue, cut.Markup, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(cut.Find("[data-testid=field-secret-value] input").GetAttribute("value")));
    }

    [Fact]
    public void Replacing_fills_in_the_name_and_overwrites_without_a_second_entry()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=replace-api_key]").Click();
        cut.Find("[data-testid=field-secret-value] input").Input(TypedValue);
        cut.Find("[data-testid=secret-save]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(_api.WrittenSecrets), Patience);
        (string Name, string Value) written = Assert.Single(_api.WrittenSecrets);
        Assert.Equal("api_key", written.Name);
        Assert.Equal(TypedValue, written.Value);
        Assert.Equal(2, cut.FindAll("[data-testid=secret-table] tbody tr").Count);
        Assert.DoesNotContain(TypedValue, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_the_agent_refuses_shows_the_validation_error()
    {
        _api.SecretWriteFailure = new ApiException(
            422,
            Samples.Problem(422, "validation_failed", "The request has a validation error.", new ApiValidationError("/value", "invalid", "The value must have at least 4 characters.")));
        IRenderedComponent<SecretsPage> cut = RenderPage(out _);

        cut.Find("[data-testid=field-secret-name] input").Input("tiny");
        cut.Find("[data-testid=field-secret-value] input").Input("abc");
        cut.Find("[data-testid=secret-save]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=save-failed]")), Patience);
        Assert.Contains("at least 4 characters", cut.Find("[data-testid=error-display]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=secret-tiny]"));
    }

    [Fact]
    public void Deleting_asks_first_and_nothing_is_deleted_when_the_user_cancels()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-db_password]").Click();
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".mud-message-box__cancel-button")), Patience);
        Assert.Empty(_api.DeletedSecrets);
        Assert.NotNull(cut.Find("[data-testid=secret-db_password]"));
    }

    [Fact]
    public void Deleting_after_the_confirmation_removes_the_secret_and_says_so()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-db_password]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=secret-db_password]")), Patience);
        Assert.Equal(["db_password"], _api.DeletedSecrets);
        Assert.Contains("db_password", cut.Find("[data-testid=notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_confirmation_of_a_secret_that_is_in_use_names_the_commands()
    {
        IRenderedComponent<SecretsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-api_key]").Click();

        cut.WaitForAssertion(() => Assert.Contains("backup, sync", dialogs.Markup, StringComparison.Ordinal), Patience);
        Assert.Empty(_api.DeletedSecrets);
        dialogs.WaitForElement(".mud-message-box__cancel-button", Patience).Click();
    }

    [Fact]
    public void A_refused_delete_keeps_the_secret_and_shows_the_error()
    {
        _api.SecretDeleteFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));
        IRenderedComponent<SecretsPage> cut = RenderPage(out IRenderedComponent<MudDialogProvider> dialogs);

        cut.Find("[data-testid=delete-db_password]").Click();
        dialogs.WaitForElement(".mud-message-box__yes-button", Patience).Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=secret-db_password]"));
        Assert.Empty(cut.FindAll("[data-testid=notice]"));
    }

    private IRenderedComponent<SecretsPage> RenderPage(out IRenderedComponent<MudDialogProvider> dialogs)
    {
        Render<MudPopoverProvider>();
        dialogs = Render<MudDialogProvider>();
        IRenderedComponent<SecretsPage> cut = Render<SecretsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=secret-table], [data-testid=secrets-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}
