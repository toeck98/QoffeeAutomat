using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Discovery;

/// <summary>
/// Announces the agent with multicast DNS and DNS-based service discovery (RFC 6762, RFC 6763), implemented in
/// the agent itself; docs/adr/0001-zeroconf.md explains why. Starting an advertisement opens the multicast sockets
/// through the <see cref="IMdnsTransportFactory"/> (nothing is opened before), sends an unsolicited announcement on
/// every usable interface and repeats it after a second, and from then on answers queries for the service type, the
/// instance and the host name. Starting it again replaces the advertisement and opens the sockets afresh, which is how
/// a changed network is picked up; a changed instance name or port first withdraws the old one. Stopping sends a
/// goodbye (the records with a time to live of zero) and closes every socket.
/// <para>
/// Known limits: no probing and no conflict resolution (two agents with the same name on one network are not told
/// apart), IPv4 only, and no random delay before answers (the record set is unique to this host).
/// </para>
/// </summary>
public sealed class MdnsServiceAdvertiser : IServiceAdvertiser, IAsyncDisposable
{
    private const uint ServiceTtl = 4500;
    private const uint HostTtl = 120;
    private const uint LegacyTtl = 10;
    private const int MulticastPort = 5353;
    private const ushort ClassAny = 255;
    private static readonly TimeSpan AnnounceRepeat = TimeSpan.FromSeconds(1);

    private readonly IMdnsTransportFactory _factory;
    private readonly string _hostLabel;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private bool _disposed;

    /// <summary>Creates the advertiser. No socket is opened.</summary>
    /// <param name="factory">Opens the multicast sockets when an advertisement starts.</param>
    /// <param name="platform">The platform facts; the host name is the target of the service record.</param>
    /// <param name="time">The clock for the repeated announcement.</param>
    /// <param name="logger">The logger.</param>
    public MdnsServiceAdvertiser(
        IMdnsTransportFactory factory,
        IPlatformInfo platform,
        TimeProvider time,
        ILogger<MdnsServiceAdvertiser> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _hostLabel = ZeroconfNames.ToHostLabel(platform.Hostname);
        _time = time;
        _logger = logger;
    }

    /// <summary>True while an advertisement is active, that is while multicast sockets are open.</summary>
    public bool IsAdvertising => Volatile.Read(ref _session) is not null;

    /// <inheritdoc />
    public async ValueTask StartAsync(ServiceAdvertisement advertisement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(advertisement);

        Names names = Names.From(advertisement, _hostLabel);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Session? previous = _session;
            _session = null;
            if (previous is not null)
            {
                bool sameService = Names.SameService(previous.Names, names) && previous.Port == advertisement.Port;
                await previous.CloseAsync(sendGoodbye: !sameService, cancellationToken).ConfigureAwait(false);
            }

            IMdnsTransport transport = _factory.Open();
            var session = new Session(this, transport, advertisement, names);
            _session = session;
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? session = _session;
            _session = null;
            if (session is not null)
            {
                await session.CloseAsync(sendGoodbye: true, cancellationToken).ConfigureAwait(false);
                DiscoveryLog.Withdrawn(_logger, session.InstanceName);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the sockets without a goodbye; call <see cref="StopAsync"/> first to withdraw the advertisement.</summary>
    /// <returns>A task that completes when the sockets are closed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Session? session = _session;
            _session = null;
            if (session is not null)
            {
                await session.CloseAsync(sendGoodbye: false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The DNS names of one advertisement.</summary>
    private sealed record Names(string[] ServiceType, string[] Instance, string[] Host, string[] Enumeration)
    {
        private static readonly string[] EnumerationName = ["_services", "_dns-sd", "_udp", "local"];

        public static Names From(ServiceAdvertisement advertisement, string hostLabel)
        {
            string[] serviceType = advertisement.ServiceType.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (serviceType.Length < 2 || serviceType.Any(label => Encoding.UTF8.GetByteCount(label) > 63))
            {
                throw new ArgumentException("The service type must look like _name._tcp.local.", nameof(advertisement));
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(advertisement.Port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(advertisement.Port, 65535);

            string instance = ZeroconfNames.ToInstanceLabel(advertisement.InstanceName);
            return new Names(serviceType, [instance, .. serviceType], [hostLabel, "local"], EnumerationName);
        }

        public static bool SameService(Names left, Names right)
        {
            return DnsWire.NameEquals(left.Instance, right.Instance) && DnsWire.NameEquals(left.ServiceType, right.ServiceType);
        }
    }

    /// <summary>The records of one advertisement on one interface.</summary>
    private sealed record Records(DnsRecord Enumeration, DnsRecord ServicePointer, DnsRecord Service, DnsRecord Text, DnsRecord[] Addresses)
    {
        public IEnumerable<DnsRecord> All => [Enumeration, ServicePointer, Service, Text, .. Addresses];
    }

    /// <summary>One started advertisement: the open transport, the receive loop and the repeated announcement.</summary>
    private sealed class Session : IDisposable
    {
        private readonly MdnsServiceAdvertiser _owner;
        private readonly IMdnsTransport _transport;
        private readonly ServiceAdvertisement _advertisement;
        private readonly CancellationTokenSource _stop = new();
        private Task _receive = Task.CompletedTask;
        private Task _repeat = Task.CompletedTask;

        public Session(MdnsServiceAdvertiser owner, IMdnsTransport transport, ServiceAdvertisement advertisement, Names names)
        {
            _owner = owner;
            _transport = transport;
            _advertisement = advertisement;
            Names = names;
        }

        public Names Names { get; }

        public int Port => _advertisement.Port;

        public string InstanceName => Names.Instance[0];

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _receive = Task.Run(() => ReceiveLoopAsync(_stop.Token), CancellationToken.None);
            await AnnounceAsync(goodbye: false, cancellationToken).ConfigureAwait(false);
            DiscoveryLog.Announced(_owner._logger, InstanceName, Port, _transport.Interfaces.Count);
            _repeat = Task.Run(() => RepeatAsync(_stop.Token), CancellationToken.None);
        }

        public async Task CloseAsync(bool sendGoodbye, CancellationToken cancellationToken)
        {
            if (sendGoodbye)
            {
                await AnnounceAsync(goodbye: true, cancellationToken).ConfigureAwait(false);
            }

            await _stop.CancelAsync().ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(_receive, _repeat).ConfigureAwait(false);
            Dispose();
        }

        public void Dispose()
        {
            _stop.Dispose();
        }

        private async Task RepeatAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(AnnounceRepeat, _owner._time, cancellationToken).ConfigureAwait(false);
                await AnnounceAsync(goodbye: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The advertisement was replaced or stopped.
            }
        }

        private async Task AnnounceAsync(bool goodbye, CancellationToken cancellationToken)
        {
            foreach (MdnsInterface networkInterface in _transport.Interfaces)
            {
                Records records = Build(networkInterface, goodbye);
                byte[] message = DnsWire.WriteMessage(0, DnsWire.ResponseFlags, [], [.. records.All], []);
                await SendAsync(networkInterface.Index, message, null, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (MdnsDatagram datagram in _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
                {
                    await HandleAsync(datagram, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The advertisement was replaced or stopped.
            }
#pragma warning disable CA1031 // A faulty datagram or socket must end the receive loop, not the agent; announcements go on.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                string reason = exception.GetType().Name;
                DiscoveryLog.ReceiveEnded(_owner._logger, reason);
            }
        }

        private async Task HandleAsync(MdnsDatagram datagram, CancellationToken cancellationToken)
        {
            if (!DnsWire.TryParse(datagram.Data.Span, out DnsPacket? packet) || !packet.IsQuery)
            {
                return;
            }

            MdnsInterface? networkInterface = _transport.Interfaces.FirstOrDefault(candidate => candidate.Index == datagram.InterfaceIndex);
            if (networkInterface is null)
            {
                return;
            }

            (List<DnsRecord> answers, List<DnsRecord> additionals) = Answer(packet, Build(networkInterface, goodbye: false));
            if (answers.Count == 0)
            {
                return;
            }

            if (datagram.Remote.Port != MulticastPort)
            {
                // A resolver that is not a multicast DNS responder (RFC 6762 section 6.7): answer it directly, with the
                // id and the question of the query, a short time to live and without the cache-flush bit.
                DnsRecord[] direct = [.. answers.Select(Legacy)];
                DnsRecord[] directExtra = [.. additionals.Select(Legacy)];
                byte[] reply = DnsWire.WriteMessage(packet.Id, DnsWire.ResponseFlags, packet.Questions, direct, directExtra);
                await SendAsync(networkInterface.Index, reply, datagram.Remote, cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] message = DnsWire.WriteMessage(0, DnsWire.ResponseFlags, [], answers, additionals);
            await SendAsync(networkInterface.Index, message, null, cancellationToken).ConfigureAwait(false);
        }

        private (List<DnsRecord> Answers, List<DnsRecord> Additionals) Answer(DnsPacket packet, Records records)
        {
            var answers = new List<DnsRecord>();
            var additionals = new List<DnsRecord>();
            foreach (DnsQuestion question in packet.Questions)
            {
                if (question.BaseClass != DnsWire.ClassIn && question.BaseClass != ClassAny)
                {
                    continue;
                }

                bool wantsPointer = question.Type is DnsWire.TypePtr or DnsWire.TypeAny;
                if (wantsPointer && DnsWire.NameEquals(question.Name, Names.Enumeration))
                {
                    AddOnce(answers, records.Enumeration);
                }
                else if (wantsPointer && DnsWire.NameEquals(question.Name, Names.ServiceType))
                {
                    if (!IsKnownAnswer(packet))
                    {
                        AddOnce(answers, records.ServicePointer);
                        AddOnce(additionals, records.Service);
                        AddOnce(additionals, records.Text);
                        foreach (DnsRecord address in records.Addresses)
                        {
                            AddOnce(additionals, address);
                        }
                    }
                }
                else if (DnsWire.NameEquals(question.Name, Names.Instance))
                {
                    if (question.Type is DnsWire.TypeSrv or DnsWire.TypeAny)
                    {
                        AddOnce(answers, records.Service);
                        foreach (DnsRecord address in records.Addresses)
                        {
                            AddOnce(additionals, address);
                        }
                    }

                    if (question.Type is DnsWire.TypeTxt or DnsWire.TypeAny)
                    {
                        AddOnce(answers, records.Text);
                    }
                }
                else if (question.Type is DnsWire.TypeA or DnsWire.TypeAny && DnsWire.NameEquals(question.Name, Names.Host))
                {
                    foreach (DnsRecord address in records.Addresses)
                    {
                        AddOnce(answers, address);
                    }
                }
            }

            additionals.RemoveAll(answers.Contains);
            return (answers, additionals);
        }

        // Known-answer suppression (RFC 6762 section 7.1): a querier that already holds our pointer with more than half of its time left needs no answer.
        private bool IsKnownAnswer(DnsPacket packet)
        {
            return packet.Answers.Any(answer =>
                answer.Type == DnsWire.TypePtr
                && answer.Ttl >= ServiceTtl / 2
                && answer.Target is not null
                && DnsWire.NameEquals(answer.Name, Names.ServiceType)
                && DnsWire.NameEquals(answer.Target, Names.Instance));
        }

        private Records Build(MdnsInterface networkInterface, bool goodbye)
        {
            uint serviceTtl = goodbye ? 0 : ServiceTtl;
            uint hostTtl = goodbye ? 0 : HostTtl;

            var enumeration = new DnsRecord(Names.Enumeration, DnsWire.TypePtr, false, serviceTtl, DnsWire.EncodeName(Names.ServiceType));
            var pointer = new DnsRecord(Names.ServiceType, DnsWire.TypePtr, false, serviceTtl, DnsWire.EncodeName(Names.Instance));
            var service = new DnsRecord(Names.Instance, DnsWire.TypeSrv, true, hostTtl, ServiceData(_advertisement.Port, Names.Host));
            var text = new DnsRecord(Names.Instance, DnsWire.TypeTxt, true, serviceTtl, TextData(_advertisement.TxtRecords));
            DnsRecord[] addresses =
            [
                .. networkInterface.Addresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => new DnsRecord(Names.Host, DnsWire.TypeA, true, hostTtl, address.GetAddressBytes())),
            ];
            return new Records(enumeration, pointer, service, text, addresses);
        }

        private async Task SendAsync(int interfaceIndex, byte[] message, IPEndPoint? destination, CancellationToken cancellationToken)
        {
            try
            {
                await _transport.SendAsync(interfaceIndex, message, destination, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
            {
                string reason = exception.GetType().Name;
                DiscoveryLog.SendFailed(_owner._logger, reason);
            }
        }

        private static DnsRecord Legacy(DnsRecord record) => record with { Ttl = Math.Min(record.Ttl, LegacyTtl), CacheFlush = false };

        private static void AddOnce(List<DnsRecord> list, DnsRecord record)
        {
            if (!list.Contains(record))
            {
                list.Add(record);
            }
        }

        private static byte[] ServiceData(int port, IReadOnlyList<string> host)
        {
            byte[] target = DnsWire.EncodeName(host);
            byte[] data = new byte[6 + target.Length];
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)port);
            target.CopyTo(data, 6);
            return data;
        }

        private static byte[] TextData(IReadOnlyDictionary<string, string> records)
        {
            var data = new List<byte>();
            foreach ((string key, string value) in records)
            {
                byte[] entry = Encoding.UTF8.GetBytes(ZeroconfNames.TruncateUtf8(key + "=" + value, 255));
                data.Add((byte)entry.Length);
                data.AddRange(entry);
            }

            if (data.Count == 0)
            {
                // RFC 6763 section 6.1: a TXT record with no strings is a single empty string.
                data.Add(0);
            }

            return [.. data];
        }
    }
}
