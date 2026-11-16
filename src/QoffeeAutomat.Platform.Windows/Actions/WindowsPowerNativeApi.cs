using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QoffeeAutomat.Platform.Windows.Actions;

/// <summary>
/// The <see cref="IPowerNativeApi"/> of a Windows computer: <c>ExitWindowsEx</c> for shutdown and restart,
/// <c>SetSuspendState</c> for sleep and hibernate. The calls need <c>SeShutdownPrivilege</c>, which LocalSystem has but
/// keeps disabled until it is asked for. This class really turns the computer off; only the human check of T306 and
/// the production wiring use it, the tests use a fake.
/// </summary>
public sealed class WindowsPowerNativeApi : IPowerNativeApi
{
    private const uint ExitPowerOff = 0x00000008;
    private const uint ExitReboot = 0x00000002;
    private const uint ExitForce = 0x00000004;
    private const uint ExitForceIfHung = 0x00000010;

    // SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_MINOR_OTHER | SHTDN_REASON_FLAG_PLANNED
    private const uint PlannedReason = 0x80000000;

    private const uint TokenAdjustPrivileges = 0x00000020;
    private const uint TokenQuery = 0x00000008;
    private const uint PrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private const string ShutdownPrivilege = "SeShutdownPrivilege";

    /// <summary>How long a suspend request may take to return before it counts as accepted (the call blocks until the computer wakes up).</summary>
    private static readonly TimeSpan SuspendReturnWait = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public bool CanSleep => NativeMethods.IsPwrSuspendAllowed();

    /// <inheritdoc />
    public bool CanHibernate => NativeMethods.IsPwrHibernateAllowed();

    /// <inheritdoc />
    public void Shutdown(bool force)
    {
        Exit(ExitPowerOff | (force ? ExitForce : ExitForceIfHung));
    }

    /// <inheritdoc />
    public void Restart(bool force)
    {
        Exit(ExitReboot | (force ? ExitForce : ExitForceIfHung));
    }

    /// <inheritdoc />
    public void Sleep()
    {
        Suspend(hibernate: false);
    }

    /// <inheritdoc />
    public void Hibernate()
    {
        Suspend(hibernate: true);
    }

    private static void Exit(uint flags)
    {
        EnableShutdownPrivilege();
        if (!NativeMethods.ExitWindowsEx(flags, PlannedReason))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void Suspend(bool hibernate)
    {
        EnableShutdownPrivilege();

        // SetSuspendState returns only after the computer woke up again, so it runs on its own thread. A call that
        // fails returns at once; one that is still blocked after a short while has put the computer to sleep.
        bool succeeded = true;
        int error = 0;
        var thread = new Thread(() =>
        {
            succeeded = NativeMethods.SetSuspendState(hibernate, forceCritical: false, disableWakeEvent: false);
            if (!succeeded)
            {
                error = Marshal.GetLastPInvokeError();
            }
        })
        {
            IsBackground = true,
            Name = hibernate ? "QoffeeAutomat.Hibernate" : "QoffeeAutomat.Sleep",
        };
        thread.Start();

        if (thread.Join(SuspendReturnWait) && !succeeded)
        {
            throw error == 0
                ? new InvalidOperationException("Windows refused the request to change the power state.")
                : new Win32Exception(error);
        }
    }

    private static void EnableShutdownPrivilege()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, ShutdownPrivilege, out NativeMethods.Luid luid))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var privileges = new NativeMethods.TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = PrivilegeEnabled,
            };

            if (!NativeMethods.AdjustTokenPrivileges(token, disableAllPrivileges: false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // The call succeeds even when the privilege is not held; only the last error tells.
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotAllAssigned)
            {
                throw new Win32Exception(error);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

#pragma warning disable CS0649 // The fields of the native structures are filled in by Windows.
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(
            IntPtr token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TokenPrivileges newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ExitWindowsEx(uint flags, uint reason);

        [DllImport("powrprof.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool SetSuspendState(
            [MarshalAs(UnmanagedType.U1)] bool hibernate,
            [MarshalAs(UnmanagedType.U1)] bool forceCritical,
            [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

        [DllImport("powrprof.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool IsPwrSuspendAllowed();

        [DllImport("powrprof.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool IsPwrHibernateAllowed();
    }
}
