namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>The direction of an audio device (PLAN.md S11, T713).</summary>
public enum AudioFlow
{
    /// <summary>A playback device (speakers, headphones): the metric <c>audio.output_device</c>.</summary>
    Output,

    /// <summary>A recording device (microphone): the metric <c>audio.input_device</c>.</summary>
    Input,
}

/// <summary>The roles in which Windows keeps a default device.</summary>
public enum AudioRole
{
    /// <summary>The default device for games, system sounds and most programs (<c>eConsole</c>). This is the one the metrics report.</summary>
    Console,

    /// <summary>The default device for music and video (<c>eMultimedia</c>).</summary>
    Multimedia,

    /// <summary>The default communication device (<c>eCommunications</c>), used by calling programs.</summary>
    Communications,
}

/// <summary>An active audio endpoint as the operating system reports it.</summary>
/// <param name="Id">The endpoint id (a string such as <c>{0.0.0.00000000}.{guid}</c>); stable for a device and never shown to the user.</param>
/// <param name="Name">The friendly name that the Sound settings show, for example <c>Speakers (Realtek Audio)</c>; may be empty.</param>
public sealed record AudioEndpoint(string Id, string Name);

/// <summary>An active audio device with the label that the metrics and the action use.</summary>
/// <param name="Id">The endpoint id.</param>
/// <param name="Label">The friendly name, made unique among the devices of the same direction (<c>Name #2</c> for a second device with the same name).</param>
public sealed record AudioDevice(string Id, string Label);

/// <summary>What the PC has of one direction: the active devices and which of them is the default.</summary>
/// <param name="Devices">The active devices, sorted by label.</param>
/// <param name="DefaultId">The id of the default device; null when there is none or it is not active.</param>
public sealed record AudioSnapshot(IReadOnlyList<AudioDevice> Devices, string? DefaultId)
{
    /// <summary>A snapshot without devices.</summary>
    public static AudioSnapshot Empty { get; } = new([], null);

    /// <summary>The labels of the devices, the options of the metric.</summary>
    public IReadOnlyList<string> Labels => [.. Devices.Select(device => device.Label)];

    /// <summary>The label of the default device; null when there is none.</summary>
    public string? DefaultLabel => Devices.FirstOrDefault(device => string.Equals(device.Id, DefaultId, StringComparison.Ordinal))?.Label;
}

/// <summary>
/// The default device cannot be changed, or the audio stack cannot be used. The message is meant for the user: it says what
/// went wrong in words and never contains more than the reason.
/// </summary>
public sealed class AudioDeviceException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public AudioDeviceException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public AudioDeviceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public AudioDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The documented part of the audio devices (Core Audio, <c>IMMDeviceEnumerator</c> and <c>IMMNotificationClient</c>): which devices
/// are active, which one is the default and when that changes. The seam between <see cref="AudioDeviceService"/> and Windows;
/// tests use a fake. The real implementation is <see cref="WindowsAudioEndpointCatalog"/>.
/// </summary>
public interface IAudioEndpointCatalog
{
    /// <summary>Lists the active devices of one direction (not unplugged, not disabled).</summary>
    /// <param name="flow">The direction.</param>
    /// <returns>The endpoints; empty when there are none or the audio service does not run. The method does not throw.</returns>
    IReadOnlyList<AudioEndpoint> GetActiveEndpoints(AudioFlow flow);

    /// <summary>Reads the default device of the role <see cref="AudioRole.Console"/>.</summary>
    /// <param name="flow">The direction.</param>
    /// <returns>The endpoint id; null when there is no default device. The method does not throw.</returns>
    string? GetDefaultEndpointId(AudioFlow flow);

    /// <summary>
    /// Asks to be told when a device appears, disappears, changes its state or becomes the default. The callback may run on any
    /// thread and several times for one plug event; it must return quickly.
    /// </summary>
    /// <param name="changed">Called after something about the devices changed.</param>
    /// <returns>Ends the subscription when disposed.</returns>
    /// <exception cref="AudioDeviceException">The audio service did not accept the registration.</exception>
    IDisposable Subscribe(Action changed);
}

/// <summary>
/// Changes the default device. This is the one place that depends on an interface of Windows that is widely used but not
/// documented (<c>IPolicyConfig</c>, see <see cref="PolicyConfigEndpointSwitch"/>), so it has an interface of its own: when
/// Windows changes it, only this implementation breaks, the action fails with a clear message and everything else keeps working.
/// </summary>
public interface IDefaultEndpointSwitch
{
    /// <summary>Makes a device the default device of one role.</summary>
    /// <param name="endpointId">The endpoint id, from <see cref="IAudioEndpointCatalog.GetActiveEndpoints"/>.</param>
    /// <param name="role">The role.</param>
    /// <exception cref="AudioDeviceException">Windows refused, or the interface is not available; the message says which.</exception>
    void SetDefault(string endpointId, AudioRole role);
}
