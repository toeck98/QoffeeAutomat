using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Linux.Actions;
using QoffeeAutomat.Platform.Windows.Actions;

namespace QoffeeAutomat.Platform.Linux.Tests.Actions;

/// <summary>The built-in actions of Linux: their names and parameters against the Windows catalog, and the default commands (PLAN.md S11, S17, T803).</summary>
[Trait("Category", "Actions")]
public sealed class LinuxActionCatalogTests
{
    private static readonly string[] WindowsOnly = ["power.keep_awake", "audio.set_default_output", "audio.set_default_input"];

    [Fact]
    public void Every_action_of_linux_has_the_name_and_the_parameters_of_the_windows_action_so_that_documents_move_between_the_platforms()
    {
        foreach (BuiltinActionDescriptor linux in LinuxActionCatalog.All)
        {
            BuiltinActionDescriptor? windows = WindowsActionCatalog.Find(linux.Name);

            Assert.NotNull(windows);
            foreach (ParameterDefinition parameter in linux.Parameters)
            {
                ParameterDefinition? counterpart = windows.Parameters.FirstOrDefault(candidate => candidate.Name == parameter.Name);
                Assert.NotNull(counterpart);
                Assert.Equal(counterpart.Type, parameter.Type);
                Assert.Equal(counterpart.Required, parameter.Required);
            }
        }
    }

    [Fact]
    public void The_windows_actions_without_an_equivalent_are_not_offered_so_that_a_command_that_uses_them_is_flagged_as_unsupported()
    {
        foreach (string name in WindowsOnly)
        {
            Assert.NotNull(WindowsActionCatalog.Find(name));
            Assert.Null(LinuxActionCatalog.Find(name));
        }

        var support = new CommandPlatformSupport(PlatformActionSupport.LinuxOs, () => new LinuxBuiltinActions(new ScriptedRunner()));
        CommandDocument keepAwake = Command(new BuiltinAction("power.keep_awake"));

        ValidationError error = Assert.Single(support.Check(keepAwake));

        Assert.Equal("/action/name", error.Path);
        Assert.Equal(ValidationCodes.Unsupported, error.Code);
    }

    [Fact]
    public void The_power_and_session_actions_run_in_the_service_and_the_desktop_actions_in_the_helper()
    {
        string[] system = [.. LinuxActionCatalog.SystemActions.Select(action => action.Name)];
        string[] user = [.. LinuxActionCatalog.UserActions.Select(action => action.Name)];

        Assert.Equal(
            ["power.shutdown", "power.restart", "power.sleep", "power.hibernate", "power.cancel", "session.lock", "session.logoff"],
            system);
        Assert.Equal(
            ["display.off", "audio.set_volume", "audio.set_mute", "media.key", "media.control", "app.launch", "url.open", "process.kill", "notify.toast"],
            user);
        Assert.Equal(LinuxActionCatalog.All.Count, system.Length + user.Length);
        Assert.Equal(LinuxActionCatalog.All.Count, LinuxActionCatalog.All.Select(action => action.Name).Distinct().Count());
    }

    [Fact]
    public void Every_action_validates_as_a_command_with_its_own_parameters()
    {
        foreach (BuiltinActionDescriptor descriptor in LinuxActionCatalog.All)
        {
            CommandDocument command = Command(new BuiltinAction(descriptor.Name)) with
            {
                RunAs = descriptor.RunAs,
                Parameters = descriptor.Parameters.Count == 0 ? null : descriptor.Parameters,
            };

            ValidationResult result = DocumentValidator.ValidateCommand(command);

            Assert.True(result.IsValid, descriptor.Name + ": " + string.Join("; ", result.Errors.Select(error => error.Path + " " + error.Message)));
        }
    }

    [Fact]
    public void The_default_commands_validate_and_are_switched_off_and_not_exposed()
    {
        Assert.NotEmpty(LinuxDefaultCommandSeeder.DefaultCommands);
        foreach (CommandDocument command in LinuxDefaultCommandSeeder.DefaultCommands)
        {
            ValidationResult result = DocumentValidator.ValidateCommand(command);

            Assert.True(result.IsValid, command.Id + ": " + string.Join("; ", result.Errors.Select(error => error.Path + " " + error.Message)));
            Assert.False(command.Enabled);
            Assert.False(command.ExposeToHa);
            Assert.Equal(DocumentOrigins.Local, command.Origin);
            Assert.IsType<BuiltinAction>(command.Action);
            Assert.NotNull(LinuxActionCatalog.Find(((BuiltinAction)command.Action).Name));
        }
    }

    [Fact]
    public void The_default_commands_use_the_run_context_that_their_action_needs()
    {
        foreach (CommandDocument command in LinuxDefaultCommandSeeder.DefaultCommands)
        {
            BuiltinActionDescriptor descriptor = LinuxActionCatalog.Find(((BuiltinAction)command.Action).Name)!;

            Assert.Equal(descriptor.RunAs, command.RunAs);
        }
    }

    [Fact]
    public void A_command_that_can_wait_has_a_timeout_that_is_longer_than_the_longest_delay()
    {
        CommandDocument shutdown = LinuxDefaultCommandSeeder.DefaultCommands.Single(command => command.Id == "power_shutdown");

        Assert.True(shutdown.TimeoutSeconds > PowerActionSpec.MaxDelaySeconds);
        Assert.Contains(shutdown.Parameters!, parameter => parameter.Name == PowerActionSpec.DelaySeconds);
    }

    [Fact]
    public void The_media_key_command_plays_and_pauses_without_a_value()
    {
        CommandDocument media = LinuxDefaultCommandSeeder.DefaultCommands.Single(command => command.Id == "media_play_pause");

        ParameterDefinition key = Assert.Single(media.Parameters!);
        Assert.False(key.Required);
        Assert.Equal("play_pause", key.Default!.Value.GetString());
    }

    private static CommandDocument Command(CommandAction action) => new(
        DocumentSchema.CommandVersion,
        "test_command",
        "Test command",
        true,
        false,
        RunAsValues.System,
        action);
}
