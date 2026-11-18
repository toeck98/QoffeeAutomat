using System.Globalization;
using System.Net.Sockets;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>
/// Where the Unix domain socket of the session helper lives (PLAN.md S17, T803): in the runtime directory of the user,
/// <c>/run/user/&lt;uid&gt;/qoffeeautomat/session.sock</c>. The directory belongs to the user and has mode 0700, the socket mode 0600. The
/// helper is the server and the agent, which runs as root, connects: root can open a socket of any user, while no other user can open
/// the socket of this one, so the commands in the context of a user can only be given by root.
/// </summary>
public static class SessionSocketPaths
{
    /// <summary>The environment variable of the runtime directory that systemd creates for every user session.</summary>
    public const string RuntimeDirectoryVariable = "XDG_RUNTIME_DIR";

    /// <summary>
    /// The environment variable that replaces the whole socket path, for tests and for a helper that is started by hand. The agent never
    /// reads it: it always looks into the runtime directory of the user that logind reports.
    /// </summary>
    public const string SocketOverrideVariable = "QOFFEE_SESSION_SOCKET";

    /// <summary>The longest path of a Unix domain socket that Linux accepts (<c>sun_path</c> has 108 bytes including the terminator).</summary>
    public const int MaxPathLength = 107;

    /// <summary>The runtime directory that systemd creates for a user.</summary>
    /// <param name="uid">The numeric user id.</param>
    /// <returns>For example <c>/run/user/1000</c>.</returns>
    public static string RuntimeDirectoryOf(long uid)
    {
        return "/run/user/" + uid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The directory that holds the socket, inside a runtime directory.</summary>
    /// <param name="runtimeDirectory">The runtime directory of the user.</param>
    /// <returns>The directory.</returns>
    public static string SocketDirectory(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        return Path.Combine(runtimeDirectory, SessionIpc.UnixSocketDirectory);
    }

    /// <summary>The socket file, inside a runtime directory.</summary>
    /// <param name="runtimeDirectory">The runtime directory of the user.</param>
    /// <returns>The path of the socket.</returns>
    public static string SocketPathIn(string runtimeDirectory)
    {
        return Path.Combine(SocketDirectory(runtimeDirectory), SessionIpc.UnixSocketFile);
    }

    /// <summary>The socket of the user with a given id; the path that the agent connects to.</summary>
    /// <param name="uid">The numeric user id.</param>
    /// <returns>The path of the socket.</returns>
    public static string SocketPathOf(long uid) => SocketPathIn(RuntimeDirectoryOf(uid));

    /// <summary>The socket path that the helper binds: the override, else the runtime directory of the user.</summary>
    /// <param name="environment">Reads an environment variable; null when it is not set.</param>
    /// <returns>The path; null when neither the override nor the runtime directory is known (no user session).</returns>
    public static string? ForHelper(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment(SocketOverrideVariable) is { Length: > 0 } overridden)
        {
            return overridden;
        }

        return environment(RuntimeDirectoryVariable) is { Length: > 0 } runtime && Path.IsPathRooted(runtime)
            ? SocketPathIn(runtime)
            : null;
    }
}

/// <summary>The credentials of the process at the other end of a Unix domain socket, as the kernel reports them (<c>SO_PEERCRED</c>).</summary>
/// <param name="ProcessId">The process id.</param>
/// <param name="UserId">The effective user id of the process when it connected.</param>
/// <param name="GroupId">The effective group id.</param>
public sealed record PeerCredentials(int ProcessId, uint UserId, uint GroupId);

/// <summary>Reads who is at the other end of a Unix domain socket; the kernel fills the answer in, the peer cannot forge it.</summary>
public interface IPeerCredentialReader
{
    /// <summary>Reads the credentials of the peer.</summary>
    /// <param name="socket">A connected Unix domain socket.</param>
    /// <returns>The credentials; null when the kernel cannot tell (not Linux).</returns>
    PeerCredentials? Read(Socket socket);
}

/// <summary>The <see cref="IPeerCredentialReader"/> of Linux: <c>getsockopt(SO_PEERCRED)</c>.</summary>
public sealed class LinuxPeerCredentialReader : IPeerCredentialReader
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int StructUcredSize = 12;

    /// <inheritdoc />
    public PeerCredentials? Read(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        byte[] buffer = new byte[StructUcredSize];
        try
        {
            int length = socket.GetRawSocketOption(SolSocket, SoPeerCred, buffer);
            if (length < StructUcredSize)
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            return null;
        }

        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }, in the byte order of the machine.
        return new PeerCredentials(BitConverter.ToInt32(buffer, 0), BitConverter.ToUInt32(buffer, 4), BitConverter.ToUInt32(buffer, 8));
    }
}
