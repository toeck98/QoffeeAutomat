using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>
/// The reverse link of the whole agent (T710, ADR 0003): the agent in the simulated platform dials the endpoint of the fake Home Assistant and
/// serves QoffeeLink on that connection. The agent has no listener that the peer could reach in these tests: nothing is paired, and every frame
/// travels over the connection that the agent opened, as it would behind a NAT.
/// </summary>
[Trait("Category", "OutboundLink")]
public sealed class ReverseLinkTests
{
    private const string RunLevel3 = "\"command\":\"" + LinkTestHost.CommandId + "\",\"vars\":{\"Level\":3}";

    [Fact]
    public async Task An_agent_behind_NAT_without_inbound_connectivity_is_fully_usable_from_the_peer()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        await host.AddCommandAsync();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();

        ReverseLinkJoinResult joined = await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
        await using LinkTestClient peer = await ha.WaitForAgentAsync();

        // The agent speaks first, no matter who dialled (S07).
        JsonElement hello = peer.Frames[0];
        Assert.Equal(MessageTypes.Hello, LinkTestClient.TypeOf(hello));
        Assert.Equal(ProtocolInfo.Version, hello.GetProperty("protocol").GetProperty("max").GetInt32());
        Assert.Equal(host.ManifestSource.Current.Rev, hello.GetProperty("manifest_rev").GetString());
        await peer.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));

        // The manifest, with the command of the agent.
        JsonElement manifest = await peer.RequestAsync(MessageTypes.GetManifest);
        Assert.True(manifest.GetProperty("ok").GetBoolean());
        Assert.Contains(
            manifest.GetProperty("data").GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("id").GetString() == LinkTestHost.CommandId);

        // Subscribing delivers a full snapshot of the metrics.
        JsonElement subscribed = await peer.RequestAsync(MessageTypes.Subscribe, "\"metrics\":\"*\"");
        Assert.True(subscribed.GetProperty("ok").GetBoolean());
        JsonElement state = await peer.WaitForTypeAsync(MessageTypes.State);
        Assert.NotEmpty(LinkTestClient.MetricsOf(state));

        // A command runs on the PC, with its variable bound as a separate argument.
        JsonElement ran = await peer.RequestAsync(MessageTypes.Run, RunLevel3);
        Assert.True(ran.GetProperty("ok").GetBoolean());
        Assert.Equal(0, ran.GetProperty("data").GetProperty("exit_code").GetInt32());
        ProcessLaunchRequest launch = Assert.Single(host.Launcher.Launches);
        Assert.Equal(["--level", "3"], launch.Arguments.ToArray());

        // Nothing went through the inbound side: no client was paired and the token was only presented to the peer.
        Assert.Empty(await host.Pairing.ListClientsAsync(CancellationToken.None));
        Assert.Equal(1, host.Server.ConnectionCount);
        Assert.Equal("Bearer " + ha.Token, Assert.Single(ha.Presented));
        Assert.Empty(peer.Problems);
    }

    [Fact]
    public async Task An_unknown_frame_does_not_end_a_reverse_connection()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        await using LinkTestClient peer = await ha.WaitForAgentAsync();

        await peer.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));
        await peer.RequestAsync(MessageTypes.GetManifest);

        // A second client_hello or an unknown frame does not end the connection: forward compatibility works over the reverse link as well.
        await peer.SendAsync("{\"t\":\"from_the_future\",\"x\":1}");
        JsonElement again = await peer.RequestAsync(MessageTypes.GetManifest);
        Assert.True(again.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_laptop_that_goes_to_sleep_says_bye_over_the_connection_and_comes_back_by_itself()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        await using LinkTestClient first = await ha.WaitForAgentAsync();
        await first.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));

        await Task.Run(() => host.Platform.Power.Simulate(PowerEventKind.Sleep));

        JsonElement bye = await first.WaitForTypeAsync(MessageTypes.Bye);
        Assert.Equal(ByeReasons.Sleep, bye.GetProperty("reason").GetString());
        Assert.Equal(CloseCodes.GoingAway, await first.WaitForCloseAsync());

        // After the resume the agent dials again, without anybody asking it to.
        await Task.Run(() => host.Platform.Power.Simulate(PowerEventKind.Resume));
        await using LinkTestClient second = await ha.WaitForAgentAsync();
        Assert.Equal(MessageTypes.Hello, LinkTestClient.TypeOf(second.Frames[0]));
    }

    [Fact]
    public async Task The_agent_dials_again_after_the_peer_restarted()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        await using LinkTestClient first = await ha.WaitForAgentAsync();
        await first.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));

        await first.CloseAsync();

        await using LinkTestClient second = await ha.WaitForAgentAsync();
        await second.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));
        JsonElement manifest = await second.RequestAsync(MessageTypes.GetManifest);
        Assert.True(manifest.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_token_that_the_peer_no_longer_knows_stops_the_agent_until_it_is_enrolled_again()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        ReverseLinkService service = host.Agent.Services.GetRequiredService<ReverseLinkService>();
        await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        await using LinkTestClient first = await ha.WaitForAgentAsync();
        await first.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));

        // Home Assistant rotates the token and drops the connection.
        ha.Token = ReverseLinkEnrollment.CreateToken(RandomNumberGenerator.GetBytes(32));
        await first.CloseAsync();

        await TestWait.UntilAsync(() => service.Connector?.State == OutboundLinkState.Rejected, "the agent to be rejected");
        int dials = ha.Dials.Length;
        await Task.Delay(300);
        Assert.Equal(dials, ha.Dials.Length);

        // The user enrols the agent again with the new token.
        ReverseLinkJoinResult joined = await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
        await using LinkTestClient second = await ha.WaitForAgentAsync();
        Assert.Equal(MessageTypes.Hello, LinkTestClient.TypeOf(second.Frames[0]));
    }

    [Fact]
    public async Task Leaving_ends_the_connection_and_the_agent_does_not_dial_without_a_token()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        ReverseLinkService service = host.Agent.Services.GetRequiredService<ReverseLinkService>();

        // Without a token the agent does not dial at all.
        await TestWait.UntilAsync(() => service.Connector?.State == OutboundLinkState.Idle, "the link to be idle");
        await Task.Delay(200);
        Assert.Empty(ha.Dials);

        await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        await using LinkTestClient peer = await ha.WaitForAgentAsync();
        await peer.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));

        Assert.True(await manager.LeaveAsync(CancellationToken.None));

        await peer.WaitForCloseAsync();
        await TestWait.UntilAsync(() => service.Connector?.State == OutboundLinkState.Idle, "the link to be idle after leaving");
        int dials = ha.Dials.Length;
        await Task.Delay(300);
        Assert.Equal(dials, ha.Dials.Length);
        Assert.Equal(0, host.Server.ConnectionCount);
    }

    [Fact]
    public async Task Stopping_the_agent_says_bye_to_the_peer_of_the_reverse_link()
    {
        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        await manager.JoinAsync(ha.EnrollmentString, CancellationToken.None);
        await using LinkTestClient peer = await ha.WaitForAgentAsync();
        await peer.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));

        await host.Server.StopAsync(ByeReasons.ServiceStop, CancellationToken.None);

        JsonElement bye = await peer.WaitForTypeAsync(MessageTypes.Bye);
        Assert.Equal(ByeReasons.ServiceStop, bye.GetProperty("reason").GetString());
        int dials = ha.Dials.Length;
        await Task.Delay(300);
        Assert.Equal(dials, ha.Dials.Length);
    }
}
