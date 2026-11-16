using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Actions;

/// <summary>The names of the built-in actions of v1 (PLAN.md S11).</summary>
public static class WindowsActionNames
{
    /// <summary>Shuts the computer down.</summary>
    public const string Shutdown = "power.shutdown";

    /// <summary>Restarts the computer.</summary>
    public const string Restart = "power.restart";

    /// <summary>Puts the computer to sleep.</summary>
    public const string Sleep = "power.sleep";

    /// <summary>Hibernates the computer.</summary>
    public const string Hibernate = "power.hibernate";

    /// <summary>Locks the session of the user.</summary>
    public const string LockSession = "session.lock";

    /// <summary>Logs the user off.</summary>
    public const string Logoff = "session.logoff";

    /// <summary>Turns the displays off.</summary>
    public const string DisplayOff = "display.off";

    /// <summary>Sets the master volume.</summary>
    public const string SetVolume = "audio.set_volume";

    /// <summary>Mutes or unmutes the master volume.</summary>
    public const string SetMute = "audio.set_mute";

    /// <summary>Presses a media key.</summary>
    public const string MediaKey = "media.key";

    /// <summary>Starts a program on the desktop of the user.</summary>
    public const string AppLaunch = "app.launch";

    /// <summary>Opens a web address in the default browser of the user.</summary>
    public const string UrlOpen = "url.open";

    /// <summary>Ends the processes of the user that have a given name.</summary>
    public const string ProcessKill = "process.kill";
}

/// <summary>The names of the parameters of the built-in actions.</summary>
public static class WindowsActionParameters
{
    /// <summary>Do not wait for programs that do not close (<c>power.shutdown</c>, <c>power.restart</c>, <c>session.logoff</c>).</summary>
    public const string Force = "Force";

    /// <summary>The volume in percent (<c>audio.set_volume</c>).</summary>
    public const string Level = "Level";

    /// <summary>Whether to mute (<c>audio.set_mute</c>).</summary>
    public const string Muted = "Muted";

    /// <summary>The media key (<c>media.key</c>).</summary>
    public const string Key = "Key";

    /// <summary>The full path of the program (<c>app.launch</c>).</summary>
    public const string Path = "Path";

    /// <summary>The arguments of the program (<c>app.launch</c>).</summary>
    public const string Arguments = "Arguments";

    /// <summary>The process name (<c>process.kill</c>).</summary>
    public const string Name = "Name";

    /// <summary>The web address (<c>url.open</c>).</summary>
    public const string Url = "Url";
}

/// <summary>The values of the parameter <see cref="WindowsActionParameters.Key"/> of <c>media.key</c>.</summary>
public static class MediaKeyNames
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
/// The v1 built-in actions of Windows with their parameter definitions and the context each needs (PLAN.md S06, S11, T306).
/// The power actions run in the service (<c>system</c>); everything that touches the desktop or the audio of the user runs
/// in the SessionHost (<c>user</c>). The SessionHost implements the <c>user</c> half itself and declares the same
/// descriptors; a test keeps the two lists equal, because the SessionHost cannot reference this project.
/// </summary>
public static class WindowsActionCatalog
{
    /// <summary>The longest process name Windows allows for an executable file name.</summary>
    public const int MaxProcessNameLength = 255;

    /// <summary>The longest web address accepted by <c>url.open</c>.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The longest argument string accepted by <c>app.launch</c>.</summary>
    public const int MaxArgumentsLength = 2048;

    /// <summary>The longest path accepted by <c>app.launch</c>.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>
    /// The pattern of a process name: no path separators, wildcards, quotes or control characters, so a name can only
    /// ever select processes by their image name.
    /// </summary>
    public const string ProcessNamePattern = @"^[^\\/:*?""<>|\x00-\x1F]+$";

    private static readonly ParameterDefinition Force = new(
        WindowsActionParameters.Force,
        ParameterTypes.Flag,
        Default: JsonBoolean(false),
        Description: "Close programs that do not respond to the request instead of waiting for them. Unsaved work in them is lost.");

    private static readonly BuiltinActionDescriptor[] Descriptors =
    [
        new(
            WindowsActionNames.Shutdown,
            "Shut down",
            RunAsValues.System,
            [Force],
            "Shuts the computer down."),
        new(
            WindowsActionNames.Restart,
            "Restart",
            RunAsValues.System,
            [Force],
            "Restarts the computer."),
        new(
            WindowsActionNames.Sleep,
            "Sleep",
            RunAsValues.System,
            [],
            "Puts the computer to sleep. The run ends when the request was made, not when the computer wakes up."),
        new(
            WindowsActionNames.Hibernate,
            "Hibernate",
            RunAsValues.System,
            [],
            "Hibernates the computer. Fails when hibernation is not enabled."),
        new(
            WindowsActionNames.LockSession,
            "Lock",
            RunAsValues.User,
            [],
            "Locks the session of the logged-in user."),
        new(
            WindowsActionNames.Logoff,
            "Log off",
            RunAsValues.User,
            [Force],
            "Logs the user off."),
        new(
            WindowsActionNames.DisplayOff,
            "Turn displays off",
            RunAsValues.User,
            [],
            "Turns the displays off; moving the mouse turns them on again."),
        new(
            WindowsActionNames.SetVolume,
            "Set volume",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    WindowsActionParameters.Level,
                    ParameterTypes.WholeNumber,
                    Required: true,
                    Min: 0,
                    Max: 100,
                    Step: 1,
                    Unit: "%",
                    Description: "The master volume of the default playback device."),
            ],
            "Sets the master volume of the default playback device."),
        new(
            WindowsActionNames.SetMute,
            "Mute or unmute",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    WindowsActionParameters.Muted,
                    ParameterTypes.Flag,
                    Default: JsonBoolean(true),
                    Description: "True mutes, false unmutes."),
            ],
            "Mutes or unmutes the default playback device."),
        new(
            WindowsActionNames.MediaKey,
            "Media key",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    WindowsActionParameters.Key,
                    ParameterTypes.Choice,
                    Required: true,
                    Options: MediaKeyNames.All,
                    Description: "The media key to press."),
            ],
            "Presses a media key; the program that has the media focus reacts."),
        new(
            WindowsActionNames.AppLaunch,
            "Launch program",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    WindowsActionParameters.Path,
                    ParameterTypes.Text,
                    Required: true,
                    MaxLength: MaxPathLength,
                    Description: "The full path of the program or document, for example C:\\Program Files\\App\\app.exe."),
                new ParameterDefinition(
                    WindowsActionParameters.Arguments,
                    ParameterTypes.Text,
                    MaxLength: MaxArgumentsLength,
                    Description: "The arguments for the program."),
            ],
            "Starts a program on the desktop of the user; the run ends when it was started."),
        new(
            WindowsActionNames.UrlOpen,
            "Open web address",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    WindowsActionParameters.Url,
                    ParameterTypes.Text,
                    Required: true,
                    MaxLength: MaxUrlLength,
                    Description: "An http or https address."),
            ],
            "Opens an http or https address in the default browser of the user."),
        new(
            WindowsActionNames.ProcessKill,
            "Kill process",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    WindowsActionParameters.Name,
                    ParameterTypes.Text,
                    Required: true,
                    Pattern: ProcessNamePattern,
                    MaxLength: MaxProcessNameLength,
                    Description: "The name of the program without path, with or without .exe."),
            ],
            "Ends every process of the logged-in user that has this name, together with its child processes."),
    ];

    /// <summary>Every action, in the order the editor shows them.</summary>
    public static IReadOnlyList<BuiltinActionDescriptor> All => Descriptors;

    /// <summary>The actions that run in the service.</summary>
    public static IReadOnlyList<BuiltinActionDescriptor> SystemActions { get; } =
        [.. Descriptors.Where(descriptor => descriptor.RunAs == RunAsValues.System)];

    /// <summary>The actions that run in the SessionHost.</summary>
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
}
