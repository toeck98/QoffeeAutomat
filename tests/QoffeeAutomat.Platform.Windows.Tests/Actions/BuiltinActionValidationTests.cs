using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Windows.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// Every built-in action validates its variables through the common <see cref="VariableBinder"/> (PLAN.md S06, T306): the
/// parameter definitions of the catalog are the contract, and the binder refuses what they do not allow before an action
/// gets to run.
/// </summary>
[Trait("Category", "Actions")]
public sealed class BuiltinActionValidationTests
{
    /// <summary>One acceptable input per action; a new action without an entry fails <see cref="Every_action_has_an_example_input"/>.</summary>
    private static readonly Dictionary<string, (string Name, string Json)[]> ValidInputs = new()
    {
        [WindowsActionNames.Shutdown] = [],
        [WindowsActionNames.Restart] = [(WindowsActionParameters.Force, "true")],
        [WindowsActionNames.Sleep] = [],
        [WindowsActionNames.Hibernate] = [],
        [WindowsActionNames.LockSession] = [],
        [WindowsActionNames.Logoff] = [(WindowsActionParameters.Force, "false")],
        [WindowsActionNames.DisplayOff] = [],
        [WindowsActionNames.SetVolume] = [(WindowsActionParameters.Level, "35")],
        [WindowsActionNames.SetMute] = [(WindowsActionParameters.Muted, "false")],
        [WindowsActionNames.MediaKey] = [(WindowsActionParameters.Key, "\"next\"")],
        [WindowsActionNames.AppLaunch] = [(WindowsActionParameters.Path, "\"C:\\\\Tools\\\\app.exe\""), (WindowsActionParameters.Arguments, "\"--quiet\"")],
        [WindowsActionNames.UrlOpen] = [(WindowsActionParameters.Url, "\"https://example.com/\"")],
        [WindowsActionNames.ProcessKill] = [(WindowsActionParameters.Name, "\"notepad.exe\"")],
    };

    public static TheoryData<string> AllActions()
    {
        var data = new TheoryData<string>();
        foreach (BuiltinActionDescriptor action in WindowsActionCatalog.All)
        {
            data.Add(action.Name);
        }

        return data;
    }

    public static TheoryData<string> ActionsWithRequiredParameters()
    {
        var data = new TheoryData<string>();
        foreach (BuiltinActionDescriptor action in WindowsActionCatalog.All.Where(candidate => candidate.Parameters.Any(parameter => parameter.Required)))
        {
            data.Add(action.Name);
        }

        return data;
    }

    private static BuiltinActionDescriptor Descriptor(string name)
    {
        return WindowsActionCatalog.Find(name) ?? throw new InvalidOperationException("No such action: " + name);
    }

    private static BindResult Bind(string action, params (string Name, JsonElement Value)[] values)
    {
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string name, JsonElement value) in values)
        {
            supplied[name] = value;
        }

        return VariableBinder.Bind(Descriptor(action).Parameters, supplied);
    }

    private static BindResult BindText(string action, string parameter, string value)
    {
        return Bind(action, (parameter, JsonSerializer.SerializeToElement(value)));
    }

    [Fact]
    public void Every_action_has_an_example_input()
    {
        foreach (BuiltinActionDescriptor action in WindowsActionCatalog.All)
        {
            Assert.Contains(action.Name, ValidInputs.Keys);
        }

        Assert.Equal(WindowsActionCatalog.All.Count, ValidInputs.Count);
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void An_action_accepts_its_valid_input(string action)
    {
        (string Name, string Json)[] input = ValidInputs[action];

        BindResult result = VariableBinder.Bind(Descriptor(action).Parameters, ActionTestData.Supplied(input));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Path + ": " + error.Message)));
        foreach ((string name, _) in input)
        {
            Assert.Contains(name, result.Values.Keys);
        }
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void An_action_refuses_a_variable_it_does_not_declare(string action)
    {
        BindResult result = Bind(action, ("NotDeclared", ActionTestData.Json("1")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == ValidationCodes.UnknownVariable);
    }

    [Theory]
    [MemberData(nameof(ActionsWithRequiredParameters))]
    public void An_action_with_required_parameters_refuses_to_run_without_them(string action)
    {
        int required = Descriptor(action).Parameters.Count(parameter => parameter.Required);

        BindResult result = VariableBinder.Bind(Descriptor(action).Parameters, null);

        Assert.False(result.IsValid);
        Assert.Equal(required, result.Errors.Count(error => error.Code == ValidationCodes.Required));
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void An_action_refuses_a_value_of_the_wrong_kind_for_every_parameter(string action)
    {
        foreach (ParameterDefinition parameter in Descriptor(action).Parameters)
        {
            BindResult result = Bind(action, (parameter.Name, ActionTestData.Json("{}")));

            Assert.False(result.IsValid, $"{action}.{parameter.Name} accepted a JSON object.");
        }
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void An_action_without_variables_is_valid_exactly_when_it_has_no_required_parameter(string action)
    {
        bool anyRequired = Descriptor(action).Parameters.Any(parameter => parameter.Required);

        BindResult result = VariableBinder.Bind(Descriptor(action).Parameters, null);

        Assert.Equal(!anyRequired, result.IsValid);
    }

    [Fact]
    public void Force_defaults_to_off_and_mute_defaults_to_on()
    {
        BindResult shutdown = VariableBinder.Bind(Descriptor(WindowsActionNames.Shutdown).Parameters, null);
        BindResult mute = VariableBinder.Bind(Descriptor(WindowsActionNames.SetMute).Parameters, null);

        Assert.True(shutdown.IsValid);
        Assert.False(BuiltinActionVariables.GetBool(shutdown.Values, WindowsActionParameters.Force, fallback: true));
        Assert.True(mute.IsValid);
        Assert.True(BuiltinActionVariables.GetBool(mute.Values, WindowsActionParameters.Muted, fallback: false));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("100")]
    [InlineData("42")]
    public void The_volume_accepts_whole_numbers_from_0_to_100(string level)
    {
        BindResult result = Bind(WindowsActionNames.SetVolume, (WindowsActionParameters.Level, ActionTestData.Json(level)));

        Assert.True(result.IsValid);
        Assert.Equal(long.Parse(level, System.Globalization.CultureInfo.InvariantCulture), BuiltinActionVariables.GetLong(result.Values, WindowsActionParameters.Level));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1000")]
    [InlineData("\"loud\"")]
    public void The_volume_refuses_values_outside_the_range_or_of_the_wrong_kind(string level)
    {
        BindResult result = Bind(WindowsActionNames.SetVolume, (WindowsActionParameters.Level, ActionTestData.Json(level)));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("play_pause")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("stop")]
    public void The_media_key_accepts_each_listed_key(string key)
    {
        BindResult result = BindText(WindowsActionNames.MediaKey, WindowsActionParameters.Key, key);

        Assert.True(result.IsValid);
        Assert.Equal(key, BuiltinActionVariables.GetString(result.Values, WindowsActionParameters.Key));
    }

    [Theory]
    [InlineData("rewind")]
    [InlineData("volume_up")]
    [InlineData("next; calc")]
    public void The_media_key_refuses_everything_else(string key)
    {
        BindResult result = BindText(WindowsActionNames.MediaKey, WindowsActionParameters.Key, key);

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("My Program")]
    public void A_process_name_is_accepted_when_it_is_a_plain_image_name(string name)
    {
        BindResult result = BindText(WindowsActionNames.ProcessKill, WindowsActionParameters.Name, name);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("a/b")]
    [InlineData("note*")]
    [InlineData("note?")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("a\tb")]
    public void A_process_name_with_a_path_a_wildcard_or_control_character_is_refused(string name)
    {
        BindResult result = BindText(WindowsActionNames.ProcessKill, WindowsActionParameters.Name, name);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Text_values_are_limited_in_length()
    {
        Assert.False(BindText(WindowsActionNames.ProcessKill, WindowsActionParameters.Name, new string('a', WindowsActionCatalog.MaxProcessNameLength + 1)).IsValid);
        Assert.False(BindText(WindowsActionNames.AppLaunch, WindowsActionParameters.Path, new string('p', WindowsActionCatalog.MaxPathLength + 1)).IsValid);
        Assert.False(BindText(WindowsActionNames.UrlOpen, WindowsActionParameters.Url, "https://example.com/" + new string('u', WindowsActionCatalog.MaxUrlLength)).IsValid);
    }

    [Fact]
    public void Launching_a_program_needs_a_path_but_not_arguments()
    {
        BindResult withoutPath = Bind(WindowsActionNames.AppLaunch, (WindowsActionParameters.Arguments, JsonSerializer.SerializeToElement("--x")));
        BindResult withoutArguments = BindText(WindowsActionNames.AppLaunch, WindowsActionParameters.Path, "C:\\Tools\\app.exe");

        Assert.False(withoutPath.IsValid);
        Assert.Contains(withoutPath.Errors, error => error.Code == ValidationCodes.Required);
        Assert.True(withoutArguments.IsValid);
    }

    [Fact]
    public void A_value_with_shell_syntax_is_bound_as_plain_text_and_never_interpreted()
    {
        // D-012: the binder hands the text over unchanged; it is never spliced into a command line.
        const string Arguments = "--name \"a b\" & del *.* | more";

        BindResult both = Bind(
            WindowsActionNames.AppLaunch,
            (WindowsActionParameters.Path, JsonSerializer.SerializeToElement("C:\\Tools\\app.exe")),
            (WindowsActionParameters.Arguments, JsonSerializer.SerializeToElement(Arguments)));
        Assert.True(both.IsValid);
        Assert.Equal(Arguments, BuiltinActionVariables.GetString(both.Values, WindowsActionParameters.Arguments));
    }
}
