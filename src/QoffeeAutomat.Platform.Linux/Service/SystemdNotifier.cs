using System.Net.Sockets;
using System.Text;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>The state assignments of the notification protocol (<c>sd_notify(3)</c>) that the agent sends.</summary>
public static class SystemdStates
{
    /// <summary>The service finished starting up and accepts requests.</summary>
    public const string Ready = "READY=1";

    /// <summary>The service begins to shut down.</summary>
    public const string Stopping = "STOPPING=1";

    /// <summary>The keep-alive for <c>WatchdogSec=</c>.</summary>
    public const string Watchdog = "WATCHDOG=1";

    /// <summary>Builds a free-form status line that <c>systemctl status</c> shows.</summary>
    /// <param name="text">The text; line breaks are replaced by spaces, because a line break would start the next assignment.</param>
    /// <returns>The assignment <c>STATUS=...</c>.</returns>
    public static string Status(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "STATUS=" + text.ReplaceLineEndings(" ");
    }
}

/// <summary>Sends state changes of the service to systemd (PLAN.md S17, T802).</summary>
public interface ISystemdNotifier
{
    /// <summary>True when systemd gave this process a notification socket, so <see cref="Notify"/> can reach it.</summary>
    bool IsEnabled { get; }

    /// <summary>Sends one message: one or more assignments such as <see cref="SystemdStates.Ready"/>, separated by line breaks.</summary>
    /// <param name="state">The message.</param>
    /// <returns>True when the message was handed to systemd; false when there is no socket or it could not be written.</returns>
    bool Notify(string state);
}

/// <summary>
/// The notification protocol of systemd in managed code: one datagram per message on the Unix domain socket that
/// <c>NOTIFY_SOCKET</c> names. The agent needs no native <c>libsystemd</c>, so the same published files run on every distribution.
/// A path that starts with <c>@</c> is an address in the abstract namespace of Linux, which the socket class takes as a name that
/// starts with a NUL character. Failures are never thrown: a service whose supervisor is gone keeps running.
/// </summary>
public sealed class SystemdNotifier : ISystemdNotifier
{
    private readonly string? _address;

    /// <summary>Creates a notifier for a socket address.</summary>
    /// <param name="notifySocket">The value of <c>NOTIFY_SOCKET</c>: an absolute path or <c>@name</c>; null or anything else disables the notifier.</param>
    public SystemdNotifier(string? notifySocket)
    {
        _address = ToEndPointAddress(notifySocket);
    }

    /// <inheritdoc />
    public bool IsEnabled => _address is not null;

    /// <summary>Creates the notifier that the environment asks for.</summary>
    /// <param name="getVariable">Reads an environment variable; null when it is not set.</param>
    /// <returns>The notifier; it is disabled when <c>NOTIFY_SOCKET</c> is not set.</returns>
    public static SystemdNotifier FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        return new SystemdNotifier(getVariable(SystemdEnvironment.NotifySocketVariable));
    }

    /// <summary>Converts the value of <c>NOTIFY_SOCKET</c> into the address that <see cref="UnixDomainSocketEndPoint"/> takes.</summary>
    /// <param name="notifySocket">The variable's value.</param>
    /// <returns>The address; null for an empty value or a kind of address that is not a Unix socket (for example <c>vsock:</c>).</returns>
    public static string? ToEndPointAddress(string? notifySocket)
    {
        if (string.IsNullOrEmpty(notifySocket))
        {
            return null;
        }

        if (notifySocket[0] == '/')
        {
            return notifySocket;
        }

        return notifySocket.Length > 1 && notifySocket[0] == '@' ? '\0' + notifySocket[1..] : null;
    }

    /// <inheritdoc />
    public bool Notify(string state)
    {
        ArgumentException.ThrowIfNullOrEmpty(state);

        if (_address is null)
        {
            return false;
        }

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_address));
            return socket.Send(Encoding.UTF8.GetBytes(state)) > 0;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or ArgumentException or PlatformNotSupportedException)
        {
            // systemd is not listening (any more): the service goes on.
            return false;
        }
    }
}
