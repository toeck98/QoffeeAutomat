using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Templates;

/// <summary>
/// The command templates under <c>templates/</c> (T309, ported from the prototype): every document passes the config
/// schema and the semantic validator, scripts exist and declare the parameters of their commands, and the example mode
/// refers to templates that exist and to values that they accept.
/// </summary>
[Trait("Category", "Templates")]
public sealed class CommandTemplateTests
{
    private static readonly string[] RequiredCommands =
    [
        "set_display_profile",
        "save_display_profile",
        "mode_example",
    ];

    [Fact]
    public void The_required_templates_exist()
    {
        IReadOnlyDictionary<string, CommandDocument> commands = TemplateFiles.AllCommands();

        foreach (string id in RequiredCommands)
        {
            Assert.True(commands.ContainsKey(id), $"templates/commands/{id}.json is missing.");
        }

        Assert.Contains(TemplateFiles.ScriptFiles(), path => Path.GetFileName(path) == "set_display_profile.ps1");
        Assert.Contains(TemplateFiles.ScriptFiles(), path => Path.GetFileName(path) == "save_display_profile.ps1");
        Assert.True(File.Exists(TemplateFiles.MigrationGuide), "docs/migration-from-prototype.md is missing.");
    }

    [Fact]
    public void Every_command_template_matches_the_command_schema()
    {
        IReadOnlyList<string> files = TemplateFiles.CommandFiles();
        Assert.NotEmpty(files);

        foreach (string path in files)
        {
            IReadOnlyList<string> errors = TemplateFiles.SchemaErrors(path);
            Assert.True(errors.Count == 0, $"{Path.GetFileName(path)} does not match the schema: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void Every_command_template_passes_the_validator()
    {
        IReadOnlyDictionary<string, CommandDocument> commands = TemplateFiles.AllCommands();
        Assert.NotEmpty(commands);

        foreach (CommandDocument command in commands.Values)
        {
            ValidationResult result = DocumentValidator.ValidateCommand(command, id => commands.GetValueOrDefault(id));
            Assert.True(
                result.IsValid,
                $"{command.Id}: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} {error.Code} {error.Message}")));
        }
    }

    [Fact]
    public void The_id_of_a_template_is_its_file_name()
    {
        foreach (string path in TemplateFiles.CommandFiles())
        {
            Assert.Equal(Path.GetFileNameWithoutExtension(path), TemplateFiles.ReadCommand(path).Id);
        }
    }

    [Fact]
    public void Templates_carry_no_revision_stamp_or_origin()
    {
        // The stamps belong to the store; a template that is imported must not claim a revision of someone else's.
        foreach (string path in TemplateFiles.CommandFiles())
        {
            CommandDocument command = TemplateFiles.ReadCommand(path);
            Assert.True(command.Revision is null && command.UpdatedAt is null && command.Origin is null, path);
        }
    }

    [Fact]
    public void Script_templates_declare_every_parameter_of_their_command()
    {
        IReadOnlyDictionary<string, CommandDocument> commands = TemplateFiles.AllCommands();
        int checkedScripts = 0;

        foreach (CommandDocument command in commands.Values)
        {
            if (command.Action is not PowerShellAction { Source: PowerShellSources.File } action)
            {
                continue;
            }

            string path = Path.Combine(TemplateFiles.ScriptsDirectory, action.Script!);
            Assert.True(File.Exists(path), $"{command.Id} refers to the missing script {action.Script}.");

            string script = File.ReadAllText(path);
            foreach (ParameterDefinition parameter in command.Parameters ?? [])
            {
                var declaration = new Regex(@"\$" + Regex.Escape(parameter.Name) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                Assert.True(declaration.IsMatch(script), $"{action.Script} does not declare the parameter {parameter.Name} of {command.Id}.");
            }

            checkedScripts++;
        }

        Assert.True(checkedScripts >= 2, "Both display profile scripts should be referenced by a command.");
    }

    [Fact]
    public void Every_script_template_is_used_by_a_command()
    {
        HashSet<string> used = TemplateFiles.AllCommands().Values
            .Select(command => command.Action)
            .OfType<PowerShellAction>()
            .Where(action => action.Script is not null)
            .Select(action => action.Script!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string path in TemplateFiles.ScriptFiles())
        {
            Assert.Contains(Path.GetFileName(path), used);
        }
    }

    [Fact]
    public void Script_templates_bind_values_instead_of_splicing_them()
    {
        // D-012: values arrive as typed named parameters; a template must not build code from them.
        foreach (string path in TemplateFiles.ScriptFiles())
        {
            string script = File.ReadAllText(path);
            Assert.Matches(@"(?im)^\s*param\s*\(", script);
            Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"(?i)\biex\b", script);
        }
    }

    [Fact]
    public void Set_display_profile_is_a_select_bound_to_the_profile()
    {
        CommandDocument command = TemplateFiles.AllCommands()["set_display_profile"];

        Assert.Equal(RunAsValues.User, command.RunAs);
        Assert.True(command.ExposeToHa);
        Assert.Equal(HaControlTypes.Select, command.HaControl?.Type);
        Assert.Equal("Profile", command.HaControl?.Parameter);

        PowerShellAction action = Assert.IsType<PowerShellAction>(command.Action);
        Assert.Equal(PowerShellSources.File, action.Source);
        Assert.Equal("set_display_profile.ps1", action.Script);

        ParameterDefinition profile = Assert.Single(command.Parameters!, p => p.Name == "Profile");
        Assert.Equal(ParameterTypes.Choice, profile.Type);
        Assert.True(profile.Required);
        Assert.NotEmpty(profile.Options!);

        ParameterDefinition update = Assert.Single(command.Parameters!, p => p.Name == "UpdateAdapterIds");
        Assert.Equal(ParameterTypes.Flag, update.Type);
        Assert.False(update.Required);
    }

    [Fact]
    public void The_example_mode_applies_a_profile_launches_programs_and_waits()
    {
        IReadOnlyDictionary<string, CommandDocument> commands = TemplateFiles.AllCommands();
        CommandDocument mode = commands["mode_example"];

        SequenceAction sequence = Assert.IsType<SequenceAction>(mode.Action);
        Assert.Equal(SequenceErrorModes.Stop, sequence.OnError);

        SequenceStep first = sequence.Steps[0];
        Assert.Equal("set_display_profile", first.Command);
        Assert.Contains(sequence.Steps, step => step.DelaySeconds is > 0);
        Assert.Contains(sequence.Steps, step => step.Command is not null
            && commands[step.Command].Action is ProcessAction { WaitForExit: false });
        Assert.Contains(sequence.Steps, step => step.Command is "launch_steam_big_picture");

        // The delay sits between launches, as in TV-Rennsport-Mode of the prototype.
        int delay = sequence.Steps.ToList().FindIndex(step => step.DelaySeconds is not null);
        Assert.True(delay > 1 && delay < sequence.Steps.Count - 1, "The wait belongs between two program launches.");
    }

    [Fact]
    public void Steps_of_the_example_mode_refer_to_templates_and_pass_accepted_values()
    {
        IReadOnlyDictionary<string, CommandDocument> commands = TemplateFiles.AllCommands();
        SequenceAction sequence = Assert.IsType<SequenceAction>(commands["mode_example"].Action);

        foreach (SequenceStep step in sequence.Steps.Where(step => step.Command is not null))
        {
            Assert.True(commands.ContainsKey(step.Command!), $"Step {step.Command} has no template.");
            CommandDocument target = commands[step.Command!];
            Assert.True(target.Enabled, $"{step.Command} is disabled in its template.");

            foreach ((string name, JsonElement value) in step.Vars ?? new Dictionary<string, JsonElement>())
            {
                ParameterDefinition? parameter = target.Parameters?.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                Assert.NotNull(parameter);

                if (parameter.Type == ParameterTypes.Choice)
                {
                    Assert.Contains(value.GetString(), parameter.Options!);
                }
            }
        }
    }

    [Fact]
    public void The_templates_hold_no_secrets_and_no_config_file_names()
    {
        string root = Path.GetDirectoryName(TemplateFiles.CommandsDirectory)!;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(path);
            Assert.DoesNotMatch(@"^config\.(ya?ml|json)$", name);

            string text = File.ReadAllText(path);
            Assert.DoesNotMatch(@"(?i)\b(password|passwd|api[_-]?key|token)\s*[:=]\s*['""]?[A-Za-z0-9]{8,}", text);
        }
    }
}
