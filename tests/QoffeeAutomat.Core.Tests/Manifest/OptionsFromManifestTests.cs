using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using static QoffeeAutomat.Core.Tests.Manifest.ManifestTestSupport;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Tests.Manifest;

/// <summary>
/// <c>optionsFrom</c> in the manifest (PLAN.md S06, T713): an enum variable takes its options from the current options of a metric
/// when the manifest is built, so they follow the state of the PC and the revision follows them. A variable without
/// <c>optionsFrom</c> is announced exactly as before.
/// </summary>
[Trait("Category", "OptionsFrom")]
public sealed class OptionsFromManifestTests
{
    private const string OutputDevice = "audio.output_device";

    private static MetricCatalogEntry DeviceMetric(string id = OutputDevice, bool exposeToHa = true, params string[] options) => new(
        new MetricDescriptor(
            id,
            "Audio device",
            "sensor",
            "enum",
            Options: options.Length == 0 ? null : options,
            EnabledDefault: true,
            IntervalSeconds: 30,
            Context: "user"),
        Enabled: true,
        exposeToHa,
        IntervalSeconds: 30,
        Deadband: null,
        IsCustom: false);

    private static CommandDocument DeviceCommand(string? optionsFrom = OutputDevice, IReadOnlyList<string>? options = null) => Command(
        "audio_output_device",
        action: new BuiltinAction("audio.set_default_output"),
        parameters: [new ParameterDefinition("Device", ParameterTypes.Choice, Required: true, Options: options, OptionsFrom: optionsFrom)],
        control: new HaControl(HaControlTypes.Select, "Device", OutputDevice),
        runAs: RunAsValues.User);

    private static LinkManifest Build(IEnumerable<MetricCatalogEntry> metrics, params CommandDocument[] commands)
    {
        return ManifestBuilder.Build(Agent(), metrics, commands, EventCatalog.Types);
    }

    private static VariableDescriptor DeviceVariable(LinkManifest manifest)
    {
        CommandDescriptor command = Assert.Single(manifest.Commands);
        return Assert.Single(command.Vars!);
    }

    [Fact]
    public void The_options_of_the_variable_are_the_current_options_of_the_metric()
    {
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, true, "Headphones", "Speakers")], DeviceCommand());

        VariableDescriptor device = DeviceVariable(manifest);

        Assert.Equal("enum", device.Type);
        Assert.Equal(["Headphones", "Speakers"], device.Options);
    }

    [Fact]
    public void The_order_of_the_options_of_the_metric_is_kept()
    {
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, true, "Zebra", "alpha", "Mike")], DeviceCommand());

        Assert.Equal(["Zebra", "alpha", "Mike"], DeviceVariable(manifest).Options);
    }

    [Fact]
    public void The_manifest_with_resolved_options_is_valid_against_the_schema()
    {
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, true, "Headphones", "Speakers")], DeviceCommand());

        Assert.Empty(SchemaErrors(manifest));
    }

    [Fact]
    public void The_manifest_does_not_carry_optionsFrom_only_the_resulting_options()
    {
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, true, "Speakers")], DeviceCommand());

        string json = JsonSerializer.Serialize(manifest, LinkJsonContext.Default.Manifest);

        Assert.DoesNotContain("options_from", json, StringComparison.Ordinal);
        Assert.DoesNotContain("optionsFrom", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_metric_without_options_gives_the_variable_none_and_the_manifest_stays_valid()
    {
        // No audio device, or no session helper yet: the schema does not allow an empty list, so the member is left out.
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, true)], DeviceCommand());

        Assert.Null(DeviceVariable(manifest).Options);
        Assert.Empty(SchemaErrors(manifest));
    }

    [Fact]
    public void A_metric_that_is_not_in_the_catalog_gives_the_variable_no_options()
    {
        LinkManifest manifest = Build([Metric("cpu.usage")], DeviceCommand());

        Assert.Null(DeviceVariable(manifest).Options);
        Assert.Empty(SchemaErrors(manifest));
    }

    [Fact]
    public void A_metric_that_is_not_exposed_to_home_assistant_still_supplies_the_options()
    {
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, false, "Speakers")], DeviceCommand());

        Assert.DoesNotContain(manifest.Metrics, metric => metric.Id == OutputDevice);
        Assert.Equal(["Speakers"], DeviceVariable(manifest).Options);
    }

    [Fact]
    public void A_metric_id_is_matched_exactly()
    {
        LinkManifest manifest = Build([DeviceMetric("audio.output_device_2", true, "Other")], DeviceCommand());

        Assert.Null(DeviceVariable(manifest).Options);
    }

    [Fact]
    public void OptionsFrom_replaces_the_options_of_the_document()
    {
        // The validator refuses both together; the builder still never mixes them if a document gets here.
        LinkManifest manifest = Build([DeviceMetric(OutputDevice, true, "Speakers")], DeviceCommand(OutputDevice, options: ["Old"]));

        Assert.Equal(["Speakers"], DeviceVariable(manifest).Options);
    }

    [Fact]
    public void The_revision_changes_when_a_device_appears_and_when_it_disappears()
    {
        string one = Build([DeviceMetric(OutputDevice, true, "Speakers")], DeviceCommand()).Rev;
        string two = Build([DeviceMetric(OutputDevice, true, "Headphones", "Speakers")], DeviceCommand()).Rev;
        string back = Build([DeviceMetric(OutputDevice, true, "Speakers")], DeviceCommand()).Rev;

        Assert.NotEqual(one, two);
        Assert.Equal(one, back);
    }

    [Fact]
    public void The_revision_is_stable_while_the_devices_stay_the_same()
    {
        string first = Build([DeviceMetric(OutputDevice, true, "Headphones", "Speakers")], DeviceCommand()).Rev;
        string second = Build([DeviceMetric(OutputDevice, true, "Headphones", "Speakers")], DeviceCommand()).Rev;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Two_variables_of_different_commands_follow_their_own_metrics()
    {
        CommandDocument input = Command(
            "audio_input_device",
            action: new BuiltinAction("audio.set_default_input"),
            parameters: [new ParameterDefinition("Device", ParameterTypes.Choice, Required: true, OptionsFrom: "audio.input_device")],
            control: new HaControl(HaControlTypes.Select, "Device", "audio.input_device"),
            runAs: RunAsValues.User);

        LinkManifest manifest = Build(
            [DeviceMetric(OutputDevice, true, "Speakers"), DeviceMetric("audio.input_device", true, "Headset microphone")],
            DeviceCommand(),
            input);

        Assert.Equal(["Speakers"], manifest.Commands.Single(command => command.Id == "audio_output_device").Vars![0].Options);
        Assert.Equal(["Headset microphone"], manifest.Commands.Single(command => command.Id == "audio_input_device").Vars![0].Options);
    }

    // ------------------------------------------------------------------
    // Backward compatibility: a command without optionsFrom is announced as before
    // ------------------------------------------------------------------

    [Fact]
    public void A_variable_with_fixed_options_is_announced_unchanged_whatever_the_catalog_holds()
    {
        CommandDocument profile = Command(
            "set_profile",
            parameters: [Choice("Profile", "Normal", "TV", "Study")],
            control: Select("Profile"));

        LinkManifest withDevices = Build([DeviceMetric(OutputDevice, true, "Speakers")], profile);
        LinkManifest without = Build([Metric("cpu.usage")], profile);

        Assert.Equal(["Normal", "TV", "Study"], Assert.Single(Assert.Single(withDevices.Commands).Vars!).Options);
        Assert.Equal(["Normal", "TV", "Study"], Assert.Single(Assert.Single(without.Commands).Vars!).Options);
    }

    [Fact]
    public void Describe_without_metric_options_gives_the_same_descriptor_as_before()
    {
        CommandDocument profile = Command(
            "set_profile",
            parameters: [Choice("Profile", "Normal", "TV"), new ParameterDefinition("Level", ParameterTypes.WholeNumber, Min: 0, Max: 10)],
            control: Select("Profile"));
        IReadOnlyDictionary<string, IReadOnlyList<string>> unrelated = new Dictionary<string, IReadOnlyList<string>> { [OutputDevice] = ["Speakers"] };

        CommandDescriptor plain = ManifestBuilder.Describe(profile);
        CommandDescriptor withMap = ManifestBuilder.Describe(profile, unrelated);

        Assert.Equal(plain.Id, withMap.Id);
        Assert.Equal(plain.Control, withMap.Control);
        Assert.Equal(plain.Vars!.Count, withMap.Vars!.Count);
        for (int index = 0; index < plain.Vars.Count; index++)
        {
            // The options are the very list of the document, so the records are equal member by member.
            Assert.Equal(plain.Vars[index], withMap.Vars[index]);
        }

        Assert.Equal(["Normal", "TV"], plain.Vars[0].Options);
        Assert.Null(plain.Vars[1].Options);
    }

    [Fact]
    public void Describe_resolves_optionsFrom_from_the_map_it_is_given()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> map = new Dictionary<string, IReadOnlyList<string>> { [OutputDevice] = ["A", "B"] };

        Assert.Equal(["A", "B"], ManifestBuilder.Describe(DeviceCommand(), map).Vars![0].Options);
        Assert.Null(ManifestBuilder.Describe(DeviceCommand()).Vars![0].Options);
    }
}
