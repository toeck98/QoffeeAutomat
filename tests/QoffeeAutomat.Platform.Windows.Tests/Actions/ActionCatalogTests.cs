using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.SessionHost.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// The list of built-in actions (PLAN.md S11, T306): which actions exist, where each one runs, and that the agent and the
/// SessionHost describe the actions of the desktop in exactly the same way (the SessionHost cannot reference the Windows
/// platform, so the descriptors are written twice and this test keeps them equal).
/// </summary>
[Trait("Category", "Actions")]
public sealed class ActionCatalogTests
{
    private static readonly string[] PowerActions =
    [
        WindowsActionNames.Shutdown,
        WindowsActionNames.Restart,
        WindowsActionNames.Sleep,
        WindowsActionNames.Hibernate,
    ];

    private static readonly string[] DesktopActions =
    [
        WindowsActionNames.LockSession,
        WindowsActionNames.Logoff,
        WindowsActionNames.DisplayOff,
        WindowsActionNames.SetVolume,
        WindowsActionNames.SetMute,
        WindowsActionNames.MediaKey,
        WindowsActionNames.AppLaunch,
        WindowsActionNames.UrlOpen,
        WindowsActionNames.ProcessKill,
    ];

    private static UserBuiltinActions NewUserActions()
    {
        return new UserBuiltinActions(new FakeDesktopNativeApi(), new FakeAudioEndpoint(), new FakeProcessControl());
    }

    [Fact]
    public void The_catalog_lists_exactly_the_v1_actions_of_the_plan()
    {
        string[] expected = [.. PowerActions, .. DesktopActions];

        Assert.Equal(expected.Order(StringComparer.Ordinal), WindowsActionCatalog.All.Select(action => action.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Action_names_are_unique()
    {
        string[] names = [.. WindowsActionCatalog.All.Select(action => action.Name)];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_power_actions_need_the_system_context_and_everything_else_the_user_context()
    {
        foreach (BuiltinActionDescriptor action in WindowsActionCatalog.All)
        {
            string expected = PowerActions.Contains(action.Name) ? RunAsValues.System : RunAsValues.User;
            Assert.True(expected == action.RunAs, $"{action.Name} declares runAs '{action.RunAs}', expected '{expected}'.");
        }

        Assert.Equal(PowerActions.Length, WindowsActionCatalog.SystemActions.Count);
        Assert.Equal(DesktopActions.Length, WindowsActionCatalog.UserActions.Count);
    }

    [Fact]
    public void Every_action_has_a_title_and_a_description()
    {
        foreach (BuiltinActionDescriptor action in WindowsActionCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(action.Title), action.Name + " has no title.");
            Assert.False(string.IsNullOrWhiteSpace(action.Description), action.Name + " has no description.");
        }
    }

    [Fact]
    public void The_parameters_follow_the_plan()
    {
        ParameterDefinition level = Single(WindowsActionNames.SetVolume);
        Assert.Equal(ParameterTypes.WholeNumber, level.Type);
        Assert.True(level.Required);
        Assert.Equal(0d, level.Min);
        Assert.Equal(100d, level.Max);

        ParameterDefinition key = Single(WindowsActionNames.MediaKey);
        Assert.Equal(ParameterTypes.Choice, key.Type);
        Assert.True(key.Required);
        string[] keys = ["play_pause", "next", "previous", "stop"];
        Assert.Equal(keys, key.Options);

        BuiltinActionDescriptor launch = WindowsActionCatalog.Find(WindowsActionNames.AppLaunch)!;
        string[] launchParameters = [WindowsActionParameters.Path, WindowsActionParameters.Arguments];
        Assert.Equal(launchParameters, launch.Parameters.Select(parameter => parameter.Name));
        Assert.True(launch.Parameters[0].Required);
        Assert.False(launch.Parameters[1].Required);

        ParameterDefinition name = Single(WindowsActionNames.ProcessKill);
        Assert.Equal(ParameterTypes.Text, name.Type);
        Assert.True(name.Required);
    }

    [Fact]
    public void The_destructive_actions_that_can_force_offer_a_flag_that_defaults_to_off()
    {
        foreach (string action in new[] { WindowsActionNames.Shutdown, WindowsActionNames.Restart, WindowsActionNames.Logoff })
        {
            ParameterDefinition force = Single(action, WindowsActionParameters.Force);

            Assert.Equal(ParameterTypes.Flag, force.Type);
            Assert.False(force.Required);
            Assert.NotNull(force.Default);
            Assert.False(force.Default!.Value.GetBoolean());
        }
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_name_and_does_not_guess_the_case()
    {
        Assert.Null(WindowsActionCatalog.Find("power.explode"));
        Assert.Null(WindowsActionCatalog.Find("POWER.SHUTDOWN"));
        Assert.NotNull(WindowsActionCatalog.Find(WindowsActionNames.Shutdown));
    }

    [Fact]
    public void The_agent_lists_every_action_but_executes_only_the_power_actions()
    {
        var provider = new WindowsBuiltinActions(new FakePowerNativeApi());

        Assert.Equal(WindowsActionCatalog.All.Count, provider.Actions.Count);
    }

    [Fact]
    public void The_session_host_offers_the_actions_of_the_desktop_only()
    {
        UserBuiltinActions provider = NewUserActions();

        Assert.Equal(
            DesktopActions.Order(StringComparer.Ordinal),
            provider.Actions.Select(action => action.Name).Order(StringComparer.Ordinal));
        Assert.All(provider.Actions, action => Assert.Equal(RunAsValues.User, action.RunAs));
    }

    [Fact]
    public void The_session_host_describes_its_actions_exactly_like_the_agent()
    {
        UserBuiltinActions provider = NewUserActions();

        foreach (BuiltinActionDescriptor expected in WindowsActionCatalog.UserActions)
        {
            BuiltinActionDescriptor? actual = provider.Actions.FirstOrDefault(action => action.Name == expected.Name);

            Assert.NotNull(actual);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.RunAs, actual.RunAs);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(
                expected.Parameters.Select(Describe),
                actual.Parameters.Select(Describe));
        }
    }

    private static ParameterDefinition Single(string action, string? parameter = null)
    {
        BuiltinActionDescriptor descriptor = WindowsActionCatalog.Find(action)!;
        return parameter is null ? Assert.Single(descriptor.Parameters) : descriptor.Parameters.Single(candidate => candidate.Name == parameter);
    }

    /// <summary>A comparable text of a parameter: <see cref="System.Text.Json.JsonElement"/> has no value equality.</summary>
    private static string Describe(ParameterDefinition parameter)
    {
        return string.Join(
            "|",
            parameter.Name,
            parameter.Type,
            parameter.Required,
            parameter.Default?.GetRawText(),
            parameter.Min,
            parameter.Max,
            parameter.Step,
            parameter.Pattern,
            parameter.MaxLength,
            parameter.Options is null ? null : string.Join(",", parameter.Options),
            parameter.OptionsFrom,
            parameter.Secret,
            parameter.Unit,
            parameter.Description);
    }
}
