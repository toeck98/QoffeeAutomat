using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Tests.Manifest;

/// <summary>
/// The features <c>notify</c> and <c>media</c> and the system commands behind them (PLAN.md S07, S10, S11, T702, T716, D-038,
/// D-044): the agent announces a feature only when its platform has the built-in action, and the commands that the Home Assistant
/// integration runs for the notify entity and the media player are announced with the feature and with nothing else.
/// </summary>
[Trait("Category", "Manifest")]
public sealed class FeatureCommandTests
{
    private sealed class ActionList : IBuiltinActionProvider
    {
        private readonly BuiltinActionDescriptor[] _actions;

        public ActionList(params string[] names)
        {
            _actions = [.. names.Select(name => new BuiltinActionDescriptor(name, name, RunAsValues.User, []))];
        }

        public IReadOnlyList<BuiltinActionDescriptor> Actions => _actions;

        public ValueTask<ExecutionResult> ExecuteAsync(string name, IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ExecutionResult(0, string.Empty, string.Empty));
        }
    }

    private static string[] Ids(IEnumerable<CommandDocument> commands) => [.. commands.Select(command => command.Id)];

    // ------------------------------------------------------------------
    // The features
    // ------------------------------------------------------------------

    [Fact]
    public void A_platform_without_the_actions_announces_no_feature()
    {
        Assert.Empty(AgentFeatures.FromActions(new ActionList("power.shutdown", "audio.set_volume", "media.key")));
    }

    [Fact]
    public void The_notify_action_announces_the_feature_notify()
    {
        Assert.Equal([AgentFeatures.Notify], AgentFeatures.FromActions(new ActionList("power.shutdown", "notify.toast")));
    }

    [Fact]
    public void The_media_action_announces_the_feature_media_and_not_the_media_key_alone()
    {
        Assert.Equal([AgentFeatures.Media], AgentFeatures.FromActions(new ActionList("media.control")));
        Assert.Empty(AgentFeatures.FromActions(new ActionList("media.key")));
    }

    [Fact]
    public void Both_actions_announce_both_features()
    {
        string[] features = [.. AgentFeatures.FromActions(new ActionList("notify.toast", "media.control"))];

        Assert.Equal(["media", "notify"], features.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_names_of_the_features_are_the_ones_of_the_plan()
    {
        Assert.Equal("notify", AgentFeatures.Notify);
        Assert.Equal("media", AgentFeatures.Media);
        Assert.Equal("notify.toast", NotifyToastSpec.ActionName);
        Assert.Equal("media.control", MediaControlSpec.ActionName);
    }

    // ------------------------------------------------------------------
    // The system commands
    // ------------------------------------------------------------------

    [Fact]
    public void Without_a_feature_only_the_maintenance_switch_is_announced()
    {
        Assert.Equal([SystemCommands.MaintenanceId], Ids(SystemCommands.Announced(null)));
        Assert.Equal([SystemCommands.MaintenanceId], Ids(SystemCommands.Announced([])));
        Assert.Equal([SystemCommands.MaintenanceId], Ids(SystemCommands.Announced(["wol"])));
    }

    [Fact]
    public void The_feature_notify_brings_the_notify_command()
    {
        Assert.Equal([SystemCommands.MaintenanceId, SystemCommands.NotifyId], Ids(SystemCommands.Announced([AgentFeatures.Notify])));
    }

    [Fact]
    public void The_feature_media_brings_the_control_volume_and_mute_commands()
    {
        Assert.Equal(
            [SystemCommands.MaintenanceId, SystemCommands.MediaControlId, SystemCommands.MediaVolumeId, SystemCommands.MediaMuteId],
            Ids(SystemCommands.Announced([AgentFeatures.Media])));
    }

    [Fact]
    public void Every_system_command_that_needs_a_feature_says_which()
    {
        Assert.Null(SystemCommands.RequiredFeature(SystemCommands.Maintenance));
        Assert.Equal(AgentFeatures.Notify, SystemCommands.RequiredFeature(SystemCommands.Notify));
        Assert.Equal(AgentFeatures.Media, SystemCommands.RequiredFeature(SystemCommands.MediaControl));
        Assert.Equal(AgentFeatures.Media, SystemCommands.RequiredFeature(SystemCommands.MediaVolume));
        Assert.Equal(AgentFeatures.Media, SystemCommands.RequiredFeature(SystemCommands.MediaMute));
        Assert.Equal(AgentFeatures.Update, SystemCommands.RequiredFeature(SystemCommands.AgentUpdate));
        Assert.Equal(
            SystemCommands.All.Count(command => SystemCommands.RequiredFeature(command) is not null),
            SystemCommands.Announced([AgentFeatures.Notify, AgentFeatures.Media, AgentFeatures.Update]).Count - 1);
    }

    [Fact]
    public void The_feature_update_brings_the_update_command()
    {
        Assert.Equal([SystemCommands.MaintenanceId, SystemCommands.AgentUpdateId], Ids(SystemCommands.Announced([AgentFeatures.Update])));
        Assert.Equal(AgentUpdateSpec.ActionName, Assert.IsType<BuiltinAction>(SystemCommands.AgentUpdate.Action).Name);
        Assert.Equal(RunAsValues.System, SystemCommands.AgentUpdate.RunAs);
        Assert.Null(SystemCommands.AgentUpdate.HaControl);
    }

    [Theory]
    [InlineData("agent_maintenance")]
    [InlineData("agent_update")]
    [InlineData("agent_notify")]
    [InlineData("agent_media_control")]
    [InlineData("agent_media_volume")]
    [InlineData("agent_media_mute")]
    public void The_ids_of_the_system_commands_are_reserved(string id)
    {
        Assert.NotNull(SystemCommands.TryGet(id));
        Assert.True(SystemCommands.IsReservedId(id));

        ValidationResult result = DocumentValidator.ValidateCommand(ManifestTestSupport.Command(id));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_id_that_is_no_system_command_is_not_found()
    {
        Assert.Null(SystemCommands.TryGet(null));
        Assert.Null(SystemCommands.TryGet("agent_unknown"));
        Assert.Null(SystemCommands.TryGet("AGENT_NOTIFY"));
        Assert.False(SystemCommands.IsReservedId("notify"));
    }

    [Fact]
    public void The_commands_run_the_actions_that_the_platform_provides_as_the_user()
    {
        Assert.Equal(NotifyToastSpec.ActionName, Assert.IsType<BuiltinAction>(SystemCommands.Notify.Action).Name);
        Assert.Equal(MediaControlSpec.ActionName, Assert.IsType<BuiltinAction>(SystemCommands.MediaControl.Action).Name);
        Assert.Equal("audio.set_volume", Assert.IsType<BuiltinAction>(SystemCommands.MediaVolume.Action).Name);
        Assert.Equal("audio.set_mute", Assert.IsType<BuiltinAction>(SystemCommands.MediaMute.Action).Name);

        CommandDocument[] featureCommands = [SystemCommands.Notify, SystemCommands.MediaControl, SystemCommands.MediaVolume, SystemCommands.MediaMute];
        Assert.All(
            featureCommands,
            command =>
            {
                Assert.Equal(RunAsValues.User, command.RunAs);
                Assert.True(command.ExposeToHa);
                Assert.True(command.Enabled);
                Assert.Null(command.HaControl);
            });
    }

    [Fact]
    public void The_variables_of_the_commands_are_the_parameters_of_the_actions()
    {
        CommandDescriptor notify = ManifestBuilder.Describe(SystemCommands.Notify);
        CommandDescriptor control = ManifestBuilder.Describe(SystemCommands.MediaControl);
        CommandDescriptor volume = ManifestBuilder.Describe(SystemCommands.MediaVolume);
        CommandDescriptor mute = ManifestBuilder.Describe(SystemCommands.MediaMute);

        Assert.Equal(NotifyToastSpec.Parameters.Select(parameter => parameter.Name), notify.Vars!.Select(variable => variable.Name));
        Assert.Null(notify.Control);

        VariableDescriptor action = Assert.Single(control.Vars!);
        Assert.Equal("Action", action.Name);
        Assert.Equal(ParameterTypes.Choice, action.Type);
        Assert.True(action.Required);
        Assert.Equal(["play", "pause", "play_pause", "next", "previous", "stop"], action.Options);

        VariableDescriptor level = Assert.Single(volume.Vars!);
        Assert.Equal("Level", level.Name);
        Assert.Equal(ParameterTypes.WholeNumber, level.Type);
        Assert.True(level.Required);
        Assert.Equal(0d, level.Min);
        Assert.Equal(100d, level.Max);

        VariableDescriptor muted = Assert.Single(mute.Vars!);
        Assert.Equal("Muted", muted.Name);
        Assert.Equal(ParameterTypes.Flag, muted.Type);
        Assert.True(muted.Required);
    }

    [Fact]
    public void A_manifest_with_both_features_is_valid_against_the_schema_and_lists_the_features()
    {
        CommandDocument[] commands = [.. SystemCommands.Announced([AgentFeatures.Notify, AgentFeatures.Media])];
        LinkManifest manifest = ManifestBuilder.Build(
            ManifestTestSupport.Agent(),
            [ManifestTestSupport.Metric("test.value")],
            commands,
            EventCatalog.Types,
            [AgentFeatures.Notify, AgentFeatures.Media]);

        Assert.Equal(["media", "notify"], manifest.Features);
        Assert.Empty(ManifestTestSupport.SchemaErrors(manifest));
        Assert.Contains(manifest.Commands, command => command.Id == SystemCommands.NotifyId);
        Assert.Contains(manifest.Events!, entry => entry.Type == EventTypes.NotificationAction);
    }

    [Fact]
    public void The_notification_action_event_is_part_of_the_catalog()
    {
        Assert.True(EventCatalog.Contains("notification_action"));
        Assert.Equal("notification_action", EventTypes.NotificationAction);
    }

    // ------------------------------------------------------------------
    // The manifest service
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_service_announces_the_commands_of_the_features_it_is_given()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        IReadOnlyList<string>? features = null;
        using var service = new ManifestService(
            harness.Registry,
            harness.Engine,
            () => ManifestTestSupport.Agent(),
            () => features,
            includeSystemCommands: true);

        string[] without = [.. service.Current.Commands.Select(command => command.Id)];
        features = [AgentFeatures.Notify];
        LinkManifest withNotify = service.Refresh();
        features = [AgentFeatures.Notify, AgentFeatures.Media];
        LinkManifest withBoth = service.Refresh();

        Assert.Equal([SystemCommands.MaintenanceId], without);
        Assert.Equal([SystemCommands.NotifyId], withNotify.Commands.Select(command => command.Id).Where(id => id != SystemCommands.MaintenanceId));
        Assert.Equal(["notify"], withNotify.Features);
        Assert.Equal(["media", "notify"], withBoth.Features);
        Assert.Equal(
            [SystemCommands.MaintenanceId, SystemCommands.MediaControlId, SystemCommands.MediaMuteId, SystemCommands.MediaVolumeId, SystemCommands.NotifyId],
            withBoth.Commands.Select(command => command.Id).Order(StringComparer.Ordinal));
        Assert.NotEqual(without.Length, withBoth.Commands.Count);
    }

    [Fact]
    public async Task A_change_of_the_features_changes_the_revision()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();
        IReadOnlyList<string>? features = null;
        using var service = new ManifestService(
            harness.Registry,
            harness.Engine,
            () => ManifestTestSupport.Agent(),
            () => features,
            includeSystemCommands: true);

        string before = service.Rev;
        features = [AgentFeatures.Media];
        string after = service.Rev;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task The_registry_finds_the_system_commands_of_the_features()
    {
        await using ManifestHarness harness = await ManifestHarness.CreateAsync();

        Assert.Same(SystemCommands.Notify, harness.Registry.TryGet(SystemCommands.NotifyId));
        Assert.Same(SystemCommands.MediaControl, harness.Registry.TryGet(SystemCommands.MediaControlId));
    }
}
