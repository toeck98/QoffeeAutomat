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
        [WindowsActionNames.Restart] = [(WindowsActionParameters.Force, "true"), (PowerActionSpec.DelaySeconds, "300")],
        [WindowsActionNames.Sleep] = [(PowerActionSpec.DelaySeconds, "60"), (PowerActionSpec.Message, "\"Going to sleep\"")],
        [WindowsActionNames.Hibernate] = [],
        [WindowsActionNames.KeepAwake] = [(PowerActionSpec.On, "true"), (PowerActionSpec.Display, "true"), (PowerActionSpec.Minutes, "90")],
        [WindowsActionNames.CancelPending] = [],
        [WindowsActionNames.LockSession] = [],
        [WindowsActionNames.Logoff] = [(WindowsActionParameters.Force, "false"), (PowerActionSpec.DelaySeconds, "3600")],
        [WindowsActionNames.DisplayOff] = [],
        [WindowsActionNames.SetVolume] = [(WindowsActionParameters.Level, "35")],
        [WindowsActionNames.SetMute] = [(WindowsActionParameters.Muted, "false")],
        [WindowsActionNames.MediaKey] = [(WindowsActionParameters.Key, "\"next\"")],
        [WindowsActionNames.AppLaunch] = [(WindowsActionParameters.Path, "\"C:\\\\Tools\\\\app.exe\""), (WindowsActionParameters.Arguments, "\"--quiet\"")],
        [WindowsActionNames.UrlOpen] = [(WindowsActionParameters.Url, "\"https://example.com/\"")],
        [WindowsActionNames.ProcessKill] = [(WindowsActionParameters.Name, "\"notepad.exe\"")],
        [WindowsActionNames.NotifyToast] =
        [
            (NotifyToastSpec.Title, "\"Doorbell\""),
            (NotifyToastSpec.Message, "\"Somebody is at the door\""),
            (NotifyToastSpec.ImageUrl, "\"http://homeassistant.local:8123/local/door.png\""),
            (NotifyToastSpec.Tag, "\"door\""),
            ("Action1Id", "\"open\""),
            ("Action1Title", "\"Open the door\""),
            ("Action2Id", "\"ignore\""),
            ("Action2Title", "\"Ignore\""),
        ],
        [WindowsActionNames.MediaControl] = [(MediaControlSpec.ActionParameter, "\"play_pause\"")],
        [WindowsActionNames.SetDefaultOutput] = [(AudioDeviceSpec.Device, "\"Speakers (Realtek Audio)\"")],
        [WindowsActionNames.SetDefaultInput] = [(AudioDeviceSpec.Device, "\"Headset Microphone\"")],
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
    [InlineData(WindowsActionNames.Shutdown)]
    [InlineData(WindowsActionNames.Restart)]
    [InlineData(WindowsActionNames.Sleep)]
    [InlineData(WindowsActionNames.Hibernate)]
    [InlineData(WindowsActionNames.Logoff)]
    public void The_delay_of_a_power_action_accepts_whole_seconds_from_0_to_3600_and_defaults_to_0(string action)
    {
        BindResult none = VariableBinder.Bind(Descriptor(action).Parameters, null);
        BindResult zero = Bind(action, (PowerActionSpec.DelaySeconds, ActionTestData.Json("0")));
        BindResult longest = Bind(action, (PowerActionSpec.DelaySeconds, ActionTestData.Json("3600")));

        Assert.True(none.IsValid);
        Assert.Equal(0L, BuiltinActionVariables.GetLong(none.Values, PowerActionSpec.DelaySeconds));
        Assert.True(zero.IsValid);
        Assert.True(longest.IsValid);
        Assert.Equal(3600L, BuiltinActionVariables.GetLong(longest.Values, PowerActionSpec.DelaySeconds));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("1.5")]
    [InlineData("\"soon\"")]
    public void The_delay_of_a_power_action_refuses_values_outside_the_range_or_of_the_wrong_kind(string delay)
    {
        BindResult result = Bind(WindowsActionNames.Shutdown, (PowerActionSpec.DelaySeconds, ActionTestData.Json(delay)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void The_message_of_a_power_action_is_limited_to_256_characters()
    {
        BindResult fits = BindText(WindowsActionNames.Restart, PowerActionSpec.Message, new string('m', 256));
        BindResult tooLong = BindText(WindowsActionNames.Restart, PowerActionSpec.Message, new string('m', 257));

        Assert.True(fits.IsValid);
        Assert.False(tooLong.IsValid);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("10081")]
    [InlineData("2.5")]
    public void Keep_awake_refuses_minutes_outside_the_range(string minutes)
    {
        BindResult result = Bind(WindowsActionNames.KeepAwake, (PowerActionSpec.Minutes, ActionTestData.Json(minutes)));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Keep_awake_defaults_to_on_without_the_display_and_without_a_time()
    {
        BindResult result = VariableBinder.Bind(Descriptor(WindowsActionNames.KeepAwake).Parameters, null);

        Assert.True(result.IsValid);
        Assert.True(BuiltinActionVariables.GetBool(result.Values, PowerActionSpec.On, fallback: false));
        Assert.False(BuiltinActionVariables.GetBool(result.Values, PowerActionSpec.Display, fallback: true));
        Assert.Equal(0L, BuiltinActionVariables.GetLong(result.Values, PowerActionSpec.Minutes));
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
