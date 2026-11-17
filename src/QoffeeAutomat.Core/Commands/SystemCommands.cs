using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Core.Commands;

/// <summary>The audio actions that the media player commands run, and their parameters (the actions themselves belong to the platform, S11).</summary>
internal static class MediaAudioActions
{
    /// <summary>The name of <c>audio.set_volume</c>.</summary>
    public const string SetVolume = "audio.set_volume";

    /// <summary>The name of its parameter.</summary>
    public const string LevelParameter = "Level";

    /// <summary>The name of <c>audio.set_mute</c>.</summary>
    public const string SetMute = "audio.set_mute";

    /// <summary>The name of its parameter.</summary>
    public const string MutedParameter = "Muted";
}

/// <summary>
/// Commands that the agent provides itself and that exist whatever the configuration says (PLAN.md S06, T705). They are not
/// documents in the config store: the <see cref="CommandRegistry"/> finds them by id, the manifest announces them, and
/// the <see cref="CommandEngine"/> executes them without an action handler. They cannot be edited, disabled or deleted, and
/// their ids are reserved (a stored command cannot use them).
/// </summary>
public static class SystemCommands
{
    /// <summary>The id of the maintenance mode switch.</summary>
    public const string MaintenanceId = "agent_maintenance";

    /// <summary>The name of its boolean variable: true switches the maintenance mode on (the agent is paused).</summary>
    public const string MaintenanceVariable = "Enabled";

    /// <summary>
    /// The maintenance mode switch: a command with one boolean variable and a Home Assistant <c>switch</c> control whose
    /// state is the metric <see cref="AgentMetricIds.Paused"/>. It is the only command that runs while the agent is paused,
    /// otherwise nobody could switch the maintenance mode off again.
    /// </summary>
    public static CommandDocument Maintenance { get; } = new(
        DocumentSchema.CommandVersion,
        MaintenanceId,
        "Maintenance mode",
        Enabled: true,
        ExposeToHa: true,
        RunAsValues.System,
        new BuiltinAction("agent.maintenance"),
        Description: "While the maintenance mode is on, the agent rejects every command with the error paused.",
        Icon: "mdi:wrench",
        Parameters:
        [
            new ParameterDefinition(
                MaintenanceVariable,
                ParameterTypes.Flag,
                Required: true,
                Description: "True switches the maintenance mode on, false switches it off."),
        ],
        HaControl: new HaControl(HaControlTypes.Switch, MaintenanceVariable, AgentMetricIds.Paused),
        TimeoutSeconds: 10,
        Concurrency: ConcurrencyModes.Exclusive,
        CooldownSeconds: 0,
        CaptureOutput: false,
        Origin: DocumentOrigins.Local);

    /// <summary>The id of the command that shows a notification on the desktop (T702).</summary>
    public const string NotifyId = "agent_notify";

    /// <summary>The id of the command that controls the current media session (T716).</summary>
    public const string MediaControlId = "agent_media_control";

    /// <summary>The id of the command that sets the master volume for the media player of Home Assistant (T716).</summary>
    public const string MediaVolumeId = "agent_media_volume";

    /// <summary>The id of the command that mutes or unmutes the sound for the media player of Home Assistant (T716).</summary>
    public const string MediaMuteId = "agent_media_mute";

    /// <summary>
    /// Shows a toast notification (<c>notify.toast</c>, T702, D-044). It has no Home Assistant control: the notify entity and the
    /// action <c>qoffeeautomat.send_notification</c> of the integration run it by id. The manifest announces it only together with
    /// the feature <see cref="AgentFeatures.Notify"/>.
    /// </summary>
    public static CommandDocument Notify { get; } = new(
        DocumentSchema.CommandVersion,
        NotifyId,
        "Show notification",
        Enabled: true,
        ExposeToHa: true,
        RunAsValues.User,
        new BuiltinAction(NotifyToastSpec.ActionName),
        Description: "Shows a notification on the desktop of the logged-in user, with up to three buttons that raise the event notification_action.",
        Icon: "mdi:message-badge",
        Parameters: NotifyToastSpec.Parameters,
        HaControl: null,
        TimeoutSeconds: 30,
        Concurrency: ConcurrencyModes.Parallel,
        CooldownSeconds: 0,
        CaptureOutput: false,
        Origin: DocumentOrigins.Local);

    /// <summary>
    /// Controls the current media session (<c>media.control</c>, T716, D-038): the Home Assistant media player runs it for play,
    /// pause, next, previous and stop. Announced only together with the feature <see cref="AgentFeatures.Media"/>.
    /// </summary>
    public static CommandDocument MediaControl { get; } = new(
        DocumentSchema.CommandVersion,
        MediaControlId,
        "Control media playback",
        Enabled: true,
        ExposeToHa: true,
        RunAsValues.User,
        new BuiltinAction(MediaControlSpec.ActionName),
        Description: "Sends play, pause, next, previous or stop to the current media session of the logged-in user.",
        Icon: "mdi:play-pause",
        Parameters: MediaControlSpec.Parameters,
        HaControl: null,
        TimeoutSeconds: 10,
        Concurrency: ConcurrencyModes.Queue,
        CooldownSeconds: 0,
        CaptureOutput: false,
        Origin: DocumentOrigins.Local);

    /// <summary>The master volume for the media player (<c>audio.set_volume</c>); announced together with the feature <see cref="AgentFeatures.Media"/>.</summary>
    public static CommandDocument MediaVolume { get; } = new(
        DocumentSchema.CommandVersion,
        MediaVolumeId,
        "Set volume",
        Enabled: true,
        ExposeToHa: true,
        RunAsValues.User,
        new BuiltinAction(MediaAudioActions.SetVolume),
        Description: "Sets the master volume of the default playback device, in percent.",
        Icon: "mdi:volume-high",
        Parameters:
        [
            new ParameterDefinition(
                MediaAudioActions.LevelParameter,
                ParameterTypes.WholeNumber,
                Required: true,
                Min: 0,
                Max: 100,
                Step: 1,
                Unit: "%",
                Description: "The master volume of the default playback device."),
        ],
        HaControl: null,
        TimeoutSeconds: 10,
        Concurrency: ConcurrencyModes.Restart,
        CooldownSeconds: 0,
        CaptureOutput: false,
        Origin: DocumentOrigins.Local);

    /// <summary>The mute switch for the media player (<c>audio.set_mute</c>); announced together with the feature <see cref="AgentFeatures.Media"/>.</summary>
    public static CommandDocument MediaMute { get; } = new(
        DocumentSchema.CommandVersion,
        MediaMuteId,
        "Mute or unmute",
        Enabled: true,
        ExposeToHa: true,
        RunAsValues.User,
        new BuiltinAction(MediaAudioActions.SetMute),
        Description: "Mutes or unmutes the default playback device.",
        Icon: "mdi:volume-off",
        Parameters:
        [
            new ParameterDefinition(
                MediaAudioActions.MutedParameter,
                ParameterTypes.Flag,
                Required: true,
                Description: "True mutes, false unmutes."),
        ],
        HaControl: null,
        TimeoutSeconds: 10,
        Concurrency: ConcurrencyModes.Restart,
        CooldownSeconds: 0,
        CaptureOutput: false,
        Origin: DocumentOrigins.Local);

    /// <summary>The id of the command that updates the agent (T704).</summary>
    public const string AgentUpdateId = "agent_update";

    /// <summary>
    /// Updates the agent from the newest release (<c>agent.update</c>, T704, D-046): the Home Assistant update entity runs it for
    /// <c>update.install</c>. It runs as the system, because the installer needs administrator rights, and it is announced only
    /// together with the feature <see cref="AgentFeatures.Update"/>. Its timeout covers the download; the installer is started
    /// and left running, so the run ends before the agent restarts.
    /// </summary>
    public static CommandDocument AgentUpdate { get; } = new(
        DocumentSchema.CommandVersion,
        AgentUpdateId,
        AgentUpdateSpec.ActionTitle,
        Enabled: true,
        ExposeToHa: true,
        RunAsValues.System,
        new BuiltinAction(AgentUpdateSpec.ActionName),
        Description: "Downloads the installer of the newest release, verifies it and runs it silently; the agent restarts.",
        Icon: "mdi:update",
        Parameters: AgentUpdateSpec.Parameters,
        HaControl: null,
        TimeoutSeconds: 900,
        Concurrency: ConcurrencyModes.Exclusive,
        CooldownSeconds: 0,
        CaptureOutput: false,
        Origin: DocumentOrigins.Local);

    /// <summary>All system commands.</summary>
    public static IReadOnlyList<CommandDocument> All { get; } = [Maintenance, Notify, MediaControl, MediaVolume, MediaMute, AgentUpdate];

    /// <summary>Finds a system command.</summary>
    /// <param name="id">The command id.</param>
    /// <returns>The command, or null when the id is not a system command.</returns>
    public static CommandDocument? TryGet(string? id)
    {
        if (id is null)
        {
            return null;
        }

        return All.FirstOrDefault(command => string.Equals(command.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// The system commands that an agent with these features announces: the maintenance mode always, and the commands that a
    /// feature needs only when the agent announces that feature (a platform without <c>notify.toast</c> must not offer the
    /// notify command).
    /// </summary>
    /// <param name="features">The features of the agent; null or empty means none.</param>
    /// <returns>The commands, in the order of <see cref="All"/>.</returns>
    public static IReadOnlyList<CommandDocument> Announced(IReadOnlyList<string>? features)
    {
        return [.. All.Where(command => RequiredFeature(command) is not { } required || features?.Contains(required, StringComparer.Ordinal) == true)];
    }

    /// <summary>The feature a system command needs.</summary>
    /// <param name="command">A system command.</param>
    /// <returns>The feature; null when the command needs none.</returns>
    public static string? RequiredFeature(CommandDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command.Id switch
        {
            NotifyId => AgentFeatures.Notify,
            MediaControlId or MediaVolumeId or MediaMuteId => AgentFeatures.Media,
            AgentUpdateId => AgentFeatures.Update,
            _ => null,
        };
    }

    /// <summary>Tells whether an id belongs to a system command, so that a stored command must not use it.</summary>
    /// <param name="id">The id.</param>
    /// <returns>True when the id is reserved.</returns>
    public static bool IsReservedId(string? id)
    {
        return TryGet(id) is not null;
    }

    /// <summary>Tells whether a command is the maintenance switch.</summary>
    /// <param name="command">The command as the engine got it.</param>
    /// <returns>True for <see cref="Maintenance"/> itself.</returns>
    public static bool IsMaintenance(CommandDocument command)
    {
        return ReferenceEquals(command, Maintenance);
    }
}
