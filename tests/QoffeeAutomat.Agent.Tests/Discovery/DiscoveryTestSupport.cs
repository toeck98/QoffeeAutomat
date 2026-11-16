using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Threading.Channels;
using QoffeeAutomat.Agent.Discovery;

namespace QoffeeAutomat.Agent.Tests.Discovery;

/// <summary>A network change source that only reports what a test raises.</summary>
internal sealed class FakeNetworkChangeSource : INetworkChangeSource
{
    private readonly object _gate = new();
    private readonly List<Action> _watchers = [];

    public int WatcherCount
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    public IDisposable Watch(Action changed)
    {
        lock (_gate)
        {
            _watchers.Add(changed);
        }

        return new Watcher(this, changed);
    }

    public void Raise()
    {
        Action[] watchers;
        lock (_gate)
        {
            watchers = [.. _watchers];
        }

        foreach (Action watcher in watchers)
        {
            watcher();
        }
    }

    private sealed class Watcher : IDisposable
    {
        private readonly FakeNetworkChangeSource _owner;
        private readonly Action _action;

        public Watcher(FakeNetworkChangeSource owner, Action action)
        {
            _owner = owner;
            _action = action;
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _owner._watchers.Remove(_action);
            }
        }
    }
}

/// <summary>A datagram that the advertiser sent.</summary>
internal sealed record SentDatagram(int InterfaceIndex, IPEndPoint? Destination, byte[] Data);

/// <summary>A multicast transport without sockets: it records what is sent and delivers what a test injects.</summary>
internal sealed class FakeMdnsTransport : IMdnsTransport
{
    private readonly object _gate = new();
    private readonly List<SentDatagram> _sent = [];
    private readonly Channel<MdnsDatagram> _incoming = Channel.CreateUnbounded<MdnsDatagram>();

    public FakeMdnsTransport(IReadOnlyList<MdnsInterface> interfaces)
    {
        Interfaces = interfaces;
    }

    public IReadOnlyList<MdnsInterface> Interfaces { get; }

    public bool IsDisposed { get; private set; }

    public IReadOnlyList<SentDatagram> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public ValueTask SendAsync(int interfaceIndex, ReadOnlyMemory<byte> datagram, IPEndPoint? destination, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _sent.Add(new SentDatagram(interfaceIndex, destination, datagram.ToArray()));
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        return _incoming.Reader.ReadAllAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Deliver(int interfaceIndex, int remotePort, byte[] datagram)
    {
        _incoming.Writer.TryWrite(new MdnsDatagram(interfaceIndex, new IPEndPoint(IPAddress.Parse("192.168.1.50"), remotePort), datagram));
    }
}

/// <summary>Opens fake transports and counts how often that happened, which is how the tests see whether a socket would have been opened.</summary>
internal sealed class FakeMdnsTransportFactory : IMdnsTransportFactory
{
    public static readonly MdnsInterface DefaultInterface = new(2, "eth0", [IPAddress.Parse("192.168.1.20")]);

    private readonly object _gate = new();
    private readonly List<FakeMdnsTransport> _opened = [];

    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _opened.Count;
            }
        }
    }

    public FakeMdnsTransport? Last
    {
        get
        {
            lock (_gate)
            {
                return _opened.Count == 0 ? null : _opened[^1];
            }
        }
    }

    public IReadOnlyList<MdnsInterface> Interfaces { get; set; } = [DefaultInterface];

    public IMdnsTransport Open()
    {
        var transport = new FakeMdnsTransport(Interfaces);
        lock (_gate)
        {
            _opened.Add(transport);
        }

        return transport;
    }
}

/// <summary>
/// A DNS message decoder that is independent of the agent's own, so the tests check the bytes on the wire and not the
/// code against itself. Names are decoded with compression, as a real resolver would.
/// </summary>
internal sealed class DecodedMessage
{
    private DecodedMessage()
    {
    }

    public ushort Id { get; private init; }

    public ushort Flags { get; private init; }

    public List<(string Name, ushort Type, ushort Class)> Questions { get; } = [];

    public List<DecodedRecord> Answers { get; } = [];

    public List<DecodedRecord> Additionals { get; } = [];

    public IEnumerable<DecodedRecord> AllRecords => Answers.Concat(Additionals);

    public static DecodedMessage Parse(byte[] data)
    {
        var message = new DecodedMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0)),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)),
        };
        int questions = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        int answers = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
        int authorities = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8));
        int additionals = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
        Assert.Equal(0, authorities);

        int offset = 12;
        for (int index = 0; index < questions; index++)
        {
            string name = ReadName(data, ref offset);
            message.Questions.Add((name, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2))));
            offset += 4;
        }

        for (int index = 0; index < answers + additionals; index++)
        {
            DecodedRecord record = ReadRecord(data, ref offset);
            (index < answers ? message.Answers : message.Additionals).Add(record);
        }

        Assert.Equal(data.Length, offset);
        return message;
    }

    private static DecodedRecord ReadRecord(byte[] data, ref int offset)
    {
        string name = ReadName(data, ref offset);
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        ushort recordClass = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));
        int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
        int start = offset + 10;
        byte[] rdata = data.AsSpan(start, length).ToArray();
        offset = start + length;

        string? target = null;
        int port = 0;
        var text = new List<string>();
        IPAddress? address = null;
        switch (type)
        {
            case 12:
                int pointerOffset = start;
                target = ReadName(data, ref pointerOffset);
                break;
            case 33:
                port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + 4));
                int hostOffset = start + 6;
                target = ReadName(data, ref hostOffset);
                break;
            case 16:
                int position = 0;
                while (position < rdata.Length)
                {
                    int size = rdata[position];
                    text.Add(Encoding.UTF8.GetString(rdata, position + 1, size));
                    position += 1 + size;
                }

                break;
            case 1:
                address = new IPAddress(rdata);
                break;
        }

        return new DecodedRecord(name, type, (recordClass & 0x8000) != 0, ttl, target, port, text, address);
    }

    private static string ReadName(byte[] data, ref int offset)
    {
        var labels = new List<string>();
        int position = offset;
        int end = -1;
        while (true)
        {
            int length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (end < 0)
                {
                    end = position + 2;
                }

                position = ((length & 0x3F) << 8) | data[position + 1];
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(data, position + 1, length));
            position += 1 + length;
        }

        offset = end >= 0 ? end : position;
        return string.Join('.', labels) + ".";
    }
}

/// <summary>A decoded resource record.</summary>
internal sealed record DecodedRecord(
    string Name,
    ushort Type,
    bool CacheFlush,
    uint Ttl,
    string? Target,
    int Port,
    IReadOnlyList<string> Text,
    IPAddress? Address);

/// <summary>Builds the queries that other hosts send.</summary>
internal static class DnsQueries
{
    public static byte[] Question(string name, ushort type, ushort id = 0, ushort questionClass = 1, string? knownPointerTarget = null)
    {
        var bytes = new List<byte>();
        AddUInt16(bytes, id);
        AddUInt16(bytes, 0);
        AddUInt16(bytes, 1);
        AddUInt16(bytes, (ushort)(knownPointerTarget is null ? 0 : 1));
        AddUInt16(bytes, 0);
        AddUInt16(bytes, 0);
        AddName(bytes, name);
        AddUInt16(bytes, type);
        AddUInt16(bytes, questionClass);
        if (knownPointerTarget is not null)
        {
            AddName(bytes, name);
            AddUInt16(bytes, 12);
            AddUInt16(bytes, 1);
            bytes.AddRange([0, 0, 0x11, 0x94]);
            var target = new List<byte>();
            AddName(target, knownPointerTarget);
            AddUInt16(bytes, (ushort)target.Count);
            bytes.AddRange(target);
        }

        return [.. bytes];
    }

    private static void AddName(List<byte> bytes, string name)
    {
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] encoded = Encoding.UTF8.GetBytes(label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value & 0xFF));
    }
}
