using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>
/// Facts about this Linux computer and the persistent agent id (PLAN.md S07, S17, T802). The boot id and the operating system come from
/// the same sources as the system metrics (<see cref="LinuxBootTime"/>, <see cref="ILinuxSystemSources"/>), so <c>hello</c> and the
/// metrics agree. The MAC addresses are those of the physical interfaces: an interface that has a <c>device</c> entry in
/// <c>/sys/class/net</c>, which loopback, bridges, tunnels and container interfaces do not have.
/// </summary>
public sealed class LinuxPlatformInfo : IPlatformInfo, IDisposable
{
    private const string ZeroAddress = "00:00:00:00:00:00";

    private readonly string _agentIdFile;
    private readonly ILinuxSystemSources _sources;
    private readonly LinuxBootTime _boot;
    private readonly string _networkDirectory;
    private readonly string _hostname;
    private readonly IFileOperations _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _agentId;

    /// <summary>Creates the info object.</summary>
    /// <param name="agentIdFile">The file that keeps the agent id, for example <c>/var/lib/qoffeeautomat/agent-id.txt</c>.</param>
    /// <param name="sources">Where the operating system version comes from.</param>
    /// <param name="boot">Knows when the computer started and has the boot id.</param>
    /// <param name="rootDirectory">The directory that holds <c>sys</c>; null or empty means <c>/</c>.</param>
    /// <param name="hostname">The host name; null asks the operating system.</param>
    /// <param name="fileOperations">The file system operations; null uses the real file system.</param>
    public LinuxPlatformInfo(
        string agentIdFile,
        ILinuxSystemSources sources,
        LinuxBootTime boot,
        string? rootDirectory = null,
        string? hostname = null,
        IFileOperations? fileOperations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentIdFile);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(boot);

        _agentIdFile = agentIdFile;
        _sources = sources;
        _boot = boot;
        _networkDirectory = Path.Combine(string.IsNullOrEmpty(rootDirectory) ? "/" : rootDirectory, "sys/class/net");
        _hostname = string.IsNullOrWhiteSpace(hostname) ? Environment.MachineName : hostname;
        _files = fileOperations ?? FileOperations.Instance;
    }

    /// <inheritdoc />
    public string Hostname => _hostname;

    /// <inheritdoc />
    public IReadOnlyList<string> MacAddresses => ReadMacAddresses();

    /// <inheritdoc />
    public OsInfo Os => new(
        "linux",
        _sources.ReadOsVersion() ?? Environment.OSVersion.Version.ToString(),
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());

    /// <inheritdoc />
    public string BootId => _boot.BootId;

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

    private List<string> ReadMacAddresses()
    {
        var addresses = new List<string>();
        if (!Directory.Exists(_networkDirectory))
        {
            return addresses;
        }

        string[] interfaces;
        try
        {
            interfaces = [.. Directory.EnumerateDirectories(_networkDirectory).Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return addresses;
        }

        foreach (string directory in interfaces)
        {
            if (!Directory.Exists(Path.Combine(directory, "device")))
            {
                continue;
            }

            try
            {
                string address = File.ReadAllText(Path.Combine(directory, "address")).Trim().ToUpperInvariant();
                if (IsHardwareAddress(address) && !addresses.Contains(address, StringComparer.Ordinal))
                {
                    addresses.Add(address);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The interface went away while the directory was read.
            }
        }

        return addresses;
    }

    private static bool IsHardwareAddress(string address)
    {
        // Six pairs of hexadecimal digits; interfaces without an address report all zeros.
        if (address.Length != 17 || string.Equals(address, ZeroAddress, StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = 0; index < address.Length; index++)
        {
            bool separator = index % 3 == 2;
            if (separator ? address[index] != ':' : !char.IsAsciiHexDigit(address[index]))
            {
                return false;
            }
        }

        return true;
    }
}
