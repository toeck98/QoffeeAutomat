using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QoffeeAutomat.Platform.Windows.Actions.Power;

/// <summary>
/// The <see cref="IPowerRequestApi"/> of a Windows computer: a power request (<c>PowerCreateRequest</c>, <c>PowerSetRequest</c>) of
/// the type system required, and display required when the screen is to stay on as well. The request belongs to the agent process
/// and appears under its name in <c>powercfg /requests</c>; Windows releases it when the process ends, so a restart of the agent
/// never leaves the computer held awake. Only the human check of T715 and the production wiring use this class; the tests use a
/// fake.
/// </summary>
public sealed class WindowsPowerRequestApi : IPowerRequestApi, IDisposable
{
    private const int RequestDisplayRequired = 0;
    private const int RequestSystemRequired = 1;
    private const uint ContextVersion = 0;
    private const uint ContextFlagSimpleString = 0x00000001;
    private const string Reason = "QoffeeAutomat keep awake";

    private readonly object _gate = new();
    private IntPtr _request = IntPtr.Zero;
    private IntPtr _reason = IntPtr.Zero;
    private bool _displaySet;

    /// <inheritdoc />
    public void Acquire(bool keepDisplayOn)
    {
        lock (_gate)
        {
            bool created = _request == IntPtr.Zero;
            if (created)
            {
                Create();
            }

            try
            {
                if (created)
                {
                    SetRequest(RequestSystemRequired);
                }

                if (keepDisplayOn && !_displaySet)
                {
                    SetRequest(RequestDisplayRequired);
                    _displaySet = true;
                }
                else if (!keepDisplayOn && _displaySet)
                {
                    _ = NativeMethods.PowerClearRequest(_request, RequestDisplayRequired);
                    _displaySet = false;
                }
            }
            catch (Win32Exception)
            {
                ReleaseLocked();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Release()
    {
        lock (_gate)
        {
            ReleaseLocked();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Release();

    private void Create()
    {
        _reason = Marshal.StringToHGlobalUni(Reason);
        var context = new NativeMethods.ReasonContext
        {
            Version = ContextVersion,
            Flags = ContextFlagSimpleString,
            SimpleReasonString = _reason,
        };

        IntPtr request = NativeMethods.PowerCreateRequest(ref context);
        if (request == IntPtr.Zero || request == new IntPtr(-1))
        {
            int error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(_reason);
            _reason = IntPtr.Zero;
            throw new Win32Exception(error);
        }

        _request = request;
    }

    private void SetRequest(int type)
    {
        if (!NativeMethods.PowerSetRequest(_request, type))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private void ReleaseLocked()
    {
        if (_request != IntPtr.Zero)
        {
            // Closing the handle ends the request, so the requests need not be cleared one by one.
            _ = NativeMethods.CloseHandle(_request);
            _request = IntPtr.Zero;
        }

        if (_reason != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_reason);
            _reason = IntPtr.Zero;
        }

        _displaySet = false;
    }

#pragma warning disable CS0649 // The fields of the native structure are read by Windows.
    private static class NativeMethods
    {
        /// <summary>
        /// <c>REASON_CONTEXT</c>: the version, the flags and a union whose first member is the simple reason string and whose largest
        /// member (the detailed reason) has the other fields; the padding keeps the size of the union.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ReasonContext
        {
            public uint Version;
            public uint Flags;
            public IntPtr SimpleReasonString;
            public uint LocalizedReasonId;
            public uint ReasonStringCount;
            public IntPtr ReasonStrings;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr PowerCreateRequest(ref ReasonContext context);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PowerSetRequest(IntPtr request, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PowerClearRequest(IntPtr request, int requestType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
#pragma warning restore CS0649
}
