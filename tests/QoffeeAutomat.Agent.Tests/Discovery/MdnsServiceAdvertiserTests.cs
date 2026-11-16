using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Discovery;

/// <summary>The multicast DNS responder (T216) on a transport without sockets; the bytes it sends are decoded independently.</summary>
[Trait("Category", "Discovery")]
public sealed class MdnsServiceAdvertiserTests : IAsyncDisposable
{
    private const string ServiceType = "_qoffeeautomat._tcp.local.";
    private const string AgentId = "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77";

    private readonly FakeMdnsTransportFactory _factory = new();
    private readonly SimulatedClock _clock = new();
    private readonly SimulatedPlatformInfo _platform = new("GAMING-PC");
    private readonly MdnsServiceAdvertiser _advertiser;

    public MdnsServiceAdvertiserTests()
    {
        _advertiser = new MdnsServiceAdvertiser(_factory, _platform, _clock, NullLogger<MdnsServiceAdvertiser>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _advertiser.DisposeAsync();
        _platform.Dispose();
    }

    private static ServiceAdvertisement Advertisement(string name = "Gaming-PC", int port = 8481) => new(
        ServiceType,
        name,
        port,
        new Dictionary<string, string>
        {
            ["id"] = AgentId,
            ["name"] = name,
            ["v"] = "1.0.0",
            ["pv"] = "1-1",
        });

    private static DecodedMessage Decode(SentDatagram datagram) => DecodedMessage.Parse(datagram.Data);

    [Fact]
    public async Task Nothing_is_opened_until_an_advertisement_starts()
    {
        Assert.Equal(0, _factory.OpenCount);
        Assert.False(_advertiser.IsAdvertising);

        await _advertiser.StopAsync(CancellationToken.None);

        Assert.Equal(0, _factory.OpenCount);
    }

    [Fact]
    public async Task Start_announces_pointer_service_text_and_address_on_every_interface()
    {
        var second = new MdnsInterface(3, "wlan0", [IPAddress.Parse("10.0.0.7")]);
        _factory.Interfaces = [FakeMdnsTransportFactory.DefaultInterface, second];

        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);

        Assert.True(_advertiser.IsAdvertising);
        FakeMdnsTransport transport = _factory.Last!;
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal([2, 3], transport.Sent.Select(sent => sent.InterfaceIndex));
        Assert.All(transport.Sent, sent => Assert.Null(sent.Destination));

        DecodedMessage first = Decode(transport.Sent[0]);
        Assert.Equal(0x8400, first.Flags);
        Assert.Empty(first.Questions);
        Assert.Equal(0, first.Id);

        DecodedRecord enumeration = Assert.Single(first.Answers, record => record.Type == 12 && record.Name == "_services._dns-sd._udp.local.");
        Assert.Equal(ServiceType, enumeration.Target);
        Assert.False(enumeration.CacheFlush);
        Assert.Equal(4500u, enumeration.Ttl);

        DecodedRecord pointer = Assert.Single(first.Answers, record => record.Type == 12 && record.Name == ServiceType);
        Assert.Equal("Gaming-PC._qoffeeautomat._tcp.local.", pointer.Target);
        Assert.False(pointer.CacheFlush);
        Assert.Equal(4500u, pointer.Ttl);

        DecodedRecord service = Assert.Single(first.Answers, record => record.Type == 33);
        Assert.Equal("Gaming-PC._qoffeeautomat._tcp.local.", service.Name);
        Assert.Equal(8481, service.Port);
        Assert.Equal("gaming-pc.local.", service.Target);
        Assert.True(service.CacheFlush);
        Assert.Equal(120u, service.Ttl);

        DecodedRecord text = Assert.Single(first.Answers, record => record.Type == 16);
        Assert.Equal("Gaming-PC._qoffeeautomat._tcp.local.", text.Name);
        Assert.Equal(["id=" + AgentId, "name=Gaming-PC", "v=1.0.0", "pv=1-1"], text.Text);
        Assert.True(text.CacheFlush);

        // Each interface announces its own addresses.
        DecodedRecord firstAddress = Assert.Single(first.Answers, record => record.Type == 1);
        Assert.Equal("gaming-pc.local.", firstAddress.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), firstAddress.Address);
        DecodedRecord secondAddress = Assert.Single(Decode(transport.Sent[1]).Answers, record => record.Type == 1);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), secondAddress.Address);
    }

    [Fact]
    public async Task The_announcement_is_repeated_once_after_a_second()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;
        Assert.Single(transport.Sent);

        // The timer of the repeat is created on a background task; keep moving the simulated clock until it fires.
        await TestWait.UntilAsync(
            () =>
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                return transport.Sent.Count >= 2;
            },
            "the second announcement");

        _clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task A_query_for_the_service_type_is_answered_with_the_pointer_and_the_records_that_belong_to_it()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;
        transport.Deliver(2, 5353, DnsQueries.Question(ServiceType, 12));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer");

        SentDatagram reply = transport.Sent[1];
        Assert.Equal(2, reply.InterfaceIndex);
        Assert.Null(reply.Destination);
        DecodedMessage message = Decode(reply);
        Assert.Equal(0x8400, message.Flags);
        DecodedRecord pointer = Assert.Single(message.Answers);
        Assert.Equal("Gaming-PC._qoffeeautomat._tcp.local.", pointer.Target);
        Assert.Contains(message.Additionals, record => record.Type == 33 && record.Port == 8481);
        Assert.Contains(message.Additionals, record => record.Type == 16);
        Assert.Contains(message.Additionals, record => record.Type == 1 && Equals(record.Address, IPAddress.Parse("192.168.1.20")));
    }

    [Fact]
    public async Task A_query_for_the_service_type_enumeration_is_answered_with_the_service_type()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;
        transport.Deliver(2, 5353, DnsQueries.Question("_services._dns-sd._udp.local.", 12));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer");

        DecodedRecord answer = Assert.Single(Decode(transport.Sent[1]).Answers);
        Assert.Equal(ServiceType, answer.Target);
    }

    [Fact]
    public async Task A_query_for_the_instance_and_for_the_host_name_is_answered()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;

        transport.Deliver(2, 5353, DnsQueries.Question("Gaming-PC._qoffeeautomat._tcp.local.", 255));
        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer for the instance");
        DecodedMessage instance = Decode(transport.Sent[1]);
        Assert.Contains(instance.Answers, record => record.Type == 33);
        Assert.Contains(instance.Answers, record => record.Type == 16);

        transport.Deliver(2, 5353, DnsQueries.Question("gaming-pc.local.", 1));
        await TestWait.UntilAsync(() => transport.Sent.Count == 3, "the answer for the host");
        DecodedRecord address = Assert.Single(Decode(transport.Sent[2]).Answers);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), address.Address);
    }

    [Fact]
    public async Task Names_are_compared_without_regard_to_case()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;
        transport.Deliver(2, 5353, DnsQueries.Question("_QoffeeAutomat._TCP.local.", 12));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer");
    }

    [Fact]
    public async Task A_querier_that_already_knows_the_pointer_gets_no_answer_for_it()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;

        // The first query lists our pointer as a known answer with a long time to live; the second is a barrier that is answered.
        transport.Deliver(2, 5353, DnsQueries.Question(ServiceType, 12, knownPointerTarget: "Gaming-PC._qoffeeautomat._tcp.local."));
        transport.Deliver(2, 5353, DnsQueries.Question("Gaming-PC._qoffeeautomat._tcp.local.", 16));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer to the second query");
        await Task.Delay(100);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(16, Assert.Single(Decode(transport.Sent[1]).Answers).Type);
    }

    [Fact]
    public async Task Queries_for_other_names_and_responses_of_other_hosts_get_no_answer()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;

        transport.Deliver(2, 5353, DnsQueries.Question("someone-else.local.", 1));
        transport.Deliver(2, 5353, DnsQueries.Question("_other._tcp.local.", 12));
        transport.Deliver(2, 5353, [1, 2, 3]);
        byte[] response = DnsQueries.Question(ServiceType, 12);
        response[2] = 0x84; // QR set: a response, which is not answered.
        transport.Deliver(2, 5353, response);
        transport.Deliver(2, 5353, DnsQueries.Question("Gaming-PC._qoffeeautomat._tcp.local.", 16));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer to the last query");
        await Task.Delay(100);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task A_datagram_from_an_unknown_interface_is_ignored()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;

        transport.Deliver(99, 5353, DnsQueries.Question(ServiceType, 12));
        transport.Deliver(2, 5353, DnsQueries.Question("Gaming-PC._qoffeeautomat._tcp.local.", 16));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the answer to the second query");
        await Task.Delay(100);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task A_resolver_that_is_not_a_multicast_responder_gets_a_direct_answer_with_its_id_a_short_ttl_and_no_cache_flush()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;
        transport.Deliver(2, 54321, DnsQueries.Question("Gaming-PC._qoffeeautomat._tcp.local.", 16, id: 0x1234));

        await TestWait.UntilAsync(() => transport.Sent.Count == 2, "the direct answer");

        SentDatagram reply = transport.Sent[1];
        Assert.Equal(54321, reply.Destination?.Port);
        DecodedMessage message = Decode(reply);
        Assert.Equal(0x1234, message.Id);
        Assert.Equal(("Gaming-PC._qoffeeautomat._tcp.local.", (ushort)16, (ushort)1), Assert.Single(message.Questions));
        DecodedRecord text = Assert.Single(message.Answers);
        Assert.Equal(10u, text.Ttl);
        Assert.False(text.CacheFlush);
    }

    [Fact]
    public async Task Stopping_sends_a_goodbye_and_closes_the_transport()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport transport = _factory.Last!;

        await _advertiser.StopAsync(CancellationToken.None);

        Assert.False(_advertiser.IsAdvertising);
        Assert.True(transport.IsDisposed);
        DecodedMessage goodbye = Decode(transport.Sent[^1]);
        Assert.NotEmpty(goodbye.Answers);
        Assert.All(goodbye.Answers, record => Assert.Equal(0u, record.Ttl));
        Assert.Contains(goodbye.Answers, record => record.Type == 12 && record.Name == ServiceType);
    }

    [Fact]
    public async Task Starting_with_a_new_name_withdraws_the_old_name_and_announces_the_new_one()
    {
        await _advertiser.StartAsync(Advertisement("Gaming-PC"), CancellationToken.None);
        FakeMdnsTransport first = _factory.Last!;

        await _advertiser.StartAsync(Advertisement("Study-PC"), CancellationToken.None);

        Assert.Equal(2, _factory.OpenCount);
        Assert.True(first.IsDisposed);
        DecodedMessage goodbye = Decode(first.Sent[^1]);
        Assert.All(goodbye.Answers, record => Assert.Equal(0u, record.Ttl));
        Assert.Contains(goodbye.Answers, record => record.Target == "Gaming-PC._qoffeeautomat._tcp.local.");

        DecodedMessage announcement = Decode(_factory.Last!.Sent[0]);
        Assert.Contains(announcement.Answers, record => record.Type == 12 && record.Target == "Study-PC._qoffeeautomat._tcp.local." && record.Ttl == 4500);
    }

    [Fact]
    public async Task Starting_again_with_the_same_service_opens_the_sockets_afresh_without_a_goodbye()
    {
        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);
        FakeMdnsTransport first = _factory.Last!;

        await _advertiser.StartAsync(Advertisement(), CancellationToken.None);

        Assert.Equal(2, _factory.OpenCount);
        Assert.True(first.IsDisposed);
        Assert.Single(first.Sent);
        Assert.DoesNotContain(first.Sent, sent => Decode(sent).Answers.Any(record => record.Ttl == 0));
        Assert.True(_advertiser.IsAdvertising);
    }

    [Fact]
    public async Task A_long_agent_name_becomes_a_valid_label_and_a_valid_text_entry()
    {
        string name = new('é', 100);

        await _advertiser.StartAsync(Advertisement(name), CancellationToken.None);

        DecodedMessage message = Decode(_factory.Last!.Sent[0]);
        DecodedRecord pointer = Assert.Single(message.Answers, record => record.Type == 12 && record.Name == ServiceType);
        string label = pointer.Target!.Split('.')[0];
        Assert.InRange(Encoding.UTF8.GetByteCount(label), 1, 63);
        DecodedRecord text = Assert.Single(message.Answers, record => record.Type == 16);
        Assert.All(text.Text, entry => Assert.True(Encoding.UTF8.GetByteCount(entry) <= 255));
    }

    [Fact]
    public async Task An_invalid_service_type_or_port_is_rejected_before_anything_is_opened()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await _advertiser.StartAsync(new ServiceAdvertisement("nonsense", "Gaming-PC", 8481, new Dictionary<string, string>()), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await _advertiser.StartAsync(Advertisement(port: 0), CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await _advertiser.StartAsync(Advertisement(port: 70000), CancellationToken.None));

        Assert.Equal(0, _factory.OpenCount);
    }

    [Fact]
    public async Task A_disposed_advertiser_refuses_to_start()
    {
        await _advertiser.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await _advertiser.StartAsync(Advertisement(), CancellationToken.None));
        Assert.Equal(0, _factory.OpenCount);
    }

    [Theory]
    [InlineData("GAMING-PC", "gaming-pc")]
    [InlineData("GAMING-PC.fritz.box", "gaming-pc")]
    [InlineData("Büro PC", "b-ro-pc")]
    [InlineData("---", "qoffeeautomat")]
    [InlineData("", "qoffeeautomat")]
    public void The_host_label_is_a_lower_case_dns_label(string hostname, string expected)
    {
        Assert.Equal(expected, ZeroconfNames.ToHostLabel(hostname));
    }

    [Fact]
    public void Truncation_never_splits_a_character_and_the_instance_label_has_at_most_63_bytes()
    {
        string emoji = "\U0001F600";

        Assert.Equal(emoji, ZeroconfNames.TruncateUtf8(emoji + emoji, 5));
        Assert.Equal("abc", ZeroconfNames.TruncateUtf8("abcdef", 3));
        Assert.Equal("short", ZeroconfNames.TruncateUtf8("short", 100));
        Assert.Equal("QoffeeAutomat", ZeroconfNames.ToInstanceLabel("  \t "));
        Assert.Equal("a b", ZeroconfNames.ToInstanceLabel("a\nb"));
        Assert.True(Encoding.UTF8.GetByteCount(ZeroconfNames.ToInstanceLabel(new string('x', 200))) <= 63);
    }

    [Fact]
    public void The_protocol_range_is_min_dash_max()
    {
        Assert.Equal("1-1", ZeroconfNames.ProtocolRange);
    }
}
