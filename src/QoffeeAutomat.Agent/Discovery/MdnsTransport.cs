using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;

namespace QoffeeAutomat.Agent.Discovery;

/// <summary>A network interface that the multicast transport uses.</summary>
/// <param name="Index">The interface index (the same number as in the received packet information).</param>
/// <param name="Name">The interface name.</param>
/// <param name="Addresses">The IPv4 addresses of the interface; they go into the address records announced on it.</param>
public sealed record MdnsInterface(int Index, string Name, IReadOnlyList<IPAddress> Addresses);

/// <summary>A datagram that arrived on a multicast interface.</summary>
/// <param name="InterfaceIndex">The interface it arrived on.</param>
/// <param name="Remote">The sender.</param>
/// <param name="Data">The datagram.</param>
public sealed record MdnsDatagram(int InterfaceIndex, IPEndPoint Remote, ReadOnlyMemory<byte> Data);

/// <summary>
/// The multicast sockets of the responder. Opening one is what makes the agent visible on the network, and disposing
/// it closes every socket; nothing outside this seam touches the network, so tests replace it.
/// </summary>
public interface IMdnsTransport : IAsyncDisposable
{
    /// <summary>The interfaces that the transport is joined on.</summary>
    IReadOnlyList<MdnsInterface> Interfaces { get; }

    /// <summary>Sends a datagram on one interface.</summary>
    /// <param name="interfaceIndex">The interface, one of <see cref="Interfaces"/>.</param>
    /// <param name="datagram">The datagram.</param>
    /// <param name="destination">A unicast destination, or null for the multicast group.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the datagram is handed to the network stack.</returns>
    ValueTask SendAsync(int interfaceIndex, ReadOnlyMemory<byte> datagram, IPEndPoint? destination, CancellationToken cancellationToken);

    /// <summary>The datagrams that arrive on any interface, until the transport is disposed.</summary>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    /// <returns>The datagrams.</returns>
    IAsyncEnumerable<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the multicast transport. This is the only place where the responder creates sockets.</summary>
public interface IMdnsTransportFactory
{
    /// <summary>Opens sockets on the usable interfaces of the computer as they are right now.</summary>
    /// <returns>The transport.</returns>
    /// <exception cref="SocketException">The port is not available.</exception>
    /// <exception cref="InvalidOperationException">No interface can be used, for example because the computer is offline.</exception>
    IMdnsTransport Open();
}

/// <summary>Tells when the network configuration of the computer changed.</summary>
public interface INetworkChangeSource
{
    /// <summary>Starts watching.</summary>
    /// <param name="changed">Called after an address or the availability of a network changed; it must return quickly.</param>
    /// <returns>Dispose it to stop watching.</returns>
    IDisposable Watch(Action changed);
}

/// <summary><see cref="INetworkChangeSource"/> on <see cref="NetworkChange"/>.</summary>
public sealed class SystemNetworkChangeSource : INetworkChangeSource
{
    /// <inheritdoc />
    public IDisposable Watch(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        NetworkAddressChangedEventHandler address = (_, _) => changed();
        NetworkAvailabilityChangedEventHandler availability = (_, _) => changed();
        NetworkChange.NetworkAddressChanged += address;
        NetworkChange.NetworkAvailabilityChanged += availability;
        return new Subscription(address, availability);
    }

    private sealed class Subscription : IDisposable
    {
        private NetworkAddressChangedEventHandler? _address;
        private NetworkAvailabilityChangedEventHandler? _availability;

        public Subscription(NetworkAddressChangedEventHandler address, NetworkAvailabilityChangedEventHandler availability)
        {
            _address = address;
            _availability = availability;
        }

        public void Dispose()
        {
            NetworkAddressChangedEventHandler? address = Interlocked.Exchange(ref _address, null);
            NetworkAvailabilityChangedEventHandler? availability = Interlocked.Exchange(ref _availability, null);
            if (address is not null)
            {
                NetworkChange.NetworkAddressChanged -= address;
            }

            if (availability is not null)
            {
                NetworkChange.NetworkAvailabilityChanged -= availability;
            }
        }
    }
}

/// <summary><see cref="IMdnsTransportFactory"/> on UDP sockets joined to 224.0.0.251:5353.</summary>
public sealed class UdpMdnsTransportFactory : IMdnsTransportFactory
{
    /// <inheritdoc />
    public IMdnsTransport Open() => UdpMdnsTransport.Open();
}

/// <summary>
/// Multicast DNS over IPv4 UDP (RFC 6762): one socket per usable interface, bound to port 5353 with address reuse
/// (other responders such as Avahi or the Windows DNS client share the port), joined to 224.0.0.251 on its interface
/// and sending through that interface only. A datagram that the packet information shows to belong to another of our
/// interfaces is dropped, so a query that every socket sees is answered once. IPv6 is not used (see docs/adr/0001-zeroconf.md).
/// </summary>
internal sealed class UdpMdnsTransport : IMdnsTransport
{
    private const int Port = 5353;
    private const int MaxDatagramBytes = 9000;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private readonly List<InterfaceSocket> _sockets = [];
    private readonly List<Task> _loops = [];
    private readonly Channel<MdnsDatagram> _received = Channel.CreateBounded<MdnsDatagram>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;

    private UdpMdnsTransport()
    {
    }

    public IReadOnlyList<MdnsInterface> Interfaces { get; private set; } = [];

    public static UdpMdnsTransport Open()
    {
        var transport = new UdpMdnsTransport();
        try
        {
            transport.JoinInterfaces();
        }
        catch
        {
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        return transport;
    }

    public async ValueTask SendAsync(int interfaceIndex, ReadOnlyMemory<byte> datagram, IPEndPoint? destination, CancellationToken cancellationToken)
    {
        InterfaceSocket? target = _sockets.Find(candidate => candidate.Info.Index == interfaceIndex);
        if (target is null)
        {
            return;
        }

        await target.Socket.SendToAsync(datagram, SocketFlags.None, destination ?? new IPEndPoint(Group, Port), cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        return _received.Reader.ReadAllAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (InterfaceSocket entry in _sockets)
        {
            entry.Socket.Dispose();
        }

        await Task.WhenAll(_loops).ConfigureAwait(false);
        _received.Writer.TryComplete();
        _stop.Dispose();
    }

    private static bool IsUsable(NetworkInterface nic)
    {
        try
        {
            return nic.OperationalStatus == OperationalStatus.Up
                && nic.SupportsMulticast
                && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel);
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private void JoinInterfaces()
    {
        var found = new List<MdnsInterface>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsUsable(nic))
            {
                continue;
            }

            int index;
            try
            {
                index = nic.GetIPProperties().GetIPv4Properties().Index;
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                // The interface has no IPv4 configuration.
                continue;
            }

            IPAddress[] addresses =
            [
                .. nic.GetIPProperties().UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork),
            ];
            if (addresses.Length == 0)
            {
                continue;
            }

            var info = new MdnsInterface(index, nic.Name, addresses);
            Socket? socket = TryOpenSocket(addresses[0]);
            if (socket is null)
            {
                continue;
            }

            _sockets.Add(new InterfaceSocket(info, socket));
            found.Add(info);
        }

        if (_sockets.Count == 0)
        {
            throw new InvalidOperationException("No network interface can be used for multicast DNS.");
        }

        Interfaces = found;
        foreach (InterfaceSocket entry in _sockets)
        {
            InterfaceSocket current = entry;
            _loops.Add(Task.Run(() => ReceiveLoopAsync(current), CancellationToken.None));
        }
    }

    private static Socket? TryOpenSocket(IPAddress local)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            return socket;
        }
        catch (SocketException)
        {
            // This interface cannot join the group (for example it went down meanwhile); the others may still work.
            socket.Dispose();
            return null;
        }
    }

    private async Task ReceiveLoopAsync(InterfaceSocket entry)
    {
        byte[] buffer = new byte[MaxDatagramBytes];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                SocketReceiveMessageFromResult result = await entry.Socket
                    .ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, remote, _stop.Token)
                    .ConfigureAwait(false);

                int arrivedOn = result.PacketInformation.Interface;
                if (arrivedOn != entry.Info.Index && _sockets.Exists(other => other.Info.Index == arrivedOn))
                {
                    continue;
                }

                if (result.RemoteEndPoint is IPEndPoint sender)
                {
                    _received.Writer.TryWrite(new MdnsDatagram(entry.Info.Index, sender, buffer.AsMemory(0, result.ReceivedBytes).ToArray()));
                }
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an ICMP "port unreachable" for an earlier send as an error of the next receive.
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    private sealed record InterfaceSocket(MdnsInterface Info, Socket Socket);
}
