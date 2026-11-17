using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// The built-in action <c>notify.toast</c> (PLAN.md S11, S16, T702): a toast notification on the desktop of the user with up to
/// three buttons. The names, limits and parameters are defined once here, because three places need them and cannot reference
/// each other: the Windows action catalog of the agent, the provider of the SessionHost and the system command that announces
/// the action to Home Assistant (<c>SystemCommands.Notify</c> in Core). A test keeps the catalog and the provider equal.
/// </summary>
public static class NotifyToastSpec
{
    /// <summary>The name of the action.</summary>
    public const string ActionName = "notify.toast";

    /// <summary>The title of the action in the editor.</summary>
    public const string ActionTitle = "Show notification";

    /// <summary>The description of the action.</summary>
    public const string ActionDescription =
        "Shows a toast notification on the desktop of the user, with an optional picture and up to three buttons; a button press raises the event notification_action.";

    /// <summary>The title of the notification (optional).</summary>
    public const string Title = "Title";

    /// <summary>The text of the notification.</summary>
    public const string Message = "Message";

    /// <summary>An <c>http</c> or <c>https</c> address of a picture; the agent fetches it with the limits below.</summary>
    public const string ImageUrl = "ImageUrl";

    /// <summary>A name that is passed on with every button press as <c>tag</c>, so that an automation can tell notifications apart. A new notification with the same tag replaces the old one.</summary>
    public const string Tag = "Tag";

    /// <summary>
    /// The picture, base64 encoded, as the agent hands it to the SessionHost. It is <b>not</b> a parameter of the action: the agent
    /// adds it after it fetched <see cref="ImageUrl"/> and removes the address, and the SessionHost ignores every other source of
    /// this name. It is never part of a command document.
    /// </summary>
    public const string ImageData = "ImageData";

    /// <summary>The most buttons a notification can have.</summary>
    public const int MaxButtons = 3;

    /// <summary>The longest title.</summary>
    public const int MaxTitleLength = 128;

    /// <summary>The longest text.</summary>
    public const int MaxMessageLength = 1024;

    /// <summary>The longest picture address.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The longest id of a button or tag.</summary>
    public const int MaxIdLength = 64;

    /// <summary>The longest label of a button.</summary>
    public const int MaxButtonTitleLength = 64;

    /// <summary>The largest picture the agent accepts, in bytes.</summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>The pattern of a button id and of a tag: letters, digits and <c>_ . : -</c>.</summary>
    public const string IdPattern = @"^[A-Za-z0-9_.:-]{1,64}$";

    /// <summary>The name of the id parameter of a button.</summary>
    /// <param name="number">The button, 1 to <see cref="MaxButtons"/>.</param>
    /// <returns>The parameter name, for example <c>Action1Id</c>.</returns>
    public static string ButtonId(int number) => "Action" + number.ToString(CultureInfo.InvariantCulture) + "Id";

    /// <summary>The name of the label parameter of a button.</summary>
    /// <param name="number">The button, 1 to <see cref="MaxButtons"/>.</param>
    /// <returns>The parameter name, for example <c>Action1Title</c>.</returns>
    public static string ButtonTitle(int number) => "Action" + number.ToString(CultureInfo.InvariantCulture) + "Title";

    /// <summary>The parameters of the action, in the order the editor shows them.</summary>
    public static IReadOnlyList<ParameterDefinition> Parameters { get; } = BuildParameters();

    /// <summary>
    /// Recognises a picture that a notification may show by its first bytes (PNG, JPEG, GIF, BMP), whatever its name or content
    /// type claims. The agent and the SessionHost both check, because the SessionHost trusts nothing it did not verify itself.
    /// </summary>
    /// <param name="data">The beginning of the file.</param>
    /// <returns>The file extension with the dot (<c>.png</c>, <c>.jpg</c>, <c>.gif</c>, <c>.bmp</c>); null for anything else.</returns>
    public static string? DetectImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ".png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ".jpg";
        }

        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8'
            && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
        {
            return ".gif";
        }

        if (data.Length >= 14 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return ".bmp";
        }

        return null;
    }

    /// <summary>Tells whether a text is usable as the id of a button or as a tag: 1 to <see cref="MaxIdLength"/> letters, digits and <c>_ . : -</c>.</summary>
    /// <param name="value">The text.</param>
    /// <returns>True when the text matches <see cref="IdPattern"/>.</returns>
    public static bool IsValidId([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool allowed = (character is >= 'a' and <= 'z') || (character is >= 'A' and <= 'Z') || (character is >= '0' and <= '9')
                || character is '_' or '.' or ':' or '-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static ParameterDefinition[] BuildParameters()
    {
        var parameters = new List<ParameterDefinition>
        {
            new(Title, ParameterTypes.Text, MaxLength: MaxTitleLength, Description: "The title of the notification."),
            new(Message, ParameterTypes.Text, Required: true, MaxLength: MaxMessageLength, Description: "The text of the notification."),
            new(
                ImageUrl,
                ParameterTypes.Text,
                MaxLength: MaxUrlLength,
                Description: "An http or https address of a picture (PNG, JPEG, GIF or BMP, at most 1 MiB) that the agent fetches and shows."),
            new(
                Tag,
                ParameterTypes.Text,
                Pattern: IdPattern,
                MaxLength: MaxIdLength,
                Description: "A name that comes back with every button press as the event data tag; a new notification with the same tag replaces the old one."),
        };

        for (int number = 1; number <= MaxButtons; number++)
        {
            string label = number.ToString(CultureInfo.InvariantCulture);
            parameters.Add(new ParameterDefinition(
                ButtonId(number),
                ParameterTypes.Text,
                Pattern: IdPattern,
                MaxLength: MaxIdLength,
                Description: "The id of button " + label + " that comes back in the event notification_action (letters, digits and _ . : -)."));
            parameters.Add(new ParameterDefinition(
                ButtonTitle(number),
                ParameterTypes.Text,
                MaxLength: MaxButtonTitleLength,
                Description: "The label of button " + label + "; a button needs an id and a label."));
        }

        return [.. parameters];
    }
}

/// <summary>
/// The built-in action <c>media.control</c> (PLAN.md S11, T716): controls the current system media session, with the media keys
/// as a fallback. Defined once for the same reason as <see cref="NotifyToastSpec"/>.
/// </summary>
public static class MediaControlSpec
{
    /// <summary>The name of the action.</summary>
    public const string ActionName = "media.control";

    /// <summary>The title of the action in the editor.</summary>
    public const string ActionTitle = "Control media playback";

    /// <summary>The description of the action.</summary>
    public const string ActionDescription =
        "Sends play, pause, play_pause, next, previous or stop to the current system media session; without a session it presses the media key.";

    /// <summary>The name of its only parameter.</summary>
    public const string ActionParameter = "Action";

    /// <summary>Starts playback.</summary>
    public const string Play = "play";

    /// <summary>Pauses playback.</summary>
    public const string Pause = "pause";

    /// <summary>Toggles between playing and paused.</summary>
    public const string PlayPause = "play_pause";

    /// <summary>Skips to the next track.</summary>
    public const string Next = "next";

    /// <summary>Goes to the previous track.</summary>
    public const string Previous = "previous";

    /// <summary>Stops playback.</summary>
    public const string Stop = "stop";

    /// <summary>All values, in the order the editor shows them.</summary>
    public static IReadOnlyList<string> Actions { get; } = [Play, Pause, PlayPause, Next, Previous, Stop];

    /// <summary>The parameters of the action.</summary>
    public static IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new ParameterDefinition(
            ActionParameter,
            ParameterTypes.Choice,
            Required: true,
            Options: Actions,
            Description: "What to do with the current media session."),
    ];
}

/// <summary>
/// The built-in action <c>agent.update</c> (PLAN.md S09, S13, S16, T704): downloads the installer of the newest release, verifies it and
/// runs it silently. Defined once for the same reason as <see cref="NotifyToastSpec"/>: the agent, which executes the action, and the
/// system command <c>agent_update</c> in Core, which announces it to Home Assistant, cannot reference each other.
/// </summary>
public static class AgentUpdateSpec
{
    /// <summary>The name of the action.</summary>
    public const string ActionName = "agent.update";

    /// <summary>The title of the action in the editor.</summary>
    public const string ActionTitle = "Update the agent";

    /// <summary>The description of the action.</summary>
    public const string ActionDescription =
        "Downloads the installer of the newest release from GitHub, checks its SHA-256 checksum (and its Authenticode signature when this build is signed) and runs it silently. The agent says bye with the reason update first and restarts.";

    /// <summary>The action has no parameters: it always installs the newest release.</summary>
    public static IReadOnlyList<ParameterDefinition> Parameters { get; } = [];
}

/// <summary>
/// The built-in actions <c>audio.set_default_output</c> and <c>audio.set_default_input</c> (PLAN.md S11, S16, T713): make a playback
/// or recording device the default one of the user. Defined once for the same reason as <see cref="NotifyToastSpec"/>: the Windows
/// action catalog of the agent and the provider of the SessionHost cannot reference each other. The variable <c>Device</c> is
/// matched against the names of the active devices: an exact name first, otherwise a name that is the only one to contain the
/// text, ignoring case. A name that matches no device or several devices is the error <c>invalid_vars</c> and changes nothing.
/// </summary>
public static class AudioDeviceSpec
{
    /// <summary>The name of the action that sets the default playback device.</summary>
    public const string SetDefaultOutput = "audio.set_default_output";

    /// <summary>The name of the action that sets the default recording device.</summary>
    public const string SetDefaultInput = "audio.set_default_input";

    /// <summary>The name of the variable.</summary>
    public const string Device = "Device";

    /// <summary>The longest device name that is accepted.</summary>
    public const int MaxDeviceLength = 256;

    /// <summary>The title of <see cref="SetDefaultOutput"/> in the editor.</summary>
    public const string OutputTitle = "Set default audio output";

    /// <summary>The title of <see cref="SetDefaultInput"/> in the editor.</summary>
    public const string InputTitle = "Set default audio input";

    /// <summary>The description of <see cref="SetDefaultOutput"/>.</summary>
    public const string OutputDescription =
        "Makes a playback device the default device and the default communication device. The name is an exact device name or a part of one that fits a single device. Windows has no documented interface for this; the agent uses the one that audio switchers use.";

    /// <summary>The description of <see cref="SetDefaultInput"/>.</summary>
    public const string InputDescription =
        "Makes a recording device the default device and the default communication device. The name is an exact device name or a part of one that fits a single device. Windows has no documented interface for this; the agent uses the one that audio switchers use.";

    /// <summary>The variables of <see cref="SetDefaultOutput"/>.</summary>
    public static IReadOnlyList<ParameterDefinition> OutputParameters { get; } = [DeviceParameter("playback")];

    /// <summary>The variables of <see cref="SetDefaultInput"/>.</summary>
    public static IReadOnlyList<ParameterDefinition> InputParameters { get; } = [DeviceParameter("recording")];

    private static ParameterDefinition DeviceParameter(string kind)
    {
        return new ParameterDefinition(
            Device,
            ParameterTypes.Text,
            Required: true,
            MaxLength: MaxDeviceLength,
            Description: "The name of the " + kind + " device, or a part of it that fits only one active device (case is ignored).");
    }
}
