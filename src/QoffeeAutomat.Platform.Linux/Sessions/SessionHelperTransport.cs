using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>What the actions and the power scheduler need from the user's session: its id, desktop actions and notifications.</summary>
public interface ILinuxUserSession
{
    /// <summary>The logind id of the interactive session; null when nobody is logged in at the computer.</summary>
    string? SessionId { get; }

    /// <summary>Runs a built-in action of the desktop in the session helper.</summary>
    /// <param name="name">The action name.</param>
    /// <param name="variables">The validated variables.</param>
    /// <param name="timeout">How long the action may take.</param>
    /// <param name="cancellationToken">Cancels the action.</param>
    /// <returns>The outcome; <see cref="QoffeeAutomat.Contracts.Link.ErrorCodes.NoSession"/> while no helper is connected.</returns>
    ValueTask<ExecutionResult> RunBuiltinAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Shows a notification to the user through the helper; does nothing without one.</summary>
    /// <param name="title">The title.</param>
    /// <param name="message">The text.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the message was sent.</returns>
    ValueTask NotifyAsync(string title, string message, CancellationToken cancellationToken);
}

/// <summary>How the agent reaches the session helper of a user: opens the stream to it.</summary>
public interface ISessionHelperTransport
{
    /// <summary>Connects to the helper of a user.</summary>
    /// <param name="uid">The numeric user id of the interactive session, as logind reports it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The connected stream; null when there is no helper to connect to, or when the socket or its owner is not what it must be.</returns>
    ValueTask<Stream?> ConnectAsync(long uid, CancellationToken cancellationToken);
}

/// <summary>
/// The <see cref="ISessionHelperTransport"/> over the Unix domain socket of the helper (PLAN.md S17, T803). Before and after it connects, it
/// checks that it talks to the user it means to: the runtime directory, the socket directory and the socket belong to that user and are
/// closed to everybody else (modes 0700 and 0600, no symbolic link), and the kernel reports the user as owner of the process at the other
/// end (<c>SO_PEERCRED</c>). Another account cannot put a socket of its own in the place of the helper, and the agent, which runs as root,
/// never sends a command to a process that does not belong to the user whose session it is for.
/// </summary>
public sealed partial class UnixSocketHelperTransport : ISessionHelperTransport
{
    /// <summary>How long a connect may take; a helper answers at once, or it is not there.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<long, string> _socketPath;
    private readonly IUnixFileSystem _files;
    private readonly IPeerCredentialReader _peers;
    private readonly ILogger? _logger;
    private readonly bool _checkOwners;

    /// <summary>Creates the transport.</summary>
    /// <param name="files">Reads the owner of a file; null uses <c>stat</c>.</param>
    /// <param name="peers">Reads the credentials of the peer; null uses <c>SO_PEERCRED</c>.</param>
    /// <param name="logger">Receives the reasons why a socket is not used.</param>
    /// <param name="socketPath">The path of the socket of a user; null uses <see cref="SessionSocketPaths.SocketPathOf"/>. A seam for tests.</param>
    /// <param name="checkOwners">
    /// False skips the checks of the owners and of the peer; only tests that run as an ordinary user against their own temporary directory
    /// use it. The modes and the symbolic link are always checked.
    /// </param>
    public UnixSocketHelperTransport(
        IUnixFileSystem? files = null,
        IPeerCredentialReader? peers = null,
        ILogger? logger = null,
        Func<long, string>? socketPath = null,
        bool checkOwners = true)
    {
        _files = files ?? new UnixFileSystem();
        _peers = peers ?? new LinuxPeerCredentialReader();
        _logger = logger;
        _socketPath = socketPath ?? SessionSocketPaths.SocketPathOf;
        _checkOwners = checkOwners;
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> ConnectAsync(long uid, CancellationToken cancellationToken)
    {
        string path = _socketPath(uid);
        if (path.Length > SessionSocketPaths.MaxPathLength || !File.Exists(path))
        {
            return null;
        }

        string? problem = CheckPath(path, uid);
        if (problem is not null)
        {
            if (_logger is not null)
            {
                SocketRefused(_logger, path, problem);
            }

            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token).ConfigureAwait(false);

            if (_checkOwners && _peers.Read(socket) is { } peer && peer.UserId != (uint)uid)
            {
                if (_logger is not null)
                {
                    SocketRefused(_logger, path, $"the process at the other end belongs to user {peer.UserId}, not to user {uid}");
                }

                socket.Dispose();
                return null;
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            socket.Dispose();
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Nobody listens (a stale socket file) or the helper is busy starting: the next attempt tries again.
            return null;
        }
    }

    [LoggerMessage(EventId = 3310, Level = LogLevel.Warning, Message = "The session socket {Path} is not used: {Reason}")]
    private static partial void SocketRefused(ILogger logger, string path, string reason);

    private static bool OpenToOthers(UnixFileMode mode) =>
        (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0;

    private string? CheckPath(string path, long uid)
    {
        string directory = Path.GetDirectoryName(path)!;
        string runtime = Path.GetDirectoryName(directory)!;
        if (OperatingSystem.IsWindows())
        {
            return "Unix permissions do not exist on this system";
        }

        try
        {
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(directory).LinkTarget is not null)
            {
                return "it is reached through a symbolic link";
            }

            if (OpenToOthers(File.GetUnixFileMode(path)))
            {
                return "other users can open it (the mode must be 0600)";
            }

            if (OpenToOthers(File.GetUnixFileMode(directory)))
            {
                return "its directory is open to other users (the mode must be 0700)";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "its permissions cannot be read: " + exception.Message;
        }

        if (!_checkOwners)
        {
            return null;
        }

        foreach (string entry in new[] { path, directory, runtime })
        {
            if (_files.GetOwner(entry) is not { } owner)
            {
                return $"the owner of {entry} cannot be read";
            }

            if (owner != (uint)uid)
            {
                return $"{entry} does not belong to user {uid} but to user {owner}";
            }
        }

        return null;
    }
}
