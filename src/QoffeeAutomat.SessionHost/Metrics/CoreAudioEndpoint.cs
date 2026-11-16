using System.Runtime.InteropServices;

namespace QoffeeAutomat.SessionHost.Metrics;

/// <summary>The state of the default playback device.</summary>
/// <param name="VolumePercent">The master volume, 0 to 100.</param>
/// <param name="IsMuted">Whether the device is muted.</param>
public readonly record struct AudioEndpointState(double VolumePercent, bool IsMuted);

/// <summary>
/// Reads the master volume and mute state of the default playback device through Core Audio (<c>IMMDeviceEnumerator</c>,
/// <c>IAudioEndpointVolume</c>; PLAN.md S06, T305). Without an audio device, with a stopped audio service or when the
/// interfaces are not available the result is null (unavailable), never an exception.
/// </summary>
public static class CoreAudioEndpoint
{
    private const int RenderDataFlow = 0;
    private const int MultimediaRole = 1;
    private const uint ClsctxAll = 23;

    private static readonly Guid EndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>Reads the default playback device.</summary>
    /// <returns>The state; null when there is no usable device.</returns>
    public static AudioEndpointState? TryRead()
    {
        AudioEndpointState? state = null;
        Use(volume =>
        {
            if (volume.GetMasterVolumeLevelScalar(out float scalar) != 0 || volume.GetMute(out bool muted) != 0)
            {
                return false;
            }

            double percent = Math.Clamp(Math.Round(scalar * 100.0, 1), 0, 100);
            state = new AudioEndpointState(percent, muted);
            return true;
        });
        return state;
    }

    /// <summary>Sets the master volume of the default playback device (T306, <c>audio.set_volume</c>).</summary>
    /// <param name="percent">The volume, 0 to 100; values outside are clamped.</param>
    /// <returns>True when the volume was set; false when there is no usable device.</returns>
    public static bool TrySetVolume(double percent)
    {
        float scalar = (float)(Math.Clamp(percent, 0, 100) / 100.0);
        return Use(volume =>
        {
            Guid eventContext = Guid.Empty;
            return volume.SetMasterVolumeLevelScalar(scalar, ref eventContext) == 0;
        });
    }

    /// <summary>Mutes or unmutes the default playback device (T306, <c>audio.set_mute</c>).</summary>
    /// <param name="muted">True to mute.</param>
    /// <returns>True when the state was set; false when there is no usable device.</returns>
    public static bool TrySetMute(bool muted)
    {
        return Use(volume =>
        {
            Guid eventContext = Guid.Empty;
            return volume.SetMute(muted, ref eventContext) == 0;
        });
    }

    /// <summary>Opens the volume interface of the default playback device, runs the action and releases the COM objects.</summary>
    /// <param name="action">Does the work; returns true when it succeeded.</param>
    /// <returns>The result of the action; false when there is no device or anything in the audio stack fails.</returns>
    private static bool Use(Func<IAudioEndpointVolume, bool> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(RenderDataFlow, MultimediaRole, out device) != 0 || device is null)
            {
                return false;
            }

            Guid iid = EndpointVolumeId;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object? endpointVolume) != 0 || endpointVolume is null)
            {
                return false;
            }

            volume = (IAudioEndpointVolume)endpointVolume;
            return action(volume);
        }
#pragma warning disable CA1031 // Whatever is wrong with the audio stack only makes the audio metrics and actions unavailable.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
        finally
        {
            Release(volume);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // Only the methods up to the one that is used need to be declared, but their order is the layout of the interface.
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? endpointVolume);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDecibels, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDecibels);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDecibels, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDecibels);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
    }
}
