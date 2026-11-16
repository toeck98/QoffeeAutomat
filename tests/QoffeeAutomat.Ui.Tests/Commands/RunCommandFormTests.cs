using System.Text.Json;
using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Commands;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Commands;

/// <summary>
/// The run form (T403) against a fake agent: the fields that the parameters make, the check before anything is sent, the
/// messages of the agent next to the fields, the live output and the result, and that leaving closes the output stream.
/// </summary>
[Trait("Category", "Commands")]
public sealed class RunCommandFormTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public RunCommandFormTests()
    {
        UiTestSetup.Register(this, _api, _browser);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void A_command_without_parameters_has_no_fields_and_runs_with_no_variables()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("lock_screen", description: "Locks the session."));

        Assert.NotNull(cut.Find("[data-testid=run-no-parameters]"));
        Assert.Equal("Locks the session.", cut.Find("[data-testid=run-description]").TextContent);
        Assert.Equal("editing", Phase(cut));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("running", Phase(cut)), Patience);
        (string commandId, IReadOnlyDictionary<string, JsonElement> variables) = Assert.Single(_api.RunRequests);
        Assert.Equal("lock_screen", commandId);
        Assert.Empty(variables);
    }

    [Fact]
    public void Every_parameter_gets_a_field_and_the_required_ones_are_marked()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command(
            "backup",
            parameters: [Samples.TextParameter("target", required: true), Samples.WholeParameter("count", min: 1, max: 10), Samples.ChoiceParameter("mode", false, "fast", "full")]));

        Assert.Equal(3, cut.FindAll(".qa-run-field").Count);
        Assert.Equal("true", cut.Find("[data-field=target]").GetAttribute("data-required"));
        Assert.Equal("false", cut.Find("[data-field=count]").GetAttribute("data-required"));
        Assert.Contains("target *", cut.Find("[data-field=target]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=field-hint-count]"));
    }

    [Fact]
    public void A_value_that_breaks_the_rules_is_not_sent()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command(
            "backup",
            parameters: [Samples.TextParameter("target", required: true), Samples.WholeParameter("count", min: 1, max: 10)]));
        cut.Find("[data-field=count] input").Input("11");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=field-error-target]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=field-error-count]"));
        Assert.Empty(_api.RunRequests);
        Assert.Equal("editing", Phase(cut));
    }

    [Fact]
    public void Submitting_sends_the_values_as_json_of_the_right_kind()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command(
            "backup",
            parameters: [Samples.TextParameter("target", required: true), Samples.WholeParameter("count", min: 1, max: 10)]));
        cut.Find("[data-field=target] input").Input("D:\\Backups");
        cut.Find("[data-field=count] input").Input("3");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Single(_api.RunRequests), Patience);
        (string commandId, IReadOnlyDictionary<string, JsonElement> variables) = _api.RunRequests[0];
        Assert.Equal("backup", commandId);
        Assert.Equal(JsonValueKind.String, variables["target"].ValueKind);
        Assert.Equal("D:\\Backups", variables["target"].GetString());
        Assert.Equal(JsonValueKind.Number, variables["count"].ValueKind);
        Assert.Equal(3, variables["count"].GetDouble());
        cut.WaitForAssertion(() => Assert.Equal("run-1", cut.Find("[data-testid=run-panel]").GetAttribute("data-run-id")), Patience);
        Assert.Equal("run-1", _api.LastStreamedRun);
    }

    [Fact]
    public void The_output_arrives_while_the_run_goes_on_and_the_end_shows_the_result()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.RunStreamsActive), Patience);
        Assert.NotNull(cut.Find("[data-testid=run-progress]"));
        Assert.Empty(cut.FindAll("[data-testid=run-submit]"));

        _api.PushRun(new RunStreamItem(Output: new RunOutputEvent("stdout", "copying\n", Samples.Now)));
        _api.PushRun(new RunStreamItem(Output: new RunOutputEvent("stderr", "warning", Samples.Now)));

        cut.WaitForAssertion(() => Assert.Contains("copying", cut.Find("[data-testid=run-output]").TextContent, StringComparison.Ordinal), Patience);
        cut.WaitForAssertion(() => Assert.Contains("warning", cut.Find("[data-testid=run-output]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Equal("running", Phase(cut));
        Assert.Empty(cut.FindAll("[data-testid=run-result]"));
        Assert.Single(cut.FindAll("[data-testid=run-output] span.qa-stderr"));

        _api.PushRun(new RunStreamItem(End: new RunEndEvent("run-1", true, 0, null, null, 1500, false)));

        cut.WaitForAssertion(() => Assert.Equal("succeeded", cut.Find("[data-testid=run-result]").GetAttribute("data-state")), Patience);
        cut.WaitForAssertion(() => Assert.Equal("finished", Phase(cut)), Patience);
        Assert.NotNull(cut.Find("[data-testid=run-again]"));
        Assert.Empty(cut.FindAll("[data-testid=run-progress]"));
        Assert.Empty(cut.FindAll("[data-testid=run-connection-lost]"));
        cut.WaitForAssertion(() => Assert.Equal(0, _api.RunStreamsActive), Patience);
    }

    [Fact]
    public void A_failed_run_shows_the_exit_code_and_the_error()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.RunStreamsActive), Patience);

        _api.PushRun(new RunStreamItem(End: new RunEndEvent("run-1", false, 1, "exit_nonzero", "The process failed.", 90, false)));

        cut.WaitForAssertion(() => Assert.Equal("failed", cut.Find("[data-testid=run-result]").GetAttribute("data-state")), Patience);
        string details = cut.Find("[data-testid=run-result-details]").TextContent;
        Assert.Contains("exit_nonzero", details, StringComparison.Ordinal);
        Assert.Contains("The process failed.", details, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_again_goes_back_to_the_fields_and_starts_a_second_run()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.RunStreamsActive), Patience);
        _api.PushRun(new RunStreamItem(End: new RunEndEvent("run-1", true, 0, null, null, 5, false)));
        cut.WaitForAssertion(() => Assert.Equal("finished", Phase(cut)), Patience);

        cut.Find("[data-testid=run-again]").Click();

        Assert.Equal("editing", Phase(cut));
        Assert.Empty(cut.FindAll("[data-testid=run-panel]"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(2, _api.RunRequests.Count), Patience);
    }

    [Fact]
    public void A_stream_that_ends_without_the_end_item_is_reported_as_a_lost_connection()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.RunStreamsActive), Patience);

        _api.EndRunStream();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=run-connection-lost]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=run-result]"));
    }

    [Fact]
    public void The_messages_of_the_agent_are_shown_next_to_the_fields()
    {
        _api.StartRunFailure = new ApiException(
            422,
            Samples.Problem(422, "validation_failed", "The variables are not valid.", new ApiValidationError("/vars/target", "pattern", "Does not match.")));
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup", parameters: [Samples.TextParameter("target")]));
        cut.Find("[data-field=target] input").Input("x");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=field-error-target]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=run-rejected]"));
        Assert.Equal("editing", Phase(cut));
        Assert.Empty(cut.FindAll("[data-testid=run-panel]"));
        Assert.Equal(0, _api.RunStreamsOpened);
    }

    [Fact]
    public void A_refusal_that_is_not_about_the_variables_is_shown_as_an_error()
    {
        _api.StartRunFailure = new ApiException(409, Samples.Problem(409, "command_disabled", "The command is switched off."));
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup"));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=error-message]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=run-rejected]"));
        Assert.Equal("editing", Phase(cut));
    }

    [Fact]
    public async Task Leaving_the_form_closes_the_output_stream()
    {
        IRenderedComponent<RunCommandForm> cut = RenderForm(Samples.Command("backup"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(1, _api.RunStreamsActive), Patience);

        await DisposeComponentsAsync();

        Assert.Equal(0, _api.RunStreamsActive);
    }

    [Fact]
    public void The_close_button_raises_the_close_callback()
    {
        int closed = 0;
        IRenderedComponent<RunCommandForm> cut = Render<RunCommandForm>(parameters => parameters
            .Add(form => form.Command, Samples.Command("backup"))
            .Add(form => form.OnClose, () => closed++));

        cut.Find("[data-testid=run-close]").Click();

        Assert.Equal(1, closed);
    }

    private static string? Phase(IRenderedComponent<RunCommandForm> cut) => cut.Find("[data-testid=run-form]").GetAttribute("data-phase");

    private IRenderedComponent<RunCommandForm> RenderForm(CommandDocument command)
    {
        Render<MudPopoverProvider>();
        return Render<RunCommandForm>(parameters => parameters.Add(form => form.Command, command));
    }
}
