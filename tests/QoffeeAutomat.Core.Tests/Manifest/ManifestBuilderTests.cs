using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using static QoffeeAutomat.Core.Tests.Manifest.ManifestTestSupport;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Tests.Manifest;

/// <summary>What goes into the manifest, how commands map to their descriptors and when <c>rev</c> changes (PLAN.md T209).</summary>
[Trait("Category", "Manifest")]
public sealed class ManifestBuilderTests
{
    private static LinkManifest Build(
        IEnumerable<CommandDocument>? commands = null,
        IEnumerable<MetricCatalogEntry>? metrics = null,
        AgentInfo? agent = null,
        IEnumerable<string>? features = null)
    {
        return ManifestBuilder.Build(
            agent ?? Agent(),
            metrics ?? [Metric("cpu.usage")],
            commands ?? [],
            EventCatalog.Types,
            features);
    }

    private static string RevOf(params CommandDocument[] commands) => Build(commands).Rev;

    private static CommandDocument Volume(int max = 100) => Command(
        "set_volume",
        parameters: [new ParameterDefinition("Level", ParameterTypes.WholeNumber, Required: true, Min: 0, Max: max, Step: 1, Unit: "%")],
        control: new HaControl(HaControlTypes.Number, "Level", "audio.volume"));

    // ------------------------------------------------------------------
    // Content
    // ------------------------------------------------------------------

    [Fact]
    public void Only_metrics_that_are_exposed_to_home_assistant_are_announced()
    {
        LinkManifest manifest = Build(metrics:
        [
            Metric("cpu.usage"),
            Metric("window.title", exposeToHa: false),
            Metric("disk.c.free"),
        ]);

        Assert.Equal(["cpu.usage", "disk.c.free"], manifest.Metrics.Select(metric => metric.Id));
    }

    [Fact]
    public void A_metric_is_announced_with_its_effective_interval_and_the_rest_of_its_descriptor()
    {
        MetricCatalogEntry entry = Metric("cpu.usage", interval: 2);

        MetricDescriptor descriptor = Assert.Single(Build(metrics: [entry]).Metrics);

        Assert.Equal(entry.Descriptor, descriptor);
        Assert.Equal(2, descriptor.IntervalSeconds);
        Assert.Equal("system", descriptor.Context);
    }

    [Fact]
    public void Only_enabled_and_exposed_commands_are_announced()
    {
        LinkManifest manifest = Build(commands:
        [
            Command("shown"),
            Command("disabled", enabled: false),
            Command("hidden", exposeToHa: false),
            Command("neither", enabled: false, exposeToHa: false),
        ]);

        Assert.Equal(["shown"], manifest.Commands.Select(command => command.Id));
    }

    [Fact]
    public void A_command_is_announced_with_its_variables_and_control_mapping()
    {
        CommandDocument profile = Command(
            "set_display_profile",
            parameters:
            [
                Choice("Profile", "Normal-Mode", "TV-Mode"),
                new ParameterDefinition("UpdateAdapterIds", ParameterTypes.Flag, Default: JsonSerializer.SerializeToElement(false)),
            ],
            control: Select("Profile"),
            captureOutput: true) with
        {
            Description = "Applies a saved DisplayConfig profile.",
            Icon = "mdi:monitor-multiple",
        };

        CommandDescriptor descriptor = Assert.Single(Build(commands: [profile]).Commands);

        Assert.Equal("set_display_profile", descriptor.Id);
        Assert.Equal("Command set_display_profile", descriptor.Name);
        Assert.Equal("mdi:monitor-multiple", descriptor.Icon);
        Assert.Equal("Applies a saved DisplayConfig profile.", descriptor.Description);
        Assert.True(descriptor.ReturnsOutput);
        Assert.Equal(new ControlDescriptor("select", "Profile"), descriptor.Control);

        VariableDescriptor[] variables = [.. descriptor.Vars!];
        Assert.Equal(["Profile", "UpdateAdapterIds"], variables.Select(variable => variable.Name));
        Assert.Equal("enum", variables[0].Type);
        Assert.True(variables[0].Required);
        Assert.Equal(["Normal-Mode", "TV-Mode"], variables[0].Options);
        Assert.Equal("bool", variables[1].Type);
        Assert.Null(variables[1].Required);
        Assert.Equal(JsonValueKind.False, variables[1].Default!.Value.ValueKind);
    }

    [Fact]
    public void Number_limits_text_limits_and_secrets_are_announced()
    {
        CommandDocument command = Command(
            "say",
            parameters:
            [
                new ParameterDefinition("Level", ParameterTypes.Real, Min: 0, Max: 1, Step: 0.1, Unit: "x", Description: "The level"),
                new ParameterDefinition("Message", ParameterTypes.Text, Required: true, Pattern: "^[a-z]+$", MaxLength: 200),
                new ParameterDefinition("Token", ParameterTypes.Text, Secret: true),
            ],
            control: new HaControl(HaControlTypes.Text, "Message"));

        VariableDescriptor[] variables = [.. Assert.Single(Build(commands: [command]).Commands).Vars!];

        Assert.Equal((0d, 1d, 0.1, "x", "The level"), (variables[0].Min, variables[0].Max, variables[0].Step, variables[0].Unit, variables[0].Description));
        Assert.Equal(("^[a-z]+$", 200), (variables[1].Pattern, variables[1].MaxLength));
        Assert.True(variables[2].Secret);
        Assert.Null(variables[0].Secret);
    }

    [Fact]
    public void Control_types_and_the_state_metric_map_one_to_one()
    {
        CommandDescriptor number = Assert.Single(Build(commands: [Volume()]).Commands);
        CommandDescriptor button = Assert.Single(Build(commands: [Command("lock", control: new HaControl(HaControlTypes.Button))]).Commands);

        Assert.Equal(new ControlDescriptor("number", "Level", "audio.volume"), number.Control);
        Assert.Equal(new ControlDescriptor("button"), button.Control);
    }

    [Fact]
    public void A_command_without_control_or_with_control_none_has_no_control_member()
    {
        CommandDescriptor none = Assert.Single(Build(commands: [Command("a", control: new HaControl(HaControlTypes.None))]).Commands);
        CommandDescriptor absent = Assert.Single(Build(commands: [Command("a")]).Commands);

        Assert.Null(none.Control);
        Assert.Null(absent.Control);
        Assert.Equal(RevOf(Command("a", control: new HaControl(HaControlTypes.None))), RevOf(Command("a")));
    }

    [Fact]
    public void A_command_without_parameters_or_output_leaves_those_members_out()
    {
        CommandDescriptor descriptor = Assert.Single(Build(commands: [Command("lock")]).Commands);

        Assert.Null(descriptor.Vars);
        Assert.Null(descriptor.ReturnsOutput);
        Assert.Null(descriptor.Icon);
        Assert.Null(descriptor.Description);
    }

    [Fact]
    public void Events_are_the_catalog_sorted_by_type()
    {
        LinkManifest manifest = Build();

        Assert.NotNull(manifest.Events);
        Assert.Equal(EventCatalog.Types.Select(entry => entry.Type).Order(StringComparer.Ordinal), manifest.Events!.Select(entry => entry.Type));
        Assert.Contains(new EventDescriptor("session_locked", "Session locked"), manifest.Events!);
    }

    [Fact]
    public void Features_are_sorted_without_duplicates_and_left_out_when_empty()
    {
        Assert.Equal(["notify", "wol"], Build(features: ["wol", "notify", "wol"]).Features);
        Assert.Null(Build(features: []).Features);
        Assert.Null(Build().Features);
    }

    [Fact]
    public void The_agent_is_announced_with_its_mac_addresses_as_a_sorted_set()
    {
        AgentInfo agent = Agent() with { Macs = ["B2:00:00:00:00:02", "A1:00:00:00:00:01", "B2:00:00:00:00:02"] };

        LinkManifest manifest = Build(agent: agent);

        Assert.Equal(["A1:00:00:00:00:01", "B2:00:00:00:00:02"], manifest.Agent.Macs);
        Assert.Equal(agent.Id, manifest.Agent.Id);
        Assert.Equal(agent.UiUrl, manifest.Agent.UiUrl);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ManifestBuilder.Build(null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => ManifestBuilder.Build(Agent(), null!, [], []));
        Assert.Throws<ArgumentNullException>(() => ManifestBuilder.Build(Agent(), [], null!, []));
        Assert.Throws<ArgumentNullException>(() => ManifestBuilder.Build(Agent(), [], [], null!));
    }

    // ------------------------------------------------------------------
    // Revision
    // ------------------------------------------------------------------

    [Fact]
    public void The_revision_is_the_one_that_the_protocol_defines()
    {
        LinkManifest manifest = Build(commands: [Volume()]);

        Assert.Matches("^[0-9a-f]{12}$", manifest.Rev);
        Assert.Equal(ManifestRevision.Compute(manifest), manifest.Rev);
        Assert.True(ManifestRevision.IsCurrent(manifest));
    }

    [Fact]
    public void Equal_inputs_give_the_same_revision_whatever_their_order()
    {
        CommandDocument[] commands = [Command("alpha"), Command("beta"), Volume()];
        MetricCatalogEntry[] metrics = [Metric("cpu.usage"), Metric("disk.c.free"), Metric("net.up")];

        string first = Build(commands, metrics, features: ["a", "b"]).Rev;
        string second = Build([.. Enumerable.Reverse(commands)], [.. Enumerable.Reverse(metrics)], features: ["b", "a"]).Rev;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Enabling_and_disabling_a_command_changes_the_revision()
    {
        string enabled = RevOf(Command("a"));
        string disabled = RevOf(Command("a", enabled: false));

        Assert.NotEqual(enabled, disabled);
        Assert.Equal(RevOf(), disabled);
    }

    [Fact]
    public void Exposing_and_hiding_a_command_changes_the_revision()
    {
        Assert.NotEqual(RevOf(Command("a")), RevOf(Command("a", exposeToHa: false)));
        Assert.Equal(RevOf(), RevOf(Command("a", exposeToHa: false)));
    }

    [Fact]
    public void Editing_the_parameters_of_a_command_changes_the_revision()
    {
        ParameterDefinition level = new("Level", ParameterTypes.WholeNumber, Required: true, Min: 0, Max: 100);
        string baseline = RevOf(Command("a", parameters: [level]));

        CommandDocument[] edits =
        [
            Command("a", parameters: [level with { Max = 90 }]),
            Command("a", parameters: [level with { Min = 5 }]),
            Command("a", parameters: [level with { Required = false }]),
            Command("a", parameters: [level with { Default = JsonSerializer.SerializeToElement(10) }]),
            Command("a", parameters: [level with { Name = "Volume" }]),
            Command("a", parameters: [level with { Unit = "%" }]),
            Command("a", parameters: [level with { Description = "How loud" }]),
            Command("a", parameters: [level with { Secret = true }]),
            Command("a", parameters: [level, new ParameterDefinition("Fade", ParameterTypes.Flag)]),
            Command("a"),
        ];

        foreach (CommandDocument edit in edits)
        {
            Assert.NotEqual(baseline, RevOf(edit));
        }

        Assert.Equal(edits.Length, edits.Select(edit => RevOf(edit)).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Changing_the_options_of_an_enum_parameter_changes_the_revision()
    {
        Assert.NotEqual(
            RevOf(Command("a", parameters: [Choice("Mode", "Off", "On")])),
            RevOf(Command("a", parameters: [Choice("Mode", "Off", "On", "Auto")])));
    }

    [Fact]
    public void Changing_what_a_client_sees_of_a_command_changes_the_revision()
    {
        CommandDocument baseline = Volume();
        string rev = RevOf(baseline);

        Assert.NotEqual(rev, RevOf(baseline with { Name = "Other name" }));
        Assert.NotEqual(rev, RevOf(baseline with { Description = "Text" }));
        Assert.NotEqual(rev, RevOf(baseline with { Icon = "mdi:volume-high" }));
        Assert.NotEqual(rev, RevOf(baseline with { CaptureOutput = true }));
        Assert.NotEqual(rev, RevOf(baseline with { HaControl = new HaControl(HaControlTypes.Number, "Level") }));
        Assert.NotEqual(rev, RevOf(baseline with { HaControl = new HaControl(HaControlTypes.Number, "Level", "audio.other") }));
        Assert.NotEqual(rev, RevOf(baseline with { HaControl = null }));
    }

    [Fact]
    public void Editing_only_the_script_of_a_command_does_not_change_the_revision()
    {
        CommandDocument baseline = Volume();
        string rev = RevOf(baseline);

        Assert.Equal(rev, RevOf(baseline.WithCode("Write-Output 'a completely different script body'")));
        Assert.Equal(rev, RevOf(baseline with { Action = new PowerShellAction(PowerShellSources.File, Script: "set_volume.ps1") }));
        Assert.Equal(rev, RevOf(baseline with { Action = new PowerShellAction(PowerShellSources.File, Script: "renamed.ps1", Engine: PowerShellEngines.Pwsh) }));
        Assert.Equal(rev, RevOf(baseline with { Action = new ProcessAction("volume.exe", ["{{Level}}"]) }));
    }

    [Fact]
    public void Settings_that_a_client_cannot_see_do_not_change_the_revision()
    {
        CommandDocument baseline = Volume();
        string rev = RevOf(baseline);

        Assert.Equal(rev, RevOf(baseline with { TimeoutSeconds = 5 }));
        Assert.Equal(rev, RevOf(baseline with { Concurrency = ConcurrencyModes.Parallel }));
        Assert.Equal(rev, RevOf(baseline with { CooldownSeconds = 30 }));
        Assert.Equal(rev, RevOf(baseline with { RunAs = RunAsValues.User }));
        Assert.Equal(rev, RevOf(baseline with { Revision = 7, UpdatedAt = DateTimeOffset.UnixEpoch, Origin = DocumentOrigins.Hub }));
    }

    [Fact]
    public void Editing_a_command_that_is_not_announced_does_not_change_the_revision()
    {
        CommandDocument hidden = Command("hidden", exposeToHa: false);
        string rev = RevOf(Volume(), hidden);

        Assert.Equal(rev, RevOf(Volume(), hidden with { Name = "Renamed" }));
        Assert.Equal(rev, RevOf(Volume(), hidden.WithCode("changed")));
        Assert.Equal(rev, RevOf(Volume(), hidden with { Parameters = [Choice("Mode", "A")] }));
    }

    [Fact]
    public void Changing_the_metrics_agent_or_features_changes_the_revision()
    {
        string baseline = Build().Rev;

        Assert.NotEqual(baseline, Build(metrics: [Metric("cpu.usage"), Metric("disk.c.free")]).Rev);
        Assert.NotEqual(baseline, Build(metrics: [Metric("cpu.usage", interval: 1)]).Rev);
        Assert.NotEqual(baseline, Build(agent: Agent("Study-PC")).Rev);
        Assert.NotEqual(baseline, Build(features: ["wol"]).Rev);
        Assert.Equal(baseline, Build(metrics: [Metric("cpu.usage"), Metric("hidden.metric", exposeToHa: false)]).Rev);
        Assert.Equal(baseline, Build(metrics: [Metric("cpu.usage") with { Enabled = false }]).Rev);
    }

    // ------------------------------------------------------------------
    // Schema
    // ------------------------------------------------------------------

    [Fact]
    public void An_empty_manifest_validates_against_the_schema()
    {
        Assert.Empty(SchemaErrors(Build(metrics: [])));
    }

    [Fact]
    public void A_manifest_with_every_kind_of_command_and_metric_validates_against_the_schema()
    {
        CommandDocument[] commands =
        [
            Volume(),
            Command("lock_session", control: new HaControl(HaControlTypes.Button), captureOutput: true) with { Icon = "mdi:lock", Description = "Locks the session." },
            Command("set_display_profile", parameters: [Choice("Profile", "Normal-Mode", "TV-Mode")], control: Select("Profile")),
            Command("set_mute", parameters: [new ParameterDefinition("Mute", ParameterTypes.Flag, Required: true)], control: new HaControl(HaControlTypes.Switch, "Mute")),
            Command(
                "say_text",
                parameters:
                [
                    new ParameterDefinition("Message", ParameterTypes.Text, Required: true, Pattern: "^[A-Za-z0-9 .,!?-]+$", MaxLength: 200),
                    new ParameterDefinition("Voice", ParameterTypes.Text, Default: JsonSerializer.SerializeToElement("Default")),
                    new ParameterDefinition("Speed", ParameterTypes.Real, Default: JsonSerializer.SerializeToElement(1.5), Min: 0.5, Max: 2, Step: 0.1),
                    new ParameterDefinition("Secret", ParameterTypes.Text, Secret: true),
                ],
                control: new HaControl(HaControlTypes.Text, "Message")),
            Command("no_control"),
            Command("none_control", control: new HaControl(HaControlTypes.None)),
        ];
        MetricCatalogEntry[] metrics =
        [
            Metric("cpu.usage"),
            Metric("session.locked", kind: "binary_sensor", valueType: "bool", interval: 0),
            Metric("custom.my_sensor", valueType: "string"),
        ];

        LinkManifest manifest = Build(commands, metrics, features: ["wol", "notify"]);

        Assert.Empty(SchemaErrors(manifest));
        Assert.True(ManifestRevision.IsCurrent(manifest));
        Assert.Equal(commands.Length, manifest.Commands.Count);
    }

    [Fact]
    public void The_built_manifest_survives_a_round_trip_through_the_link_codec()
    {
        LinkManifest manifest = Build([Volume(), Command("lock", control: new HaControl(HaControlTypes.Button))], [Metric("cpu.usage")], features: ["notify"]);

        LinkManifest parsed = LinkCodec.ParseManifest(LinkCodec.ToElement(manifest));

        Assert.Equal(manifest.Rev, parsed.Rev);
        Assert.True(ManifestRevision.IsCurrent(parsed));
        Assert.Equal(manifest.Commands.Select(command => command.Id), parsed.Commands.Select(command => command.Id));
    }
}
