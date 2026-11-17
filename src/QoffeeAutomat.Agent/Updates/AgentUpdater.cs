using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>Tells the connected clients that the agent is about to go away (<c>bye</c>, PLAN.md S07).</summary>
public interface IByeAnnouncer
{
    /// <summary>Sends <c>bye</c> with a reason to every client and waits until the frames are written or the flush time has passed.</summary>
    /// <param name="reason">One of <see cref="ByeReasons"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the clients were told.</returns>
    Task SayByeAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>The <see cref="IByeAnnouncer"/> of the agent: the QoffeeLink server. It is resolved when it is needed, so the updater does not pull the server in at start-up.</summary>
public sealed class LinkByeAnnouncer : IByeAnnouncer
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the announcer.</summary>
    /// <param name="services">The services; the <see cref="LinkServer"/> is taken from them.</param>
    public LinkByeAnnouncer(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
    }

    /// <inheritdoc />
    public async Task SayByeAsync(string reason, CancellationToken cancellationToken)
    {
        await _services.GetRequiredService<LinkServer>().SayByeAsync(reason, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Updates the agent from the newest release.</summary>
public interface IAgentUpdater
{
    /// <summary>Runs the update: look up, download, verify, say <c>bye</c> with the reason <c>update</c>, start the installer.</summary>
    /// <param name="cancellationToken">Cancels the update before the installer starts.</param>
    /// <returns>
    /// Success when the installer was started or the agent is current already; a failure with a message otherwise. A refused installer
    /// (wrong checksum, untrusted signature) is a failure, and nothing is started.
    /// </returns>
    Task<ExecutionResult> UpdateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The self-update (PLAN.md S09 item 13, S13, S16, T704, D-046). The steps, in this order, each of which can end the update with a
/// failure that is logged and returned as the result of the run:
/// <list type="number">
/// <item>ask GitHub for the newest release; stop when it is not newer than the running agent;</item>
/// <item>download <c>SHA256SUMS.txt</c> and the installer of this architecture into a fresh directory below the data directory (only
/// the system and administrators can write there, so nobody can swap the file between the checks and the start);</item>
/// <item>compare the SHA-256 checksum of the installer with the entry of <c>SHA256SUMS.txt</c> (a missing entry, a missing checksum file
/// or a different value refuses the installer and deletes it);</item>
/// <item>when the running agent is signed, require a trusted Authenticode signature on the installer;</item>
/// <item>tell the clients <c>bye</c> with the reason <c>update</c>;</item>
/// <item>start the installer with <c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART</c> and leave it running: the installer stops the service
/// and replaces the files. The arguments are separate entries and no value from outside is part of them (D-012).</item>
/// </list>
/// </summary>
public sealed class AgentUpdater : IAgentUpdater
{
    private static readonly string[] SilentArguments = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"];

    private readonly IReleaseSource _releases;
    private readonly IAuthenticodeVerifier _authenticode;
    private readonly IProcessLauncher _launcher;
    private readonly IByeAnnouncer _bye;
    private readonly IAppPaths _paths;
    private readonly UpdateOptions _options;
    private readonly UpdateChecker? _checker;
    private readonly ILogger<AgentUpdater> _logger;
    private int _running;

    /// <summary>Creates the updater.</summary>
    /// <param name="releases">Where the release and its files come from.</param>
    /// <param name="authenticode">Reads and verifies Authenticode signatures.</param>
    /// <param name="launcher">Starts the installer (without a shell).</param>
    /// <param name="bye">Tells the clients that the agent goes away.</param>
    /// <param name="paths">The data directories.</param>
    /// <param name="options">The fixed parameters.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="checker">The checker that learns about the release the updater found; null when there is none.</param>
    public AgentUpdater(
        IReleaseSource releases,
        IAuthenticodeVerifier authenticode,
        IProcessLauncher launcher,
        IByeAnnouncer bye,
        IAppPaths paths,
        UpdateOptions options,
        ILogger<AgentUpdater> logger,
        UpdateChecker? checker = null)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(authenticode);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(bye);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _releases = releases;
        _authenticode = authenticode;
        _launcher = launcher;
        _bye = bye;
        _paths = paths;
        _options = options;
        _logger = logger;
        _checker = checker;
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> UpdateAsync(CancellationToken cancellationToken)
    {
        if (!_options.Supported)
        {
            return Refuse("This agent cannot update itself on this platform.");
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return Refuse("An update is already running.");
        }

        try
        {
            return await RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UpdateException exception)
        {
            return Refuse(exception.Message);
        }
        catch (Exception exception) when (IsExpectedFailure(exception, cancellationToken))
        {
            return Refuse("The update failed: " + exception.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static bool IsExpectedFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException or IOException or UnauthorizedAccessException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
    }

    private static ExecutionResult Success(string message) => new(0, message, string.Empty);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Removed with its directory later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private ExecutionResult Refuse(string reason)
    {
        UpdateLog.UpdateRefused(_logger, reason);
        return ExecutionResult.Failure(ErrorCodes.Internal, reason);
    }

    private async Task<ExecutionResult> RunAsync(CancellationToken cancellationToken)
    {
        ReleaseInfo release = await _releases.GetLatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new UpdateException("The project has no release that can be installed.");
        _checker?.Apply(release);

        string latest = release.Version.ToString();

        // A version of this agent that is no SemVer (a development build) counts as older: the update is offered.
        if (SemanticVersion.TryParse(_options.InstalledVersion, out SemanticVersion? installed) && !release.Version.IsNewerThan(installed))
        {
            UpdateLog.AlreadyCurrent(_logger, _options.InstalledVersion, latest);
            return Success("The agent is already at the newest release (" + latest + ").");
        }

        ReleaseAsset installer = release.Installer
            ?? throw new UpdateException("The release " + release.Tag + " has no installer for " + _options.InstallerArchitecture + ".");
        ReleaseAsset checksums = release.Checksums
            ?? throw new UpdateException("The release " + release.Tag + " has no " + UpdateOptions.ChecksumsAssetName + "; the installer is refused.");
        if (!string.Equals(Path.GetFileName(installer.Name), installer.Name, StringComparison.Ordinal))
        {
            throw new UpdateException("The name of the installer is not a plain file name.");
        }

        UpdateLog.UpdateStarted(_logger, latest, installer.Name);
        string directory = PrepareWorkDirectory();
        string installerPath = Path.Combine(directory, installer.Name);

        // The checksum file comes first: a release without an entry for the installer is refused before anything large is downloaded.
        string sums = await _releases.DownloadTextAsync(checksums, _options.MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
        if (!ChecksumFile.TryFind(sums, installer.Name, out string? expected))
        {
            throw new UpdateException(UpdateOptions.ChecksumsAssetName + " has no entry for " + installer.Name + "; the installer is refused.");
        }

        await _releases.DownloadFileAsync(installer, installerPath, _options.MaxInstallerBytes, cancellationToken).ConfigureAwait(false);

        string actual = await ChecksumFile.ComputeAsync(installerPath, cancellationToken).ConfigureAwait(false);
        if (!ChecksumFile.Matches(expected, actual))
        {
            TryDelete(installerPath);
            throw new UpdateException("The checksum of " + installer.Name + " does not match " + UpdateOptions.ChecksumsAssetName + "; the installer is refused and deleted.");
        }

        CheckSignature(installerPath);

        UpdateLog.UpdateVerified(_logger, latest, actual);
        await _bye.SayByeAsync(ByeReasons.Update, cancellationToken).ConfigureAwait(false);

        string[] arguments =
        [
            .. SilentArguments,
            "/LOG=" + Path.Combine(_paths.LogsDirectory, "update-installer.log"),
        ];
        var request = new ProcessLaunchRequest(
            installerPath,
            arguments,
            directory,
            new Dictionary<string, string>(StringComparer.Ordinal),
            _options.InstallerStartTimeout,
            WaitForExit: false,
            CaptureOutput: false);
        ExecutionResult started = await _launcher.LaunchAsync(RunAsValues.System, request, cancellationToken).ConfigureAwait(false);
        if (!started.Succeeded)
        {
            string reason = string.IsNullOrWhiteSpace(started.StandardError) ? "unknown reason" : started.StandardError;
            throw new UpdateException("The installer could not be started: " + reason);
        }

        UpdateLog.UpdateInstallerStarted(_logger, latest);
        return Success("Updating to " + latest + "; the agent restarts when the installer replaces its files.");
    }

    private void CheckSignature(string installerPath)
    {
        // A build that is not signed (a development build, a portable archive) has nothing to compare with: only the checksum counts.
        // A signed build demands the same of its successor.
        string? own = _options.RunningExecutablePath;
        if (string.IsNullOrEmpty(own) || !_authenticode.IsSigned(own))
        {
            return;
        }

        AuthenticodeResult result = _authenticode.Verify(installerPath);
        if (!result.IsTrusted)
        {
            TryDelete(installerPath);
            throw new UpdateException("The Authenticode signature of the installer is not trusted (" + (result.Reason ?? "no reason given") + "); the installer is refused and deleted.");
        }
    }

    private string PrepareWorkDirectory()
    {
        string root = Path.Combine(_paths.DataDirectory, "updates");
        Directory.CreateDirectory(root);

        // Earlier attempts leave their directories behind (a running installer keeps its file); remove what can be removed.
        foreach (string old in Directory.EnumerateDirectories(root))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (IOException)
            {
                // In use or not removable; the next update tries again.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        string directory = Path.Combine(root, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
