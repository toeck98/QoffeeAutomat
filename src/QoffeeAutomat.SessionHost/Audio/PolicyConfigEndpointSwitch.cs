using System.Globalization;
using System.Runtime.InteropServices;
using static QoffeeAutomat.SessionHost.Audio.CoreAudioInterop;

namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>
/// The <see cref="IDefaultEndpointSwitch"/> of Windows (PLAN.md S11, T713): changes the default audio device through
/// <c>IPolicyConfig</c>, the COM interface that the Sound settings and every audio switcher use. <b>Windows does not document this
/// interface.</b> It has kept the same layout from Windows Vista to Windows 11, but Microsoft may change or remove it in an update,
/// and nothing here can detect that in advance. This is the only class of the product that depends on it. When the interface is
/// missing or Windows refuses the call, <see cref="SetDefault"/> throws an <see cref="AudioDeviceException"/> whose message says so
/// in words, the default device stays as it was, and the audio device sensors, which use the documented Core Audio interfaces,
/// are not affected.
/// </summary>
public sealed class PolicyConfigEndpointSwitch : IDefaultEndpointSwitch
{
    private const string Undocumented =
        "The agent uses an undocumented Windows interface (IPolicyConfig) to change the default audio device, and a Windows update may have changed or restricted it.";

    /// <inheritdoc />
    public void SetDefault(string endpointId, AudioRole role)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        object? policy = null;
        try
        {
            policy = new PolicyConfigClientComObject();
            int result = ((IPolicyConfig)policy).SetDefaultEndpoint(endpointId, RoleOf(role));
            if (result != 0)
            {
                throw new AudioDeviceException("Windows refused the change (error 0x" + Hex(result) + "). " + Undocumented);
            }
        }
        catch (COMException exception)
        {
            throw new AudioDeviceException("The Windows call failed (error 0x" + Hex(exception.HResult) + "). " + Undocumented, exception);
        }
        catch (InvalidCastException exception)
        {
            throw new AudioDeviceException("This Windows does not offer the interface that is needed. " + Undocumented, exception);
        }
        finally
        {
            Release(policy);
        }
    }

    private static string Hex(int value) => value.ToString("X8", CultureInfo.InvariantCulture);
}
