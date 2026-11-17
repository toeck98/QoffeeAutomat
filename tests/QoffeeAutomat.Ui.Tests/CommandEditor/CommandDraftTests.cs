using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.CommandEditor;

/// <summary>
/// The model behind the command editor (T404): the id made from the name, the draft that turns into a document and back,
/// the param() block, the Home Assistant rules and the preview.
/// </summary>
[Trait("Category", "CommandEditor")]
public sealed class CommandDraftTests
{
    [Theory]
    [InlineData("Set display profile", "set_display_profile")]
    [InlineData("Bildschirm Größe ändern", "bildschirm_groesse_aendern")]
    [InlineData("3D mode", "cmd_3d_mode")]
    [InlineData("  Lock -- screen!  ", "lock_screen")]
    [InlineData("   ", "")]
    [InlineData("!!!", "")]
    public void The_id_is_made_from_the_name(string name, string expected)
    {
        Assert.Equal(expected, CommandSlug.FromName(name));
    }

    [Fact]
    public void A_long_name_makes_an_id_that_is_still_valid()
    {
        string id = CommandSlug.FromName(new string('a', 100) + " " + new string('b', 20));

        Assert.True(id.Length <= CommandSlug.MaxLength);
        Assert.True(CommandSlug.IsValid(id));
    }

    [Theory]
    [InlineData("lock_screen", true)]
    [InlineData("a", true)]
    [InlineData("Lock", false)]
    [InlineData("1lock", false)]
    [InlineData("lock-screen", false)]
    [InlineData("", false)]
    public void The_pattern_of_ids_is_checked(string id, bool valid)
    {
        Assert.Equal(valid, CommandSlug.IsValid(id));
    }

    [Fact]
    public void The_id_follows_the_name_until_it_is_edited()
    {
        var draft = new CommandDraft { Name = "First" };
        Assert.Equal("first", draft.Id);

        draft.Name = "First name";
        Assert.Equal("first_name", draft.Id);

        draft.Id = "mine";
        draft.Name = "Something else";
        Assert.Equal("mine", draft.Id);

        // Emptying the id hands it back to the name.
        draft.Id = string.Empty;
        Assert.Equal("something_else", draft.Id);
    }

    [Fact]
    public void The_id_of_a_saved_command_does_not_follow_the_name()
    {
        CommandDraft draft = CommandDraft.From(Samples.Command("backup", "Back up documents"));

        draft.Name = "Something else";

        Assert.False(draft.IsNew);
        Assert.Equal("backup", draft.Id);
    }

    [Fact]
    public void A_stored_command_comes_back_unchanged_from_the_draft()
    {
        var document = new CommandDocument(
            DocumentSchema.CurrentVersion,
            "set_profile",
            "Set profile",
            true,
            true,
            RunAsValues.UserElevated,
            new PowerShellAction(PowerShellSources.Inline, Code: "param([string]$Profile)\nWrite-Output $Profile"),
            "Applies a profile.",
            "mdi:monitor",
            [
                new ParameterDefinition("Profile", ParameterTypes.Choice, true, Options: ["Normal", "TV"], Description: "The profile."),
                new ParameterDefinition("Count", ParameterTypes.WholeNumber, false, Samples.Number(2), 1, 5, 1, Unit: "x"),
                new ParameterDefinition("Name", ParameterTypes.Text, false, Pattern: "^[a-z]+$", MaxLength: 20, Secret: true),
                new ParameterDefinition("Force", ParameterTypes.Flag, false, True()),
            ],
            new HaControl(HaControlTypes.Select, "Profile", "custom.profile"),
            120,
            ConcurrencyModes.Queue,
            5,
            true,
            7,
            Samples.Now,
            DocumentOrigins.Local);

        CommandDocument rebuilt = CommandDraft.From(document).Build().Document;

        Assert.Equal(Serialize(document), Serialize(rebuilt));
    }

    [Fact]
    public void Every_action_type_survives_the_draft()
    {
        CommandAction[] actions =
        [
            new ProcessAction("notepad.exe", ["--open", "{{Name}}"], "C:\\Work", false),
            new BuiltinAction("power.shutdown"),
            new SequenceAction(
                [
                    new SequenceStep(Command: "lock_screen"),
                    new SequenceStep(DelaySeconds: 2.5),
                    new SequenceStep(Command: "backup", Vars: new Dictionary<string, JsonElement> { ["Target"] = Samples.Text("D:\\") }),
                ],
                SequenceErrorModes.Continue),
            new PowerShellAction(PowerShellSources.File, Script: "set_profile.ps1", Engine: PowerShellEngines.Pwsh),
        ];

        foreach (CommandAction action in actions)
        {
            CommandDocument document = Samples.Command("x", action: action);

            CommandDocument rebuilt = CommandDraft.From(document).Build().Document;

            Assert.Equal(Serialize(document), Serialize(rebuilt));
        }
    }

    [Fact]
    public void Text_that_is_no_number_is_reported_with_its_path_and_is_not_sent_as_a_number()
    {
        CommandDraft draft = new() { Name = "Draft", TimeoutText = "soon", CooldownText = "1.5" };
        draft.Parameters.Add(new ParameterDraft { Name = "Count", Type = ParameterTypes.WholeNumber, Min = "low", Max = "9" });

        DraftBuild build = draft.Build();

        Assert.Contains(build.Problems, problem => problem.Path == "/timeoutSeconds" && problem.Code == "not_a_number");
        Assert.Contains(build.Problems, problem => problem.Path == "/cooldownSeconds");
        Assert.Contains(build.Problems, problem => problem.Path == "/parameters/0/min");
        Assert.DoesNotContain(build.Problems, problem => problem.Path == "/parameters/0/max");
        Assert.Null(build.Document.Parameters![0].Min);
        Assert.Equal(9, build.Document.Parameters[0].Max);
    }

    [Fact]
    public void Fields_that_do_not_belong_to_the_type_are_left_out()
    {
        CommandDraft draft = new();
        draft.Parameters.Add(new ParameterDraft { Name = "Mode", Type = ParameterTypes.Choice, OptionsText = "a\n\n b \n", Min = "1", Pattern = "x", MaxLength = "3" });

        ParameterDefinition parameter = draft.Build().Document.Parameters![0];

        Assert.Equal(["a", "b"], parameter.Options);
        Assert.Null(parameter.Min);
        Assert.Null(parameter.Pattern);
        Assert.Null(parameter.MaxLength);
    }

    [Fact]
    public void Changing_the_type_of_a_parameter_clears_its_default()
    {
        var parameter = new ParameterDraft { Name = "Count", Type = ParameterTypes.WholeNumber, DefaultText = "5" };

        parameter.Type = ParameterTypes.Flag;

        Assert.Equal(string.Empty, parameter.DefaultText);
    }

    [Fact]
    public void A_default_that_does_not_fit_the_type_is_sent_as_text_so_that_the_agent_can_say_so()
    {
        var parameter = new ParameterDraft { Name = "Count", Type = ParameterTypes.WholeNumber, DefaultText = "many" };

        ParameterDefinition definition = parameter.ToDefinition("/parameters/0", []);

        Assert.Equal(JsonValueKind.String, definition.Default!.Value.ValueKind);
    }

    [Fact]
    public void Renaming_a_parameter_keeps_the_control_on_it_and_removing_it_frees_the_control()
    {
        CommandDraft draft = new() { HaType = HaControlTypes.Select, HaParameter = "Profile" };
        var row = new ParameterDraft { Name = "Profile", Type = ParameterTypes.Choice };
        draft.Parameters.Add(row);

        draft.RenameParameter(row, "Mode");
        Assert.Equal("Mode", draft.HaParameter);

        draft.RemoveParameter(row);
        Assert.Equal(string.Empty, draft.HaParameter);
        Assert.Empty(draft.Parameters);
    }

    [Fact]
    public void The_next_parameter_name_is_free()
    {
        CommandDraft draft = new();
        draft.Parameters.Add(new ParameterDraft { Name = "Value" });
        draft.Parameters.Add(new ParameterDraft { Name = "value2" });

        Assert.Equal("Value3", draft.NextParameterName());
    }

    [Fact]
    public void The_param_block_declares_every_parameter()
    {
        ParameterDefinition[] parameters =
        [
            new("Target", ParameterTypes.Text, true),
            new("Count", ParameterTypes.WholeNumber, Default: Samples.Number(3)),
            new("Mode", ParameterTypes.Choice, Default: Samples.Text("a"), Options: ["a", "b'c"]),
            new("Force", ParameterTypes.Flag, Default: True()),
            new("Ratio", ParameterTypes.Real),
            new(" ", ParameterTypes.Text),
        ];

        string block = PowerShellParamBlock.Build(parameters);

        Assert.Equal(
            "param(\n"
            + "    [Parameter(Mandatory)] [string]$Target,\n"
            + "    [int]$Count = 3,\n"
            + "    [ValidateSet('a', 'b''c')] [string]$Mode = 'a',\n"
            + "    [bool]$Force = $true,\n"
            + "    [double]$Ratio\n"
            + ")",
            block);
    }

    [Fact]
    public void There_is_no_block_without_named_parameters()
    {
        Assert.Equal(string.Empty, PowerShellParamBlock.Build([]));
        Assert.Equal("Write-Output 1", PowerShellParamBlock.Apply("Write-Output 1", string.Empty));
    }

    [Fact]
    public void The_block_is_added_at_the_top_when_there_is_none()
    {
        string block = "param(\n    [int]$N\n)";

        Assert.Equal(block + "\n", PowerShellParamBlock.Apply(string.Empty, block));
        Assert.Equal(block + "\n\nWrite-Output $N", PowerShellParamBlock.Apply("\nWrite-Output $N", block));
    }

    [Fact]
    public void The_block_that_is_there_is_replaced_and_the_rest_of_the_code_stays()
    {
        string block = "param(\n    [int]$N\n)";
        string code = "# Sets things.\nparam(\n  [string]$Old, # (the old one)\n  [string]$Text = 'a)b'\n)\nWrite-Output $Old\n";

        string result = PowerShellParamBlock.Apply(code, block);

        Assert.Equal("# Sets things.\nparam(\n    [int]$N\n)\nWrite-Output $Old\n", result);
        Assert.Equal(result, PowerShellParamBlock.Apply(result, block));
    }

    [Fact]
    public void A_param_block_that_never_closes_is_not_touched_but_a_new_one_is_added_above()
    {
        string block = "param(\n    [int]$N\n)";

        string result = PowerShellParamBlock.Apply("param(\n  [string]$A\nWrite-Output 1", block);

        Assert.StartsWith(block + "\n\nparam(", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_controls_that_the_parameters_allow_are_offered()
    {
        Assert.Equal([HaControlTypes.None, HaControlTypes.Button], HaControlRules.ValidTypes([]));

        ParameterDefinition[] parameters =
        [
            new("Profile", ParameterTypes.Choice, true, Options: ["a"]),
            new("Count", ParameterTypes.WholeNumber),
        ];

        // A required choice without a default rules the button out; the choice and the number allow select and number.
        Assert.Equal([HaControlTypes.None, HaControlTypes.Select, HaControlTypes.Number], HaControlRules.ValidTypes(parameters));
        Assert.Equal(["Profile"], HaControlRules.ParametersFor(HaControlTypes.Select, parameters));
        Assert.Equal(["Count"], HaControlRules.ParametersFor(HaControlTypes.Number, parameters));
        Assert.Empty(HaControlRules.ParametersFor(HaControlTypes.Switch, parameters));
        Assert.Empty(HaControlRules.ParametersFor(HaControlTypes.Button, parameters));
    }

    [Fact]
    public void A_required_parameter_with_a_default_still_allows_the_button()
    {
        ParameterDefinition[] parameters = [new("Count", ParameterTypes.WholeNumber, true, Samples.Number(1))];

        Assert.Contains(HaControlTypes.Button, HaControlRules.ValidTypes(parameters));
    }

    [Fact]
    public void The_state_metric_must_have_the_type_of_the_control()
    {
        MetricInfo[] metrics = [Metric("custom.mode", "enum"), Metric("custom.on", "bool"), Metric("cpu.usage", "number")];

        Assert.Equal(["custom.mode"], HaControlRules.StateMetricsFor(HaControlTypes.Select, metrics).Select(metric => metric.Id));
        Assert.Equal(["custom.on"], HaControlRules.StateMetricsFor(HaControlTypes.Switch, metrics).Select(metric => metric.Id));
        Assert.Equal(["cpu.usage"], HaControlRules.StateMetricsFor(HaControlTypes.Number, metrics).Select(metric => metric.Id));
        Assert.Empty(HaControlRules.StateMetricsFor(HaControlTypes.Button, metrics));
    }

    [Fact]
    public void The_preview_describes_the_entity()
    {
        CommandDocument command = ProfileCommand();

        HaEntityPreview? entity = HaPreview.Entity(command);

        Assert.NotNull(entity);
        Assert.Equal("select", entity.Platform);
        Assert.Equal("Set profile", entity.Name);
        Assert.Equal(["Normal", "TV"], entity.Options);
        Assert.True(entity.IsOptimistic);
        Assert.Equal("custom.profile", HaPreview.Entity(command with { HaControl = command.HaControl! with { StateMetric = "custom.profile" } })!.StateMetric);
    }

    [Fact]
    public void A_command_that_is_off_or_hidden_or_without_a_control_makes_no_entity()
    {
        CommandDocument command = ProfileCommand();

        Assert.Null(HaPreview.Entity(command with { Enabled = false }));
        Assert.Null(HaPreview.Entity(command with { ExposeToHa = false }));
        Assert.Null(HaPreview.Entity(command with { HaControl = null }));
        Assert.Null(HaPreview.Entity(command with { HaControl = new HaControl(HaControlTypes.None) }));
    }

    [Fact]
    public void The_yaml_calls_the_service_with_examples_and_with_entered_values()
    {
        CommandDocument command = ProfileCommand();

        string examples = HaPreview.Snippet(command, null);
        string entered = HaPreview.Snippet(command, new Dictionary<string, JsonElement> { ["Profile"] = Samples.Text("TV"), ["Count"] = Samples.Number(4) });

        Assert.Equal(
            "action: qoffeeautomat.run_command\n"
            + "target:\n"
            + "  device_id: \"<the device of this PC>\"\n"
            + "data:\n"
            + "  command: \"set_profile\"\n"
            + "  variables:\n"
            + "    Profile: \"Normal\"\n"
            + "    Count: 1\n"
            + "  wait: true\n",
            examples);
        Assert.Contains("    Profile: \"TV\"\n    Count: 4\n", entered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_yaml_quotes_text_and_leaves_out_variables_when_there_are_none()
    {
        CommandDocument command = ProfileCommand() with
        {
            Parameters = [new ParameterDefinition("Text", ParameterTypes.Text)],
        };

        string yaml = HaPreview.Snippet(command, new Dictionary<string, JsonElement> { ["Text"] = Samples.Text("say \"hi\"\\n") });
        string plain = HaPreview.Snippet(command with { Parameters = null }, null);

        Assert.Contains("    Text: \"say \\\"hi\\\"\\\\n\"\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("variables", plain, StringComparison.Ordinal);
    }

    private static CommandDocument ProfileCommand()
    {
        return Samples.Command(
            "set_profile",
            "Set profile",
            action: new PowerShellAction(PowerShellSources.Inline, Code: "param($Profile)"),
            parameters:
            [
                new ParameterDefinition("Profile", ParameterTypes.Choice, true, Options: ["Normal", "TV"]),
                new ParameterDefinition("Count", ParameterTypes.WholeNumber, Min: 1, Max: 5),
            ]) with
        {
            HaControl = new HaControl(HaControlTypes.Select, "Profile"),
        };
    }

    private static JsonElement True()
    {
        using JsonDocument document = JsonDocument.Parse("true");
        return document.RootElement.Clone();
    }

    private static MetricInfo Metric(string id, string valueType)
    {
        return new MetricInfo(id, id, "sensor", valueType, "system", null, null, null, null, null, null, null, true, true, true, 5, null, false, false, null);
    }

    private static string Serialize(CommandDocument document)
    {
        return JsonSerializer.Serialize(document, ApiDocumentJsonContext.Default.CommandDocument);
    }
}
