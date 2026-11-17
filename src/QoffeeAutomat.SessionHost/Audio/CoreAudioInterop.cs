using System.Runtime.InteropServices;

namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>
/// The COM declarations for the audio devices (PLAN.md S11, T713): the documented Core Audio interfaces that list devices and
/// report changes, and the undocumented <c>IPolicyConfig</c> that changes the default device. Only the layout of the interfaces
/// matters for COM, so the methods that are not called are declared with plain pointers; their order must not change.
/// </summary>
internal static class CoreAudioInterop
{
    /// <summary><c>EDataFlow.eRender</c>.</summary>
    public const int RenderFlow = 0;

    /// <summary><c>EDataFlow.eCapture</c>.</summary>
    public const int CaptureFlow = 1;

    /// <summary><c>DEVICE_STATE_ACTIVE</c>: the device is present and enabled.</summary>
    public const uint DeviceStateActive = 1;

    /// <summary><c>VT_LPWSTR</c>, the type of the friendly name in a <see cref="PropVariant"/>.</summary>
    public const ushort VariantTypeString = 31;

    /// <summary><c>STGM_READ</c>.</summary>
    public const uint StorageRead = 0;

    /// <summary>PKEY_Device_FriendlyName: the name that the Sound settings show.</summary>
    public static PropertyKey FriendlyName => new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>Maps the direction to <c>EDataFlow</c>.</summary>
    /// <param name="flow">The direction.</param>
    /// <returns>The value for the Core Audio calls.</returns>
    public static int FlowOf(AudioFlow flow) => flow == AudioFlow.Output ? RenderFlow : CaptureFlow;

    /// <summary>Maps the role to <c>ERole</c>.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The value for the Core Audio calls.</returns>
    public static int RoleOf(AudioRole role)
    {
        return role switch
        {
            AudioRole.Console => 0,
            AudioRole.Multimedia => 1,
            _ => 2,
        };
    }

    /// <summary>Releases a COM object that was obtained through the interop layer.</summary>
    /// <param name="comObject">The object; null and managed objects are ignored.</param>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal sealed class MMDeviceEnumeratorComObject
    {
    }

    /// <summary>The class of the policy configuration, the object that every audio switcher talks to (undocumented).</summary>
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal sealed class PolicyConfigClientComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection? devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? result);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore? properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    /// <summary>
    /// The callback interface of Core Audio. It is implemented in managed code and handed to Windows, so it is not a
    /// <c>ComImport</c> interface; the methods are called on a thread of the audio service and must return at once.
    /// </summary>
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    /// <summary>
    /// <c>IPolicyConfig</c> (undocumented): the interface that the Sound settings and every audio switcher use to change the
    /// default device. Windows does not publish it; the layout below is the one that has been stable from Windows Vista to
    /// Windows 11. Only <see cref="SetDefaultEndpoint"/> is called; the methods before it are placeholders for the layout.
    /// </summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(IntPtr deviceName, IntPtr format);

        [PreserveSig]
        int GetDeviceFormat(IntPtr deviceName, int defaultFormat, IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat(IntPtr deviceName);

        [PreserveSig]
        int SetDeviceFormat(IntPtr deviceName, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(IntPtr deviceName, int defaultPeriod, IntPtr defaultValue, IntPtr minimumValue);

        [PreserveSig]
        int SetProcessingPeriod(IntPtr deviceName, IntPtr period);

        [PreserveSig]
        int GetShareMode(IntPtr deviceName, IntPtr mode);

        [PreserveSig]
        int SetShareMode(IntPtr deviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(IntPtr deviceName, int effectsStore, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetPropertyValue(IntPtr deviceName, int effectsStore, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, int role);

        [PreserveSig]
        int SetEndpointVisibility(IntPtr deviceName, int visible);
    }

    /// <summary><c>PROPERTYKEY</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey
    {
        /// <summary>Creates a key.</summary>
        /// <param name="formatId">The property set.</param>
        /// <param name="propertyId">The property in the set.</param>
        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        /// <summary>The property set.</summary>
        public Guid FormatId { get; }

        /// <summary>The property in the set.</summary>
        public uint PropertyId { get; }
    }

    /// <summary><c>PROPVARIANT</c>: the type and the first pointer-sized member of the union, which is all that a string needs.</summary>
#pragma warning disable CS0649, CS0169 // The fields are filled in by Windows; the reserved ones and the second half of the union are never read here.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        /// <summary>The type, <see cref="VariantTypeString"/> for a string.</summary>
        public ushort Type;

        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;

        /// <summary>The string pointer for a string.</summary>
        public IntPtr Data;

        // The union is two pointers wide on every platform; the second one is never used here.
        private IntPtr _data2;
    }
#pragma warning restore CS0649, CS0169

    /// <summary>The native functions that the audio devices need.</summary>
    internal static class NativeMethods
    {
        [DllImport("ole32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int PropVariantClear(ref PropVariant value);
    }
}
