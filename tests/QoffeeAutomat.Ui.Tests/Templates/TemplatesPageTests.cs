using Bunit;
using MudBlazor;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Templates;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Templates;

/// <summary>The template gallery page (T701) against a fake agent: the list, the search, the preview and adding a template.</summary>
[Trait("Category", "Templates")]
public sealed class TemplatesPageTests : BunitContext, IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly FakeAgentApi _api = new FakeAgentApi().SignedIn();
    private readonly FakeBrowserInterop _browser = new();

    public TemplatesPageTests()
    {
        UiTestSetup.Register(this, _api, _browser);
        _api.Templates =
        [
            CommandTemplate("set_volume", "Set volume", "Sets the master volume of the default playback device."),
            CommandTemplate(
                "set_display_profile",
                "Set display profile",
                "Applies a saved monitor layout.",
                scripts: [new TemplateScript("set_display_profile.ps1", "param([string] $Profile)")]),
            CommandTemplate("mode_example", "Racing mode (example)", "Runs several commands.", requires: ["set_display_profile"]),
            SensorTemplate("disk_free_percent", "Disk space free", "The free space of a drive in percent."),
        ];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // MudBlazor's PointerEventsNoneService is only IAsyncDisposable, so the context must be disposed asynchronously.
    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact]
    public void The_gallery_lists_commands_and_sensors_in_two_groups()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        Assert.Equal(3, cut.FindAll("[data-testid=templates-commands] [data-kind=command]").Count);
        Assert.Single(cut.FindAll("[data-testid=templates-sensors] [data-kind=sensor]"));
        Assert.Contains("Set volume", cut.Find("[data-testid=template-set_volume]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Disk space free", cut.Find("[data-testid=template-disk_free_percent]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_without_templates_says_so()
    {
        _api.Templates = [];

        IRenderedComponent<TemplatesPage> cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid=templates-none]"));
    }

    [Fact]
    public void A_list_that_cannot_be_read_shows_the_error()
    {
        _api.TemplatesFailure = new ApiException(500, Samples.Problem(500, "internal_error", "Something broke."));

        IRenderedComponent<TemplatesPage> cut = RenderPage();

        Assert.NotNull(cut.Find("[data-testid=error-message]"));
        Assert.Empty(cut.FindAll("[data-testid=templates-none]"));
    }

    [Fact]
    public void The_search_narrows_the_list_by_name_id_and_description()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=template-search] input").Input("monitor");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-kind]")), Patience);
        Assert.NotNull(cut.Find("[data-testid=template-set_display_profile]"));

        cut.Find("[data-testid=template-search] input").Input("nothing like this");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=templates-nomatch]")), Patience);
        Assert.Empty(cut.FindAll("[data-kind]"));
    }

    [Fact]
    public void The_preview_shows_the_document_and_the_scripts_that_are_copied()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=preview-set_display_profile]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=template-preview]")), Patience);
        Assert.Contains("Set display profile", cut.Find("[data-testid=preview-title]").TextContent, StringComparison.Ordinal);
        Assert.Equal("user", cut.Find("[data-testid=preview-runas]").TextContent);
        Assert.Contains("param([string] $Profile)", cut.Find("[data-testid='preview-script-set_display_profile.ps1']").TextContent, StringComparison.Ordinal);
        Assert.Contains("Profile", cut.Find("[data-testid=preview-parameters]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_api.InstantiatedTemplates);
    }

    [Fact]
    public void The_preview_of_a_sequence_names_the_commands_that_come_with_it()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=preview-mode_example]").Click();

        cut.WaitForAssertion(() => Assert.Contains("set_display_profile", cut.Find("[data-testid=preview-requires]").TextContent, StringComparison.Ordinal), Patience);
    }

    [Fact]
    public void A_sensor_preview_shows_kind_and_value_type()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=preview-disk_free_percent]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-sensor]")), Patience);
        Assert.Contains("number", cut.Find("[data-testid=preview-sensor]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=preview-command]"));
    }

    [Fact]
    public void The_preview_can_be_closed()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();
        cut.Find("[data-testid=preview-set_volume]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=template-preview]")), Patience);

        cut.Find("[data-testid=preview-close]").Click();

        Assert.Empty(cut.FindAll("[data-testid=template-preview]"));
    }

    [Fact]
    public void A_preview_that_cannot_be_loaded_shows_the_error_and_no_template()
    {
        _api.TemplateDetailFailure = new ApiException(404, Samples.Problem(404, "unknown_template", "There is no template with this id."));
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=preview-set_volume]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-failed]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=preview-title]"));
    }

    [Fact]
    public void Adding_a_template_asks_the_agent_and_links_to_the_new_command()
    {
        _api.TemplateInstantiateHandler = _ => new TemplateInstantiation(
            TemplateKinds.Command,
            "set_display_profile_2",
            [
                new TemplateCreatedItem(TemplateKinds.Script, "set_display_profile_2.ps1"),
                new TemplateCreatedItem(TemplateKinds.Command, "set_display_profile_2"),
            ]);
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=add-set_display_profile]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=template-added]")), Patience);
        Assert.Equal(["set_display_profile"], _api.InstantiatedTemplates);
        Assert.Contains("set_display_profile_2", cut.Find("[data-testid=template-added-text]").TextContent, StringComparison.Ordinal);
        Assert.Contains("set_display_profile_2.ps1", cut.Find("[data-testid=template-added-scripts]").TextContent, StringComparison.Ordinal);
        Assert.Equal("/commands/set_display_profile_2", cut.Find("[data-testid=template-open]").GetAttribute("href"));
    }

    [Fact]
    public void A_sensor_is_opened_in_the_sensor_editor()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=add-disk_free_percent]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=template-open]")), Patience);
        Assert.Equal("/sensors/disk_free_percent", cut.Find("[data-testid=template-open]").GetAttribute("href"));
    }

    [Fact]
    public void A_template_can_be_added_from_its_preview()
    {
        IRenderedComponent<TemplatesPage> cut = RenderPage();
        cut.Find("[data-testid=preview-set_volume]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-add]")), Patience);

        cut.Find("[data-testid=preview-add]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=template-added]")), Patience);
        Assert.Equal(["set_volume"], _api.InstantiatedTemplates);
    }

    [Fact]
    public void A_template_that_cannot_be_added_shows_the_error_and_no_success()
    {
        _api.TemplateInstantiateFailure = new ApiException(422, Samples.Problem(422, "validation_failed", "The template is not valid."));
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=add-set_volume]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=action-failed]")), Patience);
        Assert.Empty(cut.FindAll("[data-testid=template-added]"));
    }

    [Fact]
    public void Adding_twice_gives_two_results_one_after_the_other()
    {
        int count = 0;
        _api.TemplateInstantiateHandler = template =>
        {
            count++;
            string id = count == 1 ? template.Summary.Id : template.Summary.Id + "_" + (count);
            return new TemplateInstantiation(template.Summary.Kind, id, [new TemplateCreatedItem(template.Summary.Kind, id)]);
        };
        IRenderedComponent<TemplatesPage> cut = RenderPage();

        cut.Find("[data-testid=add-set_volume]").Click();
        cut.WaitForAssertion(() => Assert.Contains("set_volume", cut.Find("[data-testid=template-added-text]").TextContent, StringComparison.Ordinal), Patience);
        cut.Find("[data-testid=add-set_volume]").Click();

        cut.WaitForAssertion(() => Assert.Contains("set_volume_2", cut.Find("[data-testid=template-added-text]").TextContent, StringComparison.Ordinal), Patience);
        Assert.Equal(["set_volume", "set_volume"], _api.InstantiatedTemplates);
    }

    private static TemplateDetail CommandTemplate(
        string id,
        string name,
        string description,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<TemplateScript>? scripts = null)
    {
        CommandDocument command = Samples.Command(id, name, description: description) with
        {
            Parameters = [new ParameterDefinition("Profile", ParameterTypes.Choice, Required: true, Options: ["A", "B"], Description: "The profile.")],
        };
        return new TemplateDetail(
            new TemplateSummary(id, TemplateKinds.Command, name, description, "mdi:test", command.Action.Type, requires),
            command,
            null,
            scripts ?? []);
    }

    private static TemplateDetail SensorTemplate(string id, string name, string description)
    {
        SensorDocument sensor = Samples.Sensor(id, name, unit: "%");
        return new TemplateDetail(
            new TemplateSummary(id, TemplateKinds.Sensor, name, description, "mdi:test", sensor.Source.Type),
            null,
            sensor,
            []);
    }

    private IRenderedComponent<TemplatesPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        Render<MudDialogProvider>();
        IRenderedComponent<TemplatesPage> cut = Render<TemplatesPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-kind], [data-testid=templates-none], [data-testid=error-message]")), Patience);
        return cut;
    }
}

/// <summary>The pure helpers of the gallery page (T701).</summary>
[Trait("Category", "Templates")]
public sealed class TemplateFilterTests
{
    private static readonly TemplateSummary Volume = new("set_volume", TemplateKinds.Command, "Set volume", "Sets the master volume.");

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("volume", true)]
    [InlineData("SET_VOL", true)]
    [InlineData("master", true)]
    [InlineData("  Master volume ", true)]
    [InlineData("master chief", false)]
    [InlineData("lock", false)]
    public void The_search_text_matches_id_name_or_description_ignoring_case(string? search, bool expected)
    {
        Assert.Equal(expected, TemplateFilter.Matches(Volume, search));
    }

    [Fact]
    public void A_template_without_a_description_matches_on_id_and_name_only()
    {
        var template = new TemplateSummary("lock_pc", TemplateKinds.Command, "Lock the PC");

        Assert.True(TemplateFilter.Matches(template, "lock"));
        Assert.False(TemplateFilter.Matches(template, "volume"));
    }

    [Theory]
    [InlineData(TemplateKinds.Command, "set_volume", "/commands/set_volume")]
    [InlineData(TemplateKinds.Sensor, "disk_free_percent", "/sensors/disk_free_percent")]
    [InlineData(TemplateKinds.Script, "set_volume.ps1", null)]
    public void The_editor_of_a_new_item_is_found_by_its_kind(string kind, string id, string? expected)
    {
        Assert.Equal(expected, TemplateFilter.EditorHref(kind, id));
    }
}
