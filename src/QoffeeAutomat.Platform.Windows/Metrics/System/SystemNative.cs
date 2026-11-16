using System.Runtime.InteropServices;

namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>The Win32 calls of the system metrics (PLAN.md T304). Everything that can fail is wrapped by <see cref="WindowsSystemSources"/>.</summary>
internal static class SystemNative
{
    public const byte NoBattery = 128;
    public const byte Charging = 8;
    public const byte UnknownBatteryFlag = 255;
    public const byte UnknownBatteryLife = 255;

    /// <summary>Reads the cumulative processor times as 100 ns units (a FILETIME is two 32-bit halves, which is one 64-bit integer).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

#pragma warning disable CS0649 // The fields of the native structures are filled in by Windows.
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
#pragma warning restore CS0649
}
