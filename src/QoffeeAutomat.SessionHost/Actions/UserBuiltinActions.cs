using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.SessionHost.Audio;
using QoffeeAutomat.SessionHost.Dialogs;
using QoffeeAutomat.SessionHost.Media;
using QoffeeAutomat.SessionHost.Notifications;

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

    /// <summary>The name of <c>notify.toast</c>.</summary>
    public const string NotifyToast = NotifyToastSpec.ActionName;

    /// <summary>The name of <c>media.control</c>.</summary>
    public const string MediaControl = MediaControlSpec.ActionName;

    /// <summary>The name of <c>audio.set_default_output</c>.</summary>
    public const string SetDefaultOutput = AudioDeviceSpec.SetDefaultOutput;

    /// <summary>The name of <c>audio.set_default_input</c>.</summary>
    public const string SetDefaultInput = AudioDeviceSpec.SetDefaultInput;

    /// <summary>The values of the parameter <c>Key</c> of <c>media.key</c>.</summary>
    public static readonly IReadOnlyList<string> MediaKeys = ["play_pause", "next", "previous", "stop"];

    private const string ProcessNamePattern = @"^[^\\/:*?""<>|\x00-\x1F]+$";

    /// <summary>The longest headline of the countdown dialog: the title of an action.</summary>
    private const int MaxCountdownTitleLength = 64;

    private static readonly ParameterDefinition Force = new(
        "Force",
        ParameterTypes.Flag,
        Default: JsonBoolean(false),
        Description: "Close programs that do not respond to the request instead of waiting for them. Unsaved work in them is lost.");

    private static readonly BuiltinActionDescriptor[] Descriptors =
    [
        new(LockSession, "Lock", RunAsValues.User, [], "Locks the session of the logged-in user."),
        new(
            Logoff,
            "Log off",
            RunAsValues.User,
            [Force, .. PowerActionSpec.DelayParameters],
            "Logs the user off, at once or after DelaySeconds with a countdown dialog that can be cancelled."),
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
        new(NotifyToast, NotifyToastSpec.ActionTitle, RunAsValues.User, NotifyToastSpec.Parameters, NotifyToastSpec.ActionDescription),
        new(MediaControl, MediaControlSpec.ActionTitle, RunAsValues.User, MediaControlSpec.Parameters, MediaControlSpec.ActionDescription),
        new(SetDefaultOutput, AudioDeviceSpec.OutputTitle, RunAsValues.User, AudioDeviceSpec.OutputParameters, AudioDeviceSpec.OutputDescription),
        new(SetDefaultInput, AudioDeviceSpec.InputTitle, RunAsValues.User, AudioDeviceSpec.InputParameters, AudioDeviceSpec.InputDescription),
    ];

    private readonly IDesktopNativeApi _desktop;
    private readonly IAudioEndpoint _audio;
    private readonly IProcessControl _processes;
    private readonly IToastPresenter? _toasts;
    private readonly ToastImageStore? _images;
    private readonly MediaController? _media;
    private readonly AudioDeviceService? _devices;
    private readonly ICountdownDialog? _countdown;

    /// <summary>Creates the provider.</summary>
    /// <param name="desktop">The desktop calls; tests pass a fake that never touches the desktop.</param>
    /// <param name="audio">The audio device.</param>
    /// <param name="processes">The process table.</param>
    /// <param name="toasts">Shows the notifications of <c>notify.toast</c> (T702); null makes that action fail with a message.</param>
    /// <param name="images">Keeps the pictures of notifications; null shows notifications without pictures.</param>
    /// <param name="media">The system media session for <c>media.control</c> (T716); null makes that action fail with a message.</param>
    /// <param name="devices">The audio devices for <c>audio.set_default_output</c> and <c>audio.set_default_input</c> (T713); null makes those actions fail with a message.</param>
    /// <param name="countdown">Shows the countdown dialog of the delayed power actions (T715, the internal action <c>power.countdown</c>); null makes that action fail with a message, and the countdown of the agent then simply elapses.</param>
    public UserBuiltinActions(
        IDesktopNativeApi desktop,
        IAudioEndpoint audio,
        IProcessControl processes,
        IToastPresenter? toasts = null,
        ToastImageStore? images = null,
        IMediaSessionSource? media = null,
        AudioDeviceService? devices = null,
        ICountdownDialog? countdown = null)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(processes);

        _desktop = desktop;
        _audio = audio;
        _processes = processes;
        _toasts = toasts;
        _images = images;
        _media = media is null ? null : new MediaController(media, desktop.SendMediaKey);
        _devices = devices;
        _countdown = countdown;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => Descriptors;

    /// <summary>Creates the provider that really acts on this desktop.</summary>
    /// <param name="toasts">Shows the notifications; the caller also listens to its button presses.</param>
    /// <param name="media">The system media session; the caller also reads the media metrics from it.</param>
    /// <param name="devices">The audio devices; the caller also reads the device metrics from it.</param>
    /// <param name="countdown">Shows the countdown dialog of the delayed power actions (T715).</param>
    /// <returns>The provider.</returns>
    public static UserBuiltinActions CreateNative(
        IToastPresenter? toasts = null,
        IMediaSessionSource? media = null,
        AudioDeviceService? devices = null,
        ICountdownDialog? countdown = null)
    {
        return new UserBuiltinActions(
            new WindowsDesktopNativeApi(),
            new CoreAudioEndpointControl(),
            new SystemProcessControl(),
            toasts,
            new ToastImageStore(ToastImageStore.DefaultDirectory()),
            media,
            devices,
            countdown);
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

        // The internal action of the countdown dialog (T715) is in no catalog: only the agent can send it over the session pipe.
        if (string.Equals(name, PowerActionSpec.Countdown, StringComparison.Ordinal))
        {
            try
            {
                return await CountdownAction(variables, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // No dialog could be shown; the agent counts down without one.
                return Refused("The countdown dialog could not be shown: " + exception.Message);
            }
        }

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
            case NotifyToast:
                return NotifyAction(variables);
            case MediaControl:
                return _media is null
                    ? Refused("The media session is not available in this session.")
                    : await _media.ControlAsync(BuiltinActionVariables.GetString(variables, MediaControlSpec.ActionParameter), cancellationToken).ConfigureAwait(false);
            case SetDefaultOutput:
                return SetDefaultDevice(AudioFlow.Output, variables);
            case SetDefaultInput:
                return SetDefaultDevice(AudioFlow.Input, variables);
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation.");
        }
    }

    /// <summary>
    /// <c>power.countdown</c> (T715, internal): shows the topmost dialog with the message, the remaining time and a cancel button, and
    /// waits until it ends. A press of the cancel button ends the run with the error <c>cancelled</c>, which is how the agent learns that the
    /// user cancelled; a dialog whose time is over ends it with success. The agent cancels the run when the countdown ends on its side, which
    /// closes the dialog. The values are checked here again, whatever the agent sent.
    /// </summary>
    private async ValueTask<ExecutionResult> CountdownAction(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        if (_countdown is null)
        {
            return Refused("The countdown dialog is not available in this session.");
        }

        long? seconds = BuiltinActionVariables.GetLong(variables, PowerActionSpec.CountdownSeconds);
        if (seconds is not { } total || total < 1 || total > PowerActionSpec.MaxDelaySeconds)
        {
            return Invalid("Seconds must be a whole number from 1 to " + PowerActionSpec.MaxDelaySeconds.ToString(CultureInfo.InvariantCulture) + ".");
        }

        string? title = BuiltinActionVariables.GetString(variables, PowerActionSpec.CountdownTitle);
        if (string.IsNullOrWhiteSpace(title) || title.Length > MaxCountdownTitleLength)
        {
            return Invalid("Title is required and may have at most " + MaxCountdownTitleLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        string? message = BuiltinActionVariables.GetString(variables, PowerActionSpec.Message);
        if (message is { Length: > PowerActionSpec.MaxMessageLength })
        {
            return Invalid("Message may have at most " + PowerActionSpec.MaxMessageLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        CountdownDialogOutcome outcome = await _countdown
            .ShowAsync(new CountdownDialogRequest(title, message, TimeSpan.FromSeconds(total)), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return outcome switch
        {
            CountdownDialogOutcome.Cancelled => ExecutionResult.Failure(ErrorCodes.Cancelled, "The user cancelled the countdown."),
            CountdownDialogOutcome.Elapsed => Done("The countdown is over."),
            _ => Done("The countdown dialog was closed."),
        };
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

    /// <summary>
    /// <c>audio.set_default_output</c> and <c>audio.set_default_input</c> (T713). The name is matched against the active devices
    /// by <see cref="AudioDeviceService.SetDefault"/>; a name that fits no device or several is <c>invalid_vars</c> and nothing is changed.
    /// </summary>
    private ExecutionResult SetDefaultDevice(AudioFlow flow, IReadOnlyDictionary<string, object> variables)
    {
        return _devices is null
            ? Refused("Audio devices are not available in this session.")
            : _devices.SetDefault(flow, BuiltinActionVariables.GetString(variables, AudioDeviceSpec.Device));
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

    /// <summary>
    /// <c>notify.toast</c> (T702). Every value is checked here again, whatever the agent validated: the text and the labels have
    /// their limits, the ids of the buttons and the tag are plain ids, every button needs an id and a label, ids are unique, and
    /// the picture (the member <c>ImageData</c> that only the agent adds) must be a real picture. A picture that does not pass
    /// does not stop the notification; the result says that it was left out.
    /// </summary>
    private ExecutionResult NotifyAction(IReadOnlyDictionary<string, object> variables)
    {
        if (_toasts is null)
        {
            return Refused("Notifications are not available in this session.");
        }

        string? message = BuiltinActionVariables.GetString(variables, NotifyToastSpec.Message);
        if (string.IsNullOrWhiteSpace(message) || message.Length > NotifyToastSpec.MaxMessageLength)
        {
            return Invalid("Message is required and may have at most 1024 characters.");
        }

        string? title = BuiltinActionVariables.GetString(variables, NotifyToastSpec.Title);
        if (title is { Length: > NotifyToastSpec.MaxTitleLength })
        {
            return Invalid("Title may have at most 128 characters.");
        }

        string? tag = BuiltinActionVariables.GetString(variables, NotifyToastSpec.Tag);
        if (!string.IsNullOrEmpty(tag) && !NotifyToastSpec.IsValidId(tag))
        {
            return Invalid("Tag may have at most 64 letters, digits and the characters _ . : -.");
        }

        var buttons = new List<ToastButton>(NotifyToastSpec.MaxButtons);
        for (int number = 1; number <= NotifyToastSpec.MaxButtons; number++)
        {
            string? id = BuiltinActionVariables.GetString(variables, NotifyToastSpec.ButtonId(number));
            string? label = BuiltinActionVariables.GetString(variables, NotifyToastSpec.ButtonTitle(number));
            if (string.IsNullOrEmpty(id) && string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            if (!NotifyToastSpec.IsValidId(id) || string.IsNullOrWhiteSpace(label) || label.Length > NotifyToastSpec.MaxButtonTitleLength)
            {
                return Invalid("Button " + number.ToString(CultureInfo.InvariantCulture) + " needs an id (at most 64 letters, digits and _ . : -) and a label (at most 64 characters).");
            }

            if (buttons.Any(button => string.Equals(button.Id, id, StringComparison.Ordinal)))
            {
                return Invalid("The ids of the buttons must be different.");
            }

            buttons.Add(new ToastButton(id, label));
        }

        string? skipped = null;
        string? imagePath = null;
        string? imageData = BuiltinActionVariables.GetString(variables, NotifyToastSpec.ImageData);
        if (!string.IsNullOrEmpty(imageData))
        {
            if (_images is null)
            {
                skipped = "pictures are not available in this session";
            }
            else
            {
                try
                {
                    skipped = _images.TrySave(imageData, out imagePath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped = exception.Message;
                }
            }
        }

        string? problem = _toasts.Show(new ToastRequest(string.IsNullOrWhiteSpace(title) ? null : title, message, buttons, string.IsNullOrEmpty(tag) ? null : tag, imagePath));
        if (problem is not null)
        {
            return Refused(problem);
        }

        return Done(skipped is null ? "The notification was shown." : "The notification was shown; the picture was left out: " + skipped);
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
