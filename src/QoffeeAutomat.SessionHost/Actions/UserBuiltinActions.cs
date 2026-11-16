using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.SessionHost.Actions;

/// <summary>
/// The built-in actions that run in the session of the user (PLAN.md S11, T306): lock, log off, displays, volume, mute,
/// media keys, launching programs, opening addresses and ending processes. The agent hands such an action to the SessionHost
/// (<c>UserContextBuiltinActions</c>), which executes it here with the standard token of the user. The descriptors repeat the
/// ones of <c>WindowsActionCatalog</c> in the Windows platform, which this project cannot reference; a test keeps them equal.
/// Every value is checked once more (<see cref="LaunchPolicy"/>), whatever the agent validated.
/// </summary>
public sealed class UserBuiltinActions : IBuiltinActionProvider
{
    /// <summary>The name of <c>session.lock</c>.</summary>
    public const string LockSession = "session.lock";

    /// <summary>The name of <c>session.logoff</c>.</summary>
    public const string Logoff = "session.logoff";

    /// <summary>The name of <c>display.off</c>.</summary>
    public const string DisplayOff = "display.off";

    /// <summary>The name of <c>audio.set_volume</c>.</summary>
    public const string SetVolume = "audio.set_volume";

    /// <summary>The name of <c>audio.set_mute</c>.</summary>
    public const string SetMute = "audio.set_mute";

    /// <summary>The name of <c>media.key</c>.</summary>
    public const string MediaKey = "media.key";

    /// <summary>The name of <c>app.launch</c>.</summary>
    public const string AppLaunch = "app.launch";

    /// <summary>The name of <c>url.open</c>.</summary>
    public const string UrlOpen = "url.open";

    /// <summary>The name of <c>process.kill</c>.</summary>
    public const string ProcessKill = "process.kill";

    /// <summary>The values of the parameter <c>Key</c> of <c>media.key</c>.</summary>
    public static readonly IReadOnlyList<string> MediaKeys = ["play_pause", "next", "previous", "stop"];

    private const string ProcessNamePattern = @"^[^\\/:*?""<>|\x00-\x1F]+$";

    private static readonly ParameterDefinition Force = new(
        "Force",
        ParameterTypes.Flag,
        Default: JsonBoolean(false),
        Description: "Close programs that do not respond to the request instead of waiting for them. Unsaved work in them is lost.");

    private static readonly BuiltinActionDescriptor[] Descriptors =
    [
        new(LockSession, "Lock", RunAsValues.User, [], "Locks the session of the logged-in user."),
        new(Logoff, "Log off", RunAsValues.User, [Force], "Logs the user off."),
        new(DisplayOff, "Turn displays off", RunAsValues.User, [], "Turns the displays off; moving the mouse turns them on again."),
        new(
            SetVolume,
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
                    Description: "The master volume of the default playback device."),
            ],
            "Sets the master volume of the default playback device."),
        new(
            SetMute,
            "Mute or unmute",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Muted",
                    ParameterTypes.Flag,
                    Default: JsonBoolean(true),
                    Description: "True mutes, false unmutes."),
            ],
            "Mutes or unmutes the default playback device."),
        new(
            MediaKey,
            "Media key",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Key",
                    ParameterTypes.Choice,
                    Required: true,
                    Options: MediaKeys,
                    Description: "The media key to press."),
            ],
            "Presses a media key; the program that has the media focus reacts."),
        new(
            AppLaunch,
            "Launch program",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Path",
                    ParameterTypes.Text,
                    Required: true,
                    MaxLength: 1024,
                    Description: "The full path of the program or document, for example C:\\Program Files\\App\\app.exe."),
                new ParameterDefinition(
                    "Arguments",
                    ParameterTypes.Text,
                    MaxLength: 2048,
                    Description: "The arguments for the program."),
            ],
            "Starts a program on the desktop of the user; the run ends when it was started."),
        new(
            UrlOpen,
            "Open web address",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Url",
                    ParameterTypes.Text,
                    Required: true,
                    MaxLength: 2048,
                    Description: "An http or https address."),
            ],
            "Opens an http or https address in the default browser of the user."),
        new(
            ProcessKill,
            "Kill process",
            RunAsValues.User,
            [
                new ParameterDefinition(
                    "Name",
                    ParameterTypes.Text,
                    Required: true,
                    Pattern: ProcessNamePattern,
                    MaxLength: 255,
                    Description: "The name of the program without path, with or without .exe."),
            ],
            "Ends every process of the logged-in user that has this name, together with its child processes."),
    ];

    private readonly IDesktopNativeApi _desktop;
    private readonly IAudioEndpoint _audio;
    private readonly IProcessControl _processes;

    /// <summary>Creates the provider.</summary>
    /// <param name="desktop">The desktop calls; tests pass a fake that never touches the desktop.</param>
    /// <param name="audio">The audio device.</param>
    /// <param name="processes">The process table.</param>
    public UserBuiltinActions(IDesktopNativeApi desktop, IAudioEndpoint audio, IProcessControl processes)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(processes);

        _desktop = desktop;
        _audio = audio;
        _processes = processes;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => Descriptors;

    /// <summary>Creates the provider that really acts on this desktop.</summary>
    /// <returns>The provider.</returns>
    public static UserBuiltinActions CreateNative()
    {
        return new UserBuiltinActions(new WindowsDesktopNativeApi(), new CoreAudioEndpointControl(), new SystemProcessControl());
    }

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variables);
        cancellationToken.ThrowIfCancellationRequested();

        BuiltinActionDescriptor? descriptor = Descriptors.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return ExecutionResult.Failure(ErrorCodes.BadRequest, $"Unknown built-in action '{name}'.");
        }

        try
        {
            return await RunAsync(name, variables, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ExecutionResult(1, string.Empty, $"{descriptor.Title} failed: {exception.Message}");
        }
    }

    private static ExecutionResult Done(string message) => new(0, message, string.Empty);

    private static ExecutionResult Refused(string message) => new(1, string.Empty, message);

    private static ExecutionResult Invalid(string message) => ExecutionResult.Failure(ErrorCodes.InvalidVars, message);

    private static JsonElement JsonBoolean(bool value)
    {
        using JsonDocument document = JsonDocument.Parse(value ? "true" : "false");
        return document.RootElement.Clone();
    }

    private async ValueTask<ExecutionResult> RunAsync(string name, IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case LockSession:
                _desktop.LockWorkstation();
                return Done("The session was locked.");
            case Logoff:
                _desktop.LogOff(BuiltinActionVariables.GetBool(variables, "Force"));
                return Done("Logoff was requested.");
            case DisplayOff:
                _desktop.TurnDisplayOff();
                return Done("The displays were turned off.");
            case SetVolume:
                return SetVolumeAction(variables);
            case SetMute:
                return SetMuteAction(variables);
            case MediaKey:
                return MediaKeyAction(variables);
            case AppLaunch:
                return LaunchAction(variables);
            case UrlOpen:
                return OpenUrlAction(variables);
            case ProcessKill:
                return await KillAction(variables, cancellationToken).ConfigureAwait(false);
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation.");
        }
    }

    private ExecutionResult SetVolumeAction(IReadOnlyDictionary<string, object> variables)
    {
        long? level = BuiltinActionVariables.GetLong(variables, "Level");
        if (level is not { } percent || percent < 0 || percent > 100)
        {
            return Invalid("Level must be a whole number from 0 to 100.");
        }

        return _audio.TrySetVolume(percent)
            ? Done($"The volume is {percent.ToString(CultureInfo.InvariantCulture)} %.")
            : Refused("There is no audio playback device.");
    }

    private ExecutionResult SetMuteAction(IReadOnlyDictionary<string, object> variables)
    {
        bool muted = BuiltinActionVariables.GetBool(variables, "Muted", fallback: true);
        return _audio.TrySetMute(muted)
            ? Done(muted ? "The sound is muted." : "The sound is on.")
            : Refused("There is no audio playback device.");
    }

    private ExecutionResult MediaKeyAction(IReadOnlyDictionary<string, object> variables)
    {
        string? key = BuiltinActionVariables.GetString(variables, "Key");
        if (key is null || !MediaKeys.Contains(key))
        {
            return Invalid("Key must be one of play_pause, next, previous and stop.");
        }

        _desktop.SendMediaKey(key);
        return Done($"The media key {key} was pressed.");
    }

    private ExecutionResult LaunchAction(IReadOnlyDictionary<string, object> variables)
    {
        string? path = BuiltinActionVariables.GetString(variables, "Path");
        string? arguments = BuiltinActionVariables.GetString(variables, "Arguments");
        string? problem = LaunchPolicy.CheckLaunch(path, arguments);
        if (problem is not null)
        {
            return Invalid(problem);
        }

        _desktop.Launch(path!, string.IsNullOrEmpty(arguments) ? null : arguments);
        return Done("The program was started.");
    }

    private ExecutionResult OpenUrlAction(IReadOnlyDictionary<string, object> variables)
    {
        string? problem = LaunchPolicy.CheckUrl(BuiltinActionVariables.GetString(variables, "Url"), out Uri? uri);
        if (problem is not null || uri is null)
        {
            return Invalid(problem ?? "The address is not valid.");
        }

        _desktop.OpenUrl(uri);
        return Done("The address was opened.");
    }

    private async ValueTask<ExecutionResult> KillAction(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        string? problem = LaunchPolicy.CheckProcessName(BuiltinActionVariables.GetString(variables, "Name"), out string name);
        if (problem is not null)
        {
            return Invalid(problem);
        }

        int[] targets = [.. _processes.FindByName(name).Where(id => id != _processes.CurrentProcessId)];
        if (targets.Length == 0)
        {
            return Refused($"No process named '{name}' is running in this session.");
        }

        int ended = 0;
        var failures = new List<string>();
        foreach (int id in targets)
        {
            string? failure = await _processes.KillTreeAsync(id, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                ended++;
            }
            else
            {
                failures.Add(failure);
            }
        }

        string summary = $"Ended {ended.ToString(CultureInfo.InvariantCulture)} process(es) named '{name}'.";
        return failures.Count == 0
            ? Done(summary)
            : new ExecutionResult(1, summary, $"{failures.Count.ToString(CultureInfo.InvariantCulture)} process(es) could not be ended: {failures[0]}");
    }
}
