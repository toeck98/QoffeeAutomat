using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Actions;

/// <summary>The names of the built-in actions of Linux (PLAN.md S11, S17, T803); they are the names of the Windows actions, so a command document moves between the platforms.</summary>
public static class LinuxActionNames
{
    /// <summary>Shuts the computer down.</summary>
    public const string Shutdown = "power.shutdown";

    /// <summary>Restarts the computer.</summary>
    public const string Restart = "power.restart";

    /// <summary>Puts the computer to sleep.</summary>
    public const string Sleep = "power.sleep";

    /// <summary>Hibernates the computer.</summary>
    public const string Hibernate = "power.hibernate";

    /// <summary>Cancels the delayed power action that is pending.</summary>
    public const string CancelPending = PowerActionSpec.Cancel;

    /// <summary>Locks the session of the user.</summary>
    public const string LockSession = "session.lock";

    /// <summary>Logs the user off.</summary>
    public const string Logoff = "session.logoff";

    /// <summary>Turns the displays off.</summary>
    public const string DisplayOff = "display.off";

    /// <summary>Sets the volume of the default output.</summary>
    public const string SetVolume = "audio.set_volume";

    /// <summary>Mutes or unmutes the default output.</summary>
    public const string SetMute = "audio.set_mute";

    /// <summary>Presses a media key (sent to the media player through MPRIS).</summary>
    public const string MediaKey = "media.key";

    /// <summary>Controls the current media player.</summary>
    public const string MediaControl = MediaControlSpec.ActionName;

    /// <summary>Starts a program on the desktop of the user.</summary>
    public const string AppLaunch = "app.launch";

    /// <summary>Opens a web address in the default browser of the user.</summary>
    public const string UrlOpen = "url.open";

    /// <summary>Ends the processes of the user that have a given name.</summary>
    public const string ProcessKill = "process.kill";

    /// <summary>Shows a desktop notification.</summary>
    public const string NotifyToast = NotifyToastSpec.ActionName;
}

/// <summary>The values of <c>Key</c> of <c>media.key</c>, the same as on Windows. A class of its own, so that it is ready when the catalog is built.</summary>
public static class LinuxMediaKeyNames
{
    /// <summary>Play or pause.</summary>
    public const string PlayPause = "play_pause";

    /// <summary>The next track.</summary>
    public const string Next = "next";

    /// <summary>The previous track.</summary>
    public const string Previous = "previous";

    /// <summary>Stop.</summary>
    public const string Stop = "stop";

    /// <summary>All values, in the order the editor shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [PlayPause, Next, Previous, Stop];
}

/// <summary>
/// The built-in actions that Linux offers, with their parameters and the context each needs (PLAN.md S06, S11, S17, T803). Power and
/// session actions run in the service (<c>system</c>): logind and systemd answer the root of the agent, so they work without anybody
/// logged in (the session actions need a session, of course). What touches the desktop of the user (volume, notifications, media,
/// programs) runs in the session helper (<c>user</c>). A Windows built-in action that Linux has no equivalent for is not in the list, so
/// a command that uses it is flagged as unsupported: <c>power.keep_awake</c>, <c>audio.set_default_output</c> and
/// <c>audio.set_default_input</c>. The helper declares the same descriptors (the same class); a test keeps the lists equal.
/// </summary>
public static class LinuxActionCatalog
{
    /// <summary>The longest process name that <c>process.kill</c> takes; Linux limits the name of a process to 15 characters, but a script name is longer.</summary>
    public const int MaxProcessNameLength = 255;

    /// <summary>The longest web address accepted by <c>url.open</c>.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The longest argument string accepted by <c>app.launch</c>.</summary>
    public const int MaxArgumentsLength = 2048;

    /// <summary>The longest path accepted by <c>app.launch</c>.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>
    /// The pattern of a process name: no path separators, wildcards, quotes or control characters, so a name can only ever select
    /// processes by their name.
    /// </summary>
    public const string ProcessNamePattern = @"^[^\\/:*?""<>|\x00-\x1F]+$";

    private static readonly ParameterDefinition Force = new(
        "Force",
        ParameterTypes.Flag,
        Default: JsonBoolean(false),
        Description: "Ignore programs that ask the system to stay up (the inhibitors of logind, for example a running update or a recording). Unsaved work in them is lost.");

    private static readonly ParameterDefinition[] DelayParameters =
    [
        new ParameterDefinition(
            PowerActionSpec.DelaySeconds,
            ParameterTypes.WholeNumber,
            Default: JsonNumber(0),
            Min: 0,
            Max: PowerActionSpec.MaxDelaySeconds,
            Step: 1,
            Unit: "s",
            Description: "Wait this long before acting. A notification tells the logged-in user; the action power.cancel stops it. 0 acts at once. Only one delayed power action can be pending."),
        new ParameterDefinition(
            PowerActionSpec.Message,
            ParameterTypes.Text,
            MaxLength: PowerActionSpec.MaxMessageLength,
            Description: "A text for the notification that announces the delayed action."),
    ];

    private static readonly BuiltinActionDescriptor[] Descriptors =
    [
        new(
            LinuxActionNames.Shutdown,
            "Shut down",
            RunAsValues.System,
            [Force, .. DelayParameters],
            "Shuts the computer down through systemd (systemctl poweroff), at once or after DelaySeconds."),
        new(
            LinuxActionNames.Restart,
            "Restart",
            RunAsValues.System,
            [Force, .. DelayParameters],
            "Restarts the computer through systemd (systemctl reboot), at once or after DelaySeconds."),
        new(
            LinuxActionNames.Sleep,
            "Sleep",
            RunAsValues.System,
            DelayParameters,
            "Puts the computer to sleep through systemd (systemctl suspend), at once or after DelaySeconds. The run ends when the request was made, not when the computer wakes up."),
        new(
            LinuxActionNames.Hibernate,
            "Hibernate",
            RunAsValues.System,
            DelayParameters,
            "Hibernates the computer through systemd (systemctl hibernate), at once or after DelaySeconds. Fails when hibernation is not set up (no swap space for the image)."),
        new(
            LinuxActionNames.CancelPending,
            PowerActionSpec.CancelTitle,
            RunAsValues.System,
            PowerActionSpec.CancelParameters,
            PowerActionSpec.CancelDescription),
        new(
            LinuxActionNames.LockSession,
            "Lock",
            RunAsValues.System,
            [],
            "Locks the session of the logged-in user through logind (loginctl lock-session). The desktop must listen for the lock request, which GNOME, KDE and the other full desktops do."),
        new(
            LinuxActionNames.Logoff,
            "Log off",
            RunAsValues.System,
            DelayParameters,
            "Ends the session of the logged-in user through logind (loginctl terminate-session), at once or after DelaySeconds. Programs of the user are closed without asking."),
        new(
            LinuxActionNames.DisplayOff,
            "Turn displays off",
            RunAsValues.User,
            [],
            "Turns the displays off (X11: xset dpms force off). A Wayland desktop that has no such request reports the action as unsupported."),
        new(
            LinuxActionNames.SetVolume,
            "Set volume",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Level",
                    ParameterTypes.WholeNumber,
                    Required: true,
                    Min: 0,
                    Max: 100,
                    Step: 1,
                    Unit: "%",
                    Description: "The volume of the default output (wpctl of PipeWire, otherwise pactl)."),
            ],
            "Sets the volume of the default audio output of the user."),
        new(
            LinuxActionNames.SetMute,
            "Mute or unmute",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Muted",
                    ParameterTypes.Flag,
                    Default: JsonBoolean(true),
                    Description: "True mutes, false unmutes."),
            ],
            "Mutes or unmutes the default audio output of the user."),
        new(
            LinuxActionNames.MediaKey,
            "Media key",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Key",
                    ParameterTypes.Choice,
                    Required: true,
                    Options: LinuxMediaKeyNames.All,
                    Description: "The media key to press; it goes to the current media player (MPRIS, through playerctl)."),
            ],
            "Sends a media key to the current media player of the user. Needs playerctl."),
        new(
            LinuxActionNames.MediaControl,
            MediaControlSpec.ActionTitle,
            RunAsValues.User,
            MediaControlSpec.Parameters,
            MediaControlSpec.ActionDescription),
        new(
            LinuxActionNames.AppLaunch,
            "Launch program",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Path",
                    ParameterTypes.Text,
                    Required: true,
                    MaxLength: MaxPathLength,
                    Description: "The program: a full path such as /usr/bin/firefox, or a name that is on the PATH of the user."),
                new ParameterDefinition(
                    "Arguments",
                    ParameterTypes.Text,
                    MaxLength: MaxArgumentsLength,
                    Description: "The arguments, separated by spaces. Quote an argument that has spaces ('like this' or \"like this\"). No shell reads them: $, backticks and ; are ordinary characters."),
            ],
            "Starts a program on the desktop of the user; the run ends when it was started."),
        new(
            LinuxActionNames.UrlOpen,
            "Open web address",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Url",
                    ParameterTypes.Text,
                    Required: true,
                    MaxLength: MaxUrlLength,
                    Description: "An http or https address."),
            ],
            "Opens an http or https address in the default browser of the user (xdg-open)."),
        new(
            LinuxActionNames.ProcessKill,
            "Kill process",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Name",
                    ParameterTypes.Text,
                    Required: true,
                    Pattern: ProcessNamePattern,
                    MaxLength: MaxProcessNameLength,
                    Description: "The name of the program without path."),
            ],
            "Ends every process of the logged-in user that has this name, together with its child processes."),
        new(
            LinuxActionNames.NotifyToast,
            NotifyToastSpec.ActionTitle,
            RunAsValues.User,
            NotifyToastSpec.Parameters,
            NotifyToastSpec.ActionDescription
                + " On Linux the notification shows the title and the text; pictures and buttons are not supported and are ignored (notify-send)."),
    ];

    /// <summary>Every action, in the order the editor shows them.</summary>
    public static IReadOnlyList<BuiltinActionDescriptor> All => Descriptors;

    /// <summary>The actions that run in the service.</summary>
    public static IReadOnlyList<BuiltinActionDescriptor> SystemActions { get; } =
        [.. Descriptors.Where(descriptor => descriptor.RunAs == RunAsValues.System)];

    /// <summary>The actions that run in the session helper.</summary>
    public static IReadOnlyList<BuiltinActionDescriptor> UserActions { get; } =
        [.. Descriptors.Where(descriptor => descriptor.RunAs == RunAsValues.User)];

    /// <summary>Looks an action up.</summary>
    /// <param name="name">The action name.</param>
    /// <returns>The descriptor, or null for an unknown name.</returns>
    public static BuiltinActionDescriptor? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Descriptors.FirstOrDefault(descriptor => string.Equals(descriptor.Name, name, StringComparison.Ordinal));
    }

    private static JsonElement JsonBoolean(bool value)
    {
        using JsonDocument document = JsonDocument.Parse(value ? "true" : "false");
        return document.RootElement.Clone();
    }

    private static JsonElement JsonNumber(int value)
    {
        using JsonDocument document = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }
}
