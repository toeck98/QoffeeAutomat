using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.SessionHost.Metrics;

/// <summary>
/// The <see cref="IUserMetricProbe"/> of a Windows session (PLAN.md S06, T305): idle time from <c>GetLastInputInfo</c>, the
/// foreground window, master volume and mute from Core Audio, microphone and webcam use from the capability access
/// manager, and the displays. Every reader falls back to "unavailable" (null) when Windows has nothing to say, for example
/// no audio device, a locked desktop without a foreground window, or a registry key that does not exist.
/// </summary>
public sealed class WindowsUserMetricProbe : IUserMetricProbe
{
    private const int MonitorCountMetric = 80;
    private const int ScreenWidthMetric = 0;
    private const int ScreenHeightMetric = 1;
    private const int DesktopVerticalResolution = 117;
    private const int DesktopHorizontalResolution = 118;
    private const int MaxTitleLength = 512;

    private readonly CapabilityAccess _capabilities;
    private readonly Func<AudioEndpointState?> _audio;

    /// <summary>Creates the probe for the current user.</summary>
    public WindowsUserMetricProbe()
        : this(
            new CapabilityAccess(new CurrentUserRegistry(), BootTime),
            CoreAudioEndpoint.TryRead)
    {
    }

    /// <summary>Creates the probe with its own sources, for tests.</summary>
    /// <param name="capabilities">Reads the microphone and webcam use.</param>
    /// <param name="audio">Reads the default playback device.</param>
    public WindowsUserMetricProbe(CapabilityAccess capabilities, Func<AudioEndpointState?> audio)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(audio);

        _capabilities = capabilities;
        _audio = audio;
    }

    /// <inheritdoc />
    public object? Read(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        try
        {
            return metricId switch
            {
                SessionMetricIds.IdleSeconds => ReadIdleSeconds(),
                SessionMetricIds.WindowTitle => ReadForegroundWindow().Title,
                SessionMetricIds.WindowProcess => ReadForegroundWindow().Process,
                SessionMetricIds.Volume => _audio()?.VolumePercent,
                SessionMetricIds.Muted => _audio()?.IsMuted,
                SessionMetricIds.MicrophoneInUse => _capabilities.IsInUse(CapabilityAccess.Microphone),
                SessionMetricIds.WebcamInUse => _capabilities.IsInUse(CapabilityAccess.Webcam),
                SessionMetricIds.DisplayCount => ReadDisplayCount(),
                SessionMetricIds.PrimaryResolution => ReadPrimaryResolution(),
                _ => null,
            };
        }
#pragma warning disable CA1031 // A reader that fails makes its metric unavailable.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static DateTimeOffset BootTime()
    {
        return DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    private static double? ReadIdleSeconds()
    {
        // The structure is two 32-bit integers: its size and the tick count of the last input, which the call fills in.
        var info = new LastInputInfo { Size = 8, Time = 0 };
        if (!GetLastInputInfo(ref info))
        {
            return null;
        }

        // Both values are 32-bit tick counts that wrap around after 49.7 days; the difference stays right.
        uint now = unchecked((uint)Environment.TickCount64);
        uint idleMilliseconds = unchecked(now - info.Time);
        return Math.Floor(idleMilliseconds / 1000.0);
    }

    private static (string? Title, string? Process) ReadForegroundWindow()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return (null, null);
        }

        string? title = null;
        int length = GetWindowTextLengthW(window);
        if (length > 0)
        {
            var buffer = new char[Math.Min(length, MaxTitleLength) + 1];
            int copied = GetWindowTextW(window, buffer, buffer.Length);
            title = copied > 0 ? new string(buffer, 0, copied) : null;
        }

        string? process = null;
        if (GetWindowThreadProcessId(window, out uint processId) != 0)
        {
            try
            {
                using Process owner = Process.GetProcessById((int)processId);
                process = owner.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                process = null;
            }
        }

        return (title, process);
    }

    private static double? ReadDisplayCount()
    {
        int count = GetSystemMetrics(MonitorCountMetric);
        return count > 0 ? count : null;
    }

    private static string? ReadPrimaryResolution()
    {
        // The device capabilities report the real pixels of the primary display, also for a process that Windows scales
        // because it is not DPI aware (GetSystemMetrics would report the scaled size).
        int width = 0;
        int height = 0;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen != IntPtr.Zero)
        {
            try
            {
                width = GetDeviceCaps(screen, DesktopHorizontalResolution);
                height = GetDeviceCaps(screen, DesktopVerticalResolution);
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }

        if (width <= 0 || height <= 0)
        {
            width = GetSystemMetrics(ScreenWidthMetric);
            height = GetSystemMetrics(ScreenHeightMetric);
        }

        return width > 0 && height > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{width}x{height}")
            : null;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextLengthW(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextW(IntPtr window, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);

#pragma warning disable CS0649 // The fields of the native structure are filled in by Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }
#pragma warning restore CS0649
}
