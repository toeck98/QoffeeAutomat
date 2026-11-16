using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>
/// The <see cref="ISystemSources"/> of a real Windows computer (PLAN.md S06, T304): <c>GetSystemTimes</c>,
/// <c>GlobalMemoryStatusEx</c>, <c>DriveInfo</c>, the interface statistics of the network stack,
/// <c>GetSystemPowerStatus</c>, the GPU Engine performance counters and a few registry values. Whatever Windows cannot
/// answer becomes null; nothing here throws.
/// </summary>
public sealed class WindowsSystemSources : ISystemSources
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // Keys whose existence means that a restart is pending, and the value that lists renames for the next start.
    private static readonly string[] RebootPendingKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
    ];

    private readonly GpuUsageReader _gpu;

    /// <summary>Creates the sources.</summary>
    /// <param name="time">The clock; null uses the system clock.</param>
    public WindowsSystemSources(TimeProvider? time = null)
    {
        _gpu = new GpuUsageReader(time ?? TimeProvider.System);
    }

    /// <inheritdoc />
    public SystemTimes? ReadSystemTimes()
    {
        return SystemNative.GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
            ? new SystemTimes(idle, kernel, user)
            : null;
    }

    /// <inheritdoc />
    public MemoryStatus? ReadMemory()
    {
        var buffer = new SystemNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<SystemNative.MemoryStatusEx>() };
        return SystemNative.GlobalMemoryStatusEx(ref buffer)
            ? new MemoryStatus(buffer.TotalPhysical, buffer.AvailablePhysical)
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<VolumeStatus> ReadVolumes()
    {
        var volumes = new List<VolumeStatus>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady && char.IsAsciiLetter(drive.Name[0]))
                    {
                        volumes.Add(new VolumeStatus(char.ToUpperInvariant(drive.Name[0]), drive.TotalSize, drive.TotalFreeSpace));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A volume that cannot be read right now (locked by BitLocker, being removed) is simply not listed.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return volumes;
    }

    /// <inheritdoc />
    public IReadOnlyList<NetworkAdapterStatus> ReadAdapters()
    {
        var adapters = new List<NetworkAdapterStatus>();
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || !NetworkAdapterFilter.IsPhysical(adapter.NetworkInterfaceType, adapter.Name, adapter.Description))
                {
                    continue;
                }

                try
                {
                    IPInterfaceStatistics statistics = adapter.GetIPStatistics();
                    adapters.Add(new NetworkAdapterStatus(adapter.Id, adapter.Name, statistics.BytesReceived, statistics.BytesSent));
                }
                catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
                {
                    // An adapter without statistics is not listed.
                }
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }

        return adapters;
    }

    /// <inheritdoc />
    public string? ReadPrimaryIPv4()
    {
        try
        {
            var candidates = new List<AdapterAddresses>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                candidates.Add(new AdapterAddresses(
                    adapter.OperationalStatus == OperationalStatus.Up,
                    NetworkAdapterFilter.IsPhysical(adapter.NetworkInterfaceType, adapter.Name, adapter.Description),
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel,
                    properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)),
                    [
                        .. properties.UnicastAddresses
                            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(unicast => unicast.Address.ToString()),
                    ]));
            }

            return NetworkAddressSelector.SelectPrimary(candidates);
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public BatteryStatus? ReadBattery()
    {
        if (!SystemNative.GetSystemPowerStatus(out SystemNative.SystemPowerStatus status))
        {
            return null;
        }

        // 128 means "no system battery" (desktop computers); 255 means that Windows cannot tell.
        if ((status.BatteryFlag & SystemNative.NoBattery) != 0 || status.BatteryFlag == SystemNative.UnknownBatteryFlag)
        {
            return null;
        }

        double? level = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        return new BatteryStatus(level, (status.BatteryFlag & SystemNative.Charging) != 0);
    }

    /// <inheritdoc />
    public double? ReadGpuUsage() => _gpu.Read();

    /// <inheritdoc />
    public TimeSpan ReadUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <inheritdoc />
    public string? ReadOsVersion()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            if (key is null)
            {
                return OsVersionFormatter.Format(null, null, null, null, Environment.OSVersion.Version);
            }

            int? build = int.TryParse(key.GetValue("CurrentBuildNumber") as string, out int number) ? number : null;
            int? revision = key.GetValue("UBR") is int update ? update : null;
            return OsVersionFormatter.Format(
                key.GetValue("ProductName") as string,
                key.GetValue("DisplayVersion") as string,
                build,
                revision,
                Environment.OSVersion.Version);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return OsVersionFormatter.Format(null, null, null, null, Environment.OSVersion.Version);
        }
    }

    /// <inheritdoc />
    public bool? ReadPendingReboot()
    {
        try
        {
            foreach (string path in RebootPendingKeys)
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
                if (key is not null)
                {
                    return true;
                }
            }

            using RegistryKey? sessionManager = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return sessionManager?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
