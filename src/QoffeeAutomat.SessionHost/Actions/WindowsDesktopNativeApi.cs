using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.SessionHost.Actions;

/// <summary>
/// The <see cref="IDesktopNativeApi"/> of a Windows desktop: <c>LockWorkStation</c>, <c>ExitWindowsEx</c>, the monitor
/// power broadcast, <c>keybd_event</c> for media keys and the shell for programs and web addresses. It acts on the desktop
/// it runs on (it logs the user off and locks the screen); only the production wiring and the human check of T306 use it.
/// </summary>
public sealed class WindowsDesktopNativeApi : IDesktopNativeApi
{
    private const uint ExitLogOff = 0x00000000;
    private const uint ExitForce = 0x00000004;
    private const uint ExitForceIfHung = 0x00000010;

    // SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED
    private const uint PlannedReason = 0x80000000;

    private const uint WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int MonitorOff = 2;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaStop = 0xB2;
    private const byte VkMediaPlayPause = 0xB3;

    /// <inheritdoc />
    public void LockWorkstation()
    {
        if (!NativeMethods.LockWorkStation())
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <inheritdoc />
    public void LogOff(bool force)
    {
        if (!NativeMethods.ExitWindowsEx(ExitLogOff | (force ? ExitForce : ExitForceIfHung), PlannedReason))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <inheritdoc />
    public void TurnDisplayOff()
    {
        // Posted, not sent: a window that does not answer must not hold the action back.
        if (!NativeMethods.PostMessageW(HwndBroadcast, WmSysCommand, new UIntPtr(ScMonitorPower), new IntPtr(MonitorOff)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <inheritdoc />
    public void SendMediaKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        byte virtualKey = key switch
        {
            "play_pause" => VkMediaPlayPause,
            "next" => VkMediaNextTrack,
            "previous" => VkMediaPreviousTrack,
            "stop" => VkMediaStop,
            _ => throw new InvalidOperationException($"'{key}' is not a media key."),
        };

        NativeMethods.KeyboardEvent(virtualKey, 0, KeyEventExtendedKey, UIntPtr.Zero);
        NativeMethods.KeyboardEvent(virtualKey, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);
    }

    /// <inheritdoc />
    public void Launch(string path, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            info.Arguments = arguments;
        }

        // The shell may hand the document to a program that is running already, which gives no process object.
        using Process? started = Process.Start(info);
    }

    /// <inheritdoc />
    public void OpenUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        using Process? started = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ExitWindowsEx(uint flags, uint reason);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "keybd_event")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void KeyboardEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    }
}

/// <summary>The audio device of this desktop: <see cref="CoreAudioEndpoint"/> behind <see cref="IAudioEndpoint"/>.</summary>
public sealed class CoreAudioEndpointControl : IAudioEndpoint
{
    /// <inheritdoc />
    public AudioEndpointState? Read() => CoreAudioEndpoint.TryRead();

    /// <inheritdoc />
    public bool TrySetVolume(double percent) => CoreAudioEndpoint.TrySetVolume(percent);

    /// <inheritdoc />
    public bool TrySetMute(bool muted) => CoreAudioEndpoint.TrySetMute(muted);
}

/// <summary>The process table of this session: <see cref="Process"/> behind <see cref="IProcessControl"/>.</summary>
public sealed class SystemProcessControl : IProcessControl
{
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public int CurrentProcessId => Environment.ProcessId;

    /// <inheritdoc />
    public IReadOnlyList<int> FindByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        int session;
        using (Process current = Process.GetCurrentProcess())
        {
            session = current.SessionId;
        }

        var ids = new List<int>();
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    // Other users' sessions are not ours to touch; their processes would refuse anyway.
                    if (process.SessionId == session)
                    {
                        ids.Add(process.Id);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    // The process ended while it was inspected.
                }
            }
        }

        return ids;
    }

    /// <inheritdoc />
    public async Task<string?> KillTreeAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(EndTimeout);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return null;
        }
        catch (ArgumentException)
        {
            // Gone already, which is what was asked for.
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"process {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)} did not end in time";
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return exception.Message;
        }
    }
}
