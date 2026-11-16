using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>
/// Facts about this Windows computer and the persistent agent id. The boot id is derived from the time of the last
/// start, rounded to the minute; with a <see cref="WindowsBootTime"/> it is the same boot time that the metric
/// <c>system.last_boot</c> reports (T304).
/// </summary>
public sealed class WindowsPlatformInfo : IPlatformInfo, IDisposable
{
    private readonly string _agentIdFile;
    private readonly IFileOperations _files;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WindowsBootTime? _bootTime;
    private string? _agentId;

    /// <summary>Creates the info object.</summary>
    /// <param name="agentIdFile">The file that keeps the agent id, for example <c>data/agent-id.txt</c>.</param>
    /// <param name="time">The clock; null uses the system clock.</param>
    /// <param name="fileOperations">The file system operations; null uses the real file system.</param>
    /// <param name="bootTime">The source of the boot id, shared with the metric <c>system.last_boot</c> (T304); null derives it here.</param>
    public WindowsPlatformInfo(
        string agentIdFile,
        TimeProvider? time = null,
        IFileOperations? fileOperations = null,
        WindowsBootTime? bootTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentIdFile);

        _agentIdFile = agentIdFile;
        _time = time ?? TimeProvider.System;
        _files = fileOperations ?? FileOperations.Instance;
        _bootTime = bootTime;
    }

    /// <inheritdoc />
    public string Hostname => Environment.MachineName;

    /// <inheritdoc />
    public IReadOnlyList<string> MacAddresses => ReadMacAddresses();

    /// <inheritdoc />
    public OsInfo Os => new(
        "windows",
        Environment.OSVersion.Version.ToString(3),
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());

    /// <inheritdoc />
    public string BootId
    {
        get
        {
            if (_bootTime is not null)
            {
                return _bootTime.BootId;
            }

            DateTimeOffset boot = _time.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
            long minutes = boot.ToUnixTimeSeconds() / 60;
            return "win-" + minutes.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> GetOrCreateAgentIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agentId is not null)
            {
                return _agentId;
            }

            if (File.Exists(_agentIdFile))
            {
                string existing = (await File.ReadAllTextAsync(_agentIdFile, cancellationToken).ConfigureAwait(false)).Trim();
                if (Guid.TryParse(existing, out Guid parsed))
                {
                    _agentId = parsed.ToString("D", CultureInfo.InvariantCulture);
                    return _agentId;
                }
            }

            string created = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            byte[] bytes = Encoding.UTF8.GetBytes(created + "\n");
            await AtomicFile.WriteAsync(_files, _agentIdFile, bytes, ownerOnly: false, cancellationToken).ConfigureAwait(false);
            _agentId = created;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the lock of the object.</summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    private static List<string> ReadMacAddresses()
    {
        var addresses = new List<string>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            bool wired = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT;
            if (!wired && adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            {
                continue;
            }

            string description = adapter.Description + " " + adapter.Name;
            if (description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                || description.Contains("vethernet", StringComparison.OrdinalIgnoreCase)
                || description.Contains("vmware", StringComparison.OrdinalIgnoreCase)
                || description.Contains("vpn", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] bytes = adapter.GetPhysicalAddress().GetAddressBytes();
            if (bytes.Length == 6)
            {
                addresses.Add(string.Join(':', bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture))));
            }
        }

        return addresses;
    }
}
