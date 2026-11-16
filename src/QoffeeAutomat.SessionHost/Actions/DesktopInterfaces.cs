using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.SessionHost.Actions;

/// <summary>
/// The Windows calls behind the desktop actions of the SessionHost (PLAN.md S11, T306): the seam that lets the tests cover
/// lock, log off, launching and the rest without touching the desktop that runs them. The real implementation is
/// <see cref="WindowsDesktopNativeApi"/>. A call that fails throws <see cref="System.ComponentModel.Win32Exception"/>,
/// <see cref="InvalidOperationException"/> or <see cref="IOException"/>; it returns when the request was made.
/// </summary>
public interface IDesktopNativeApi
{
    /// <summary>Locks the session.</summary>
    void LockWorkstation();

    /// <summary>Logs the user off.</summary>
    /// <param name="force">Close programs that do not respond instead of waiting for them.</param>
    void LogOff(bool force);

    /// <summary>Turns the displays off.</summary>
    void TurnDisplayOff();

    /// <summary>Presses and releases a media key.</summary>
    /// <param name="key">One of the media key names of the <c>media.key</c> action: <c>play_pause</c>, <c>next</c>, <c>previous</c> or <c>stop</c>.</param>
    void SendMediaKey(string key);

    /// <summary>Starts a program or opens a document through the shell, in the working directory of the file.</summary>
    /// <param name="path">The full path, already checked by <see cref="LaunchPolicy"/>.</param>
    /// <param name="arguments">The arguments, or null.</param>
    void Launch(string path, string? arguments);

    /// <summary>Opens a web address in the default browser.</summary>
    /// <param name="url">An http or https address, already checked by <see cref="LaunchPolicy"/>.</param>
    void OpenUrl(Uri url);
}

/// <summary>The master volume of the default playback device; the seam in front of <see cref="CoreAudioEndpoint"/>.</summary>
public interface IAudioEndpoint
{
    /// <summary>Reads the state.</summary>
    /// <returns>The state; null when there is no usable device.</returns>
    AudioEndpointState? Read();

    /// <summary>Sets the master volume.</summary>
    /// <param name="percent">0 to 100.</param>
    /// <returns>False when there is no usable device.</returns>
    bool TrySetVolume(double percent);

    /// <summary>Mutes or unmutes.</summary>
    /// <param name="muted">True to mute.</param>
    /// <returns>False when there is no usable device.</returns>
    bool TrySetMute(bool muted);
}

/// <summary>Finds and ends the processes of this session; the seam in front of <c>System.Diagnostics.Process</c>.</summary>
public interface IProcessControl
{
    /// <summary>The id of the SessionHost itself, which no action may end.</summary>
    int CurrentProcessId { get; }

    /// <summary>Lists the processes of this session that have an image name.</summary>
    /// <param name="name">The name without path and without <c>.exe</c>.</param>
    /// <returns>The process ids.</returns>
    IReadOnlyList<int> FindByName(string name);

    /// <summary>Ends a process together with its child processes and waits for it.</summary>
    /// <param name="processId">The process.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>Null when the process is gone, otherwise why it could not be ended.</returns>
    Task<string?> KillTreeAsync(int processId, CancellationToken cancellationToken);
}
