using System.Globalization;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Simulated;

/// <summary>
/// Fixed facts about a pretend computer. The boot id stays the same for the life of the object (and so
/// across agent restarts, which create a new object with the same initial value) and changes only when
/// <see cref="SimulateReboot"/> is called, like the real boot id changes only when the operating system
/// restarts. The agent id is kept in a file when a path is given, otherwise it is a fixed value.
/// </summary>
public sealed class SimulatedPlatformInfo : IPlatformInfo, IDisposable
{
    /// <summary>The agent id used when no file is configured.</summary>
    public const string DefaultAgentId = "00000000-0000-4000-8000-0000000000a1";

    private static readonly string[] DefaultMacAddresses = ["02:00:00:00:00:01"];

    private readonly string? _agentIdFile;
    private readonly IFileOperations _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _agentId;
    private int _bootNumber = 1;

    /// <summary>Creates the info object.</summary>
    /// <param name="hostname">The host name to report.</param>
    /// <param name="macAddresses">The MAC addresses; null uses one fixed address.</param>
    /// <param name="agentIdFile">The file that keeps the agent id; null keeps a fixed id in memory.</param>
    /// <param name="fileOperations">The file system operations; null uses the real file system.</param>
    public SimulatedPlatformInfo(
        string hostname = "SIM-PC",
        IReadOnlyList<string>? macAddresses = null,
        string? agentIdFile = null,
        IFileOperations? fileOperations = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        Hostname = hostname;
        MacAddresses = macAddresses ?? DefaultMacAddresses;
        _agentIdFile = agentIdFile;
        _files = fileOperations ?? FileOperations.Instance;
    }

    /// <inheritdoc />
    public string Hostname { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> MacAddresses { get; }

    /// <inheritdoc />
    public OsInfo Os { get; } = new("simulated", "1.0", "x64");

    /// <inheritdoc />
    public string BootId => "sim-boot-" + Volatile.Read(ref _bootNumber).ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>Simulates an operating system restart: the boot id changes.</summary>
    public void SimulateReboot()
    {
        Interlocked.Increment(ref _bootNumber);
    }

    /// <inheritdoc />
    public async ValueTask<string> GetOrCreateAgentIdAsync(CancellationToken cancellationToken)
    {
        if (_agentIdFile is null)
        {
            return DefaultAgentId;
        }

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
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(created + "\n");
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
}
