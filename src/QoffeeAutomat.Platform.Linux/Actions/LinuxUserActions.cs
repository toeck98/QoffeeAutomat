using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Actions;

/// <summary>Starts a program on the desktop and does not wait for it (<c>app.launch</c>, <c>url.open</c>).</summary>
public interface IProgramLauncher
{
    /// <summary>Starts a program without a shell; every argument is one argv entry.</summary>
    /// <param name="fileName">The program: a path, or a name that is found on the <c>PATH</c>.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="failure">Why the program could not be started; null when it was.</param>
    /// <returns>True when the program was started.</returns>
    bool TryStart(string fileName, IReadOnlyList<string> arguments, out string? failure);
}

/// <summary>The <see cref="IProgramLauncher"/> that starts a real process, detached from the helper's output.</summary>
public sealed class ProcessProgramLauncher : IProgramLauncher
{
    /// <inheritdoc />
    public bool TryStart(string fileName, IReadOnlyList<string> arguments, out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            failure = process is null ? "The program could not be started." : null;
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            failure = exception.Message;
            return false;
        }
    }
}

/// <summary>Ends the processes of the user that have a given name (<c>process.kill</c>).</summary>
public interface IUserProcesses
{
    /// <summary>Ends every process of the user with this name, together with its child processes.</summary>
    /// <param name="name">The name of the program, without path.</param>
    /// <returns>The number of processes that were ended; processes of other users are not touched.</returns>
    int Kill(string name);
}

/// <summary>The <see cref="IUserProcesses"/> over the process table of the operating system.</summary>
public sealed class UserProcesses : IUserProcesses
{
    /// <summary>The length that Linux keeps of a process name (<c>comm</c>).</summary>
    public const int KernelNameLength = 15;

    /// <summary>Tells whether the name of a process is the name that was asked for; a name longer than the kernel keeps matches by its first characters.</summary>
    /// <param name="processName">The name that the operating system reports.</param>
    /// <param name="wanted">The name that was asked for.</param>
    /// <returns>True when they match (case matters on Linux).</returns>
    public static bool NameMatches(string processName, string wanted)
    {
        ArgumentNullException.ThrowIfNull(processName);
        ArgumentNullException.ThrowIfNull(wanted);

        if (string.Equals(processName, wanted, StringComparison.Ordinal))
        {
            return true;
        }

        return wanted.Length > KernelNameLength
            && processName.Length == KernelNameLength
            && string.Equals(processName, wanted[..KernelNameLength], StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public int Kill(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int ended = 0;
        int self = Environment.ProcessId;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || !NameMatches(process.ProcessName, name))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    ended++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
                {
                    // Not ours (another user), or already gone.
                }
            }
        }

        return ended;
    }
}

/// <summary>
/// Splits the argument text of <c>app.launch</c> into arguments without a shell (PLAN.md D-012): white space separates, single quotes keep
/// everything literally, double quotes keep everything except <c>\"</c> and <c>\\</c>, a backslash outside quotes protects the next
/// character. Nothing is expanded: <c>$HOME</c>, backticks, <c>;</c> and <c>*</c> are ordinary characters.
/// </summary>
public static class ArgumentTokenizer
{
    /// <summary>Splits a text.</summary>
    /// <param name="text">The argument text; null or blank gives no arguments.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>False when a quote is not closed or the text ends in a backslash.</returns>
    public static bool TrySplit(string? text, out IReadOnlyList<string> arguments)
    {
        var result = new List<string>();
        arguments = result;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var current = new StringBuilder();
        bool inToken = false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsWhiteSpace(character))
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            inToken = true;
            switch (character)
            {
                case '\'':
                    int closing = text.IndexOf('\'', index + 1);
                    if (closing < 0)
                    {
                        return false;
                    }

                    current.Append(text, index + 1, closing - index - 1);
                    index = closing;
                    break;
                case '"':
                    index++;
                    while (true)
                    {
                        if (index >= text.Length)
                        {
                            return false;
                        }

                        char inner = text[index];
                        if (inner == '"')
                        {
                            break;
                        }

                        if (inner == '\\' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
                        {
                            index++;
                            inner = text[index];
                        }

                        current.Append(inner);
                        index++;
                    }

                    break;
                case '\\':
                    if (index + 1 >= text.Length)
                    {
                        return false;
                    }

                    index++;
                    current.Append(text[index]);
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (inToken)
        {
            result.Add(current.ToString());
        }

        return true;
    }
}

/// <summary>
/// The built-in actions of the user's desktop (PLAN.md S11, S17, T803), executed inside the session helper with the rights and the
/// environment of the user (the display, the audio server and the session bus are the user's). Each action uses the tool that the desktop
/// offers (<c>wpctl</c> or <c>pactl</c> for audio, <c>playerctl</c> for media players, <c>notify-send</c> for notifications,
/// <c>xdg-open</c> for addresses, the GNOME shell or <c>xset</c> for the screen) and ends with a clear message when none is installed: the
/// action is then reported as not supported on this desktop, and nothing happens. Programs are started without a shell; no value is ever
/// part of a command line that a shell reads (D-012).
/// </summary>
public sealed class LinuxUserActions : IBuiltinActionProvider
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);

    private static readonly string[] GnomePowerSaveArguments =
    [
        "--user", "set-property", "org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig", "org.gnome.Mutter.DisplayConfig", "PowerSaveMode", "i", "1",
    ];

    private static readonly string[] XsetDisplayOffArguments = ["dpms", "force", "off"];

    private readonly ICommandRunner _runner;
    private readonly IProgramLauncher _launcher;
    private readonly IUserProcesses _processes;
    private readonly Func<string, string?> _environment;

    /// <summary>Creates the provider.</summary>
    /// <param name="runner">Runs the tools; tests pass a fake that never touches the desktop.</param>
    /// <param name="launcher">Starts programs; null starts real processes.</param>
    /// <param name="processes">Ends processes; null uses the process table.</param>
    /// <param name="environment">Reads an environment variable of the helper (<c>DISPLAY</c>); null uses the real environment.</param>
    public LinuxUserActions(
        ICommandRunner runner,
        IProgramLauncher? launcher = null,
        IUserProcesses? processes = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _launcher = launcher ?? new ProcessProgramLauncher();
        _processes = processes ?? new UserProcesses();
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => LinuxActionCatalog.UserActions;

    /// <inheritdoc />
    public async ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variables);

        if (LinuxActionCatalog.Find(name) is not { RunAs: RunAsValues.User })
        {
            return new ExecutionResult(127, string.Empty, $"Unknown built-in action '{name}'.", ErrorCodes.BadRequest);
        }

        return name switch
        {
            LinuxActionNames.DisplayOff => await DisplayOffAsync(cancellationToken).ConfigureAwait(false),
            LinuxActionNames.SetVolume => await SetVolumeAsync(variables, cancellationToken).ConfigureAwait(false),
            LinuxActionNames.SetMute => await SetMuteAsync(variables, cancellationToken).ConfigureAwait(false),
            LinuxActionNames.MediaKey => await MediaKeyAsync(variables, cancellationToken).ConfigureAwait(false),
            LinuxActionNames.MediaControl => await MediaControlAsync(variables, cancellationToken).ConfigureAwait(false),
            LinuxActionNames.AppLaunch => AppLaunch(variables),
            LinuxActionNames.UrlOpen => UrlOpen(variables),
            LinuxActionNames.ProcessKill => ProcessKill(variables),
            LinuxActionNames.NotifyToast => await NotifyAsync(variables, cancellationToken).ConfigureAwait(false),
            _ => ExecutionResult.Failure(ErrorCodes.Internal, $"The action '{name}' has no implementation."),
        };
    }

    /// <summary>Shows a notification; the agent sends one for a delayed power action.</summary>
    /// <param name="title">The title; blank becomes the name of the product.</param>
    /// <param name="message">The text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public async ValueTask<ExecutionResult> ShowNotificationAsync(string? title, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        string heading = string.IsNullOrWhiteSpace(title) ? "QoffeeAutomat" : title.Trim();
        CommandResult? result = await _runner.RunAsync(
            "notify-send",
            ["--app-name=QoffeeAutomat", "--", heading, message],
            ToolTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Unsupported("Showing notifications", "install notify-send (libnotify-bin)");
        }

        return result.Succeeded ? Done("The notification was shown.") : Failed("Notification", result);
    }

    private static ExecutionResult Done(string message) => new(0, message, string.Empty);

    private static ExecutionResult Unsupported(string what, string remedy) =>
        new(1, string.Empty, $"{what} is not supported on this desktop: {remedy}.");

    private static ExecutionResult Failed(string title, CommandResult result)
    {
        string line = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "the tool reported an error";
        return new ExecutionResult(result.ExitCode == 0 ? 1 : result.ExitCode, string.Empty, $"{title} failed: {line}");
    }

    private static ExecutionResult Invalid(string message) => ExecutionResult.Failure(ErrorCodes.InvalidVars, message);

    private async ValueTask<ExecutionResult> DisplayOffAsync(CancellationToken cancellationToken)
    {
        // GNOME (Wayland and X11) switches the screens off through the display configuration of the shell.
        CommandResult? shell = await _runner.RunAsync("busctl", GnomePowerSaveArguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (shell is { Succeeded: true })
        {
            return Done("The displays were turned off.");
        }

        if (!string.IsNullOrEmpty(_environment("DISPLAY")))
        {
            CommandResult? xset = await _runner.RunAsync("xset", XsetDisplayOffArguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
            if (xset is { Succeeded: true })
            {
                return Done("The displays were turned off.");
            }
        }

        return Unsupported("Turning the displays off", "this works on GNOME and on X11 with xset, and the desktop offers neither");
    }

    private async ValueTask<ExecutionResult> SetVolumeAsync(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        long? level = BuiltinActionVariables.GetLong(variables, "Level");
        if (level is null or < 0 or > 100)
        {
            return Invalid("Level must be a whole number from 0 to 100.");
        }

        string fraction = (level.Value / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
        string percent = level.Value.ToString(CultureInfo.InvariantCulture) + "%";
        return await AudioAsync(
            ["set-volume", "@DEFAULT_AUDIO_SINK@", fraction],
            ["set-sink-volume", "@DEFAULT_SINK@", percent],
            "Setting the volume",
            $"The volume is {percent}.",
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExecutionResult> SetMuteAsync(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        bool muted = BuiltinActionVariables.GetBool(variables, "Muted", fallback: true);
        string flag = muted ? "1" : "0";
        return await AudioAsync(
            ["set-mute", "@DEFAULT_AUDIO_SINK@", flag],
            ["set-sink-mute", "@DEFAULT_SINK@", flag],
            "Muting",
            muted ? "The sound is muted." : "The sound is on.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tries PipeWire (<c>wpctl</c>) first, then PulseAudio (<c>pactl</c>, which PipeWire also answers).</summary>
    private async ValueTask<ExecutionResult> AudioAsync(
        string[] wpctl,
        string[] pactl,
        string title,
        string success,
        CancellationToken cancellationToken)
    {
        CommandResult? first = await _runner.RunAsync("wpctl", wpctl, ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (first is { Succeeded: true })
        {
            return Done(success);
        }

        CommandResult? second = await _runner.RunAsync("pactl", pactl, ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (second is { Succeeded: true })
        {
            return Done(success);
        }

        CommandResult? failure = second ?? first;
        return failure is null
            ? Unsupported(title, "install wpctl (PipeWire) or pactl (PulseAudio)")
            : Failed(title, failure);
    }

    private async ValueTask<ExecutionResult> MediaKeyAsync(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        string? key = BuiltinActionVariables.GetString(variables, "Key");
        string? command = key switch
        {
            LinuxMediaKeyNames.PlayPause => "play-pause",
            LinuxMediaKeyNames.Next => "next",
            LinuxMediaKeyNames.Previous => "previous",
            LinuxMediaKeyNames.Stop => "stop",
            _ => null,
        };
        return command is null
            ? Invalid("Key must be play_pause, next, previous or stop.")
            : await PlayerAsync(command, $"The media key {key} was sent.", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExecutionResult> MediaControlAsync(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        string? action = BuiltinActionVariables.GetString(variables, MediaControlSpec.ActionParameter);
        string? command = action switch
        {
            MediaControlSpec.Play => "play",
            MediaControlSpec.Pause => "pause",
            MediaControlSpec.PlayPause => "play-pause",
            MediaControlSpec.Next => "next",
            MediaControlSpec.Previous => "previous",
            MediaControlSpec.Stop => "stop",
            _ => null,
        };
        return command is null
            ? Invalid("Action must be play, pause, play_pause, next, previous or stop.")
            : await PlayerAsync(command, $"The media player was told to {action}.", cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExecutionResult> PlayerAsync(string command, string success, CancellationToken cancellationToken)
    {
        CommandResult? result = await _runner.RunAsync("playerctl", [command], ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Unsupported("Controlling a media player", "install playerctl, which talks to every player that implements MPRIS");
        }

        if (result.Succeeded)
        {
            return Done(success);
        }

        // playerctl says "No players found" when nothing plays; that is a state of the desktop, not a fault of the action.
        return result.StandardError.Contains("No players found", StringComparison.OrdinalIgnoreCase)
            ? new ExecutionResult(1, string.Empty, "No media player is running.")
            : Failed("Media control", result);
    }

    private ExecutionResult AppLaunch(IReadOnlyDictionary<string, object> variables)
    {
        string? path = BuiltinActionVariables.GetString(variables, "Path");
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return Invalid("Path is required.");
        }

        if (!ArgumentTokenizer.TrySplit(BuiltinActionVariables.GetString(variables, "Arguments"), out IReadOnlyList<string> arguments))
        {
            return Invalid("Arguments has a quote that is not closed.");
        }

        if (path.Contains('/', StringComparison.Ordinal) && !File.Exists(path))
        {
            return new ExecutionResult(1, string.Empty, $"The program '{path}' does not exist.");
        }

        return _launcher.TryStart(path, arguments, out string? error)
            ? Done($"Started {Path.GetFileName(path)}.")
            : new ExecutionResult(1, string.Empty, $"The program could not be started: {error}");
    }

    private ExecutionResult UrlOpen(IReadOnlyDictionary<string, object> variables)
    {
        string? text = BuiltinActionVariables.GetString(variables, "Url");
        if (string.IsNullOrWhiteSpace(text)
            || text.Any(char.IsControl)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Invalid("Url must be an http or https address.");
        }

        return _launcher.TryStart("xdg-open", [uri.AbsoluteUri], out string? error)
            ? Done("The address was handed to the browser.")
            : Unsupported("Opening addresses", "install xdg-utils (xdg-open)" + (error is null ? string.Empty : " (" + error + ")"));
    }

    private ExecutionResult ProcessKill(IReadOnlyDictionary<string, object> variables)
    {
        string? name = BuiltinActionVariables.GetString(variables, "Name");
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || name.Contains('/', StringComparison.Ordinal))
        {
            return Invalid("Name must be the name of a program without a path.");
        }

        int ended = _processes.Kill(name.Trim());
        return ended == 0
            ? new ExecutionResult(1, string.Empty, $"No process of the user is named '{name.Trim()}'.")
            : Done($"Ended {ended.ToString(CultureInfo.InvariantCulture)} process(es) named '{name.Trim()}'.");
    }

    private async ValueTask<ExecutionResult> NotifyAsync(IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        string? message = BuiltinActionVariables.GetString(variables, NotifyToastSpec.Message);
        if (string.IsNullOrEmpty(message))
        {
            return Invalid("Message is required.");
        }

        ExecutionResult shown = await ShowNotificationAsync(BuiltinActionVariables.GetString(variables, NotifyToastSpec.Title), message, cancellationToken).ConfigureAwait(false);
        bool extras = variables.ContainsKey(NotifyToastSpec.ImageUrl) || variables.ContainsKey(NotifyToastSpec.ButtonId(1));
        return shown.Succeeded && extras
            ? shown with { StandardOutput = shown.StandardOutput + " (Pictures and buttons are not supported on Linux and were left out.)" }
            : shown;
    }
}
