using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using LinkOptions = QoffeeAutomat.Agent.Link.LinkOptions;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>
/// How a QoffeeLink connection ends (T210, PLAN.md S07): <c>bye</c> before power events and when the agent stops,
/// revoked tokens, and clients that cannot keep up. The slow clients are a WebSocket that never accepts a frame.
/// </summary>
[Trait("Category", "Link")]
public sealed class LinkLifecycleTests
{
    [Theory]
    [InlineData(PowerEventKind.Sleep, ByeReasons.Sleep)]
    [InlineData(PowerEventKind.Hibernate, ByeReasons.Hibernate)]
    [InlineData(PowerEventKind.Shutdown, ByeReasons.Shutdown)]
    [InlineData(PowerEventKind.Restart, ByeReasons.Restart)]
    public async Task A_power_event_that_takes_the_pc_away_is_announced_with_bye_and_the_close_code_1001(PowerEventKind kind, string reason)
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient first = await host.ConnectAsync();
        await using LinkTestClient second = await host.ConnectAsync();

        // The handler of the platform waits until the frames are written, so the PC may go to sleep right after it.
        await Task.Run(() => host.Platform.Power.Simulate(kind));

        foreach (LinkTestClient client in new[] { first, second })
        {
            JsonElement bye = await client.WaitForTypeAsync(MessageTypes.Bye);
            Assert.Equal(reason, bye.GetProperty("reason").GetString());
            Assert.Equal(CloseCodes.GoingAway, await client.WaitForCloseAsync());
        }

        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the connections to be removed");
    }

    [Fact]
    public async Task Waking_up_does_not_say_bye_and_the_connection_stays_usable()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await Task.Run(() => host.Platform.Power.Simulate(PowerEventKind.Resume));

        JsonElement resumed = await client.WaitForAsync(
            frame => LinkTestClient.TypeOf(frame) == MessageTypes.Event && frame.GetProperty("type").GetString() == EventTypes.Resumed,
            "the resumed event");
        JsonElement result = await client.RequestAsync(MessageTypes.GetManifest);
        Assert.Equal(EventTypes.Resumed, resumed.GetProperty("type").GetString());
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Empty(client.FramesOf(MessageTypes.Bye));
        Assert.True(client.IsOpen);
    }

    [Fact]
    public async Task Stopping_the_server_says_bye_with_service_stop_and_refuses_new_connections_with_503()
    {
        await using var host = new LinkTestHost();
        (string token, _) = await host.PairAsync();
        await using LinkTestClient client = await LinkTestClient.ConnectAndGreetAsync(host.Agent, token);

        await host.Server.StopAsync(ByeReasons.ServiceStop, CancellationToken.None);

        JsonElement bye = await client.WaitForTypeAsync(MessageTypes.Bye);
        Assert.Equal(ByeReasons.ServiceStop, bye.GetProperty("reason").GetString());
        Assert.Equal(CloseCodes.GoingAway, await client.WaitForCloseAsync());
        Assert.Equal(0, host.Server.ConnectionCount);
        Assert.True(host.Server.IsStopping);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LinkTestClient.OpenSocketAsync(host.Agent, token));
        Assert.Contains("503", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_the_agent_tells_the_connected_clients_before_the_web_server_goes()
    {
        var host = new LinkTestHost();
        await using (host)
        {
            await using LinkTestClient client = await host.ConnectAsync();

            await host.DisposeAsync();

            JsonElement bye = await client.WaitForTypeAsync(MessageTypes.Bye);
            Assert.Equal(ByeReasons.ServiceStop, bye.GetProperty("reason").GetString());
            Assert.Equal(CloseCodes.GoingAway, await client.WaitForCloseAsync());
        }
    }

    [Fact]
    public async Task Revoking_the_token_closes_every_live_connection_of_that_client_with_4401_and_leaves_the_others_alone()
    {
        await using var host = new LinkTestHost();
        (string token, LinkClient owner) = await host.PairAsync();
        await using LinkTestClient first = await LinkTestClient.ConnectAndGreetAsync(host.Agent, token);
        await using LinkTestClient second = await LinkTestClient.ConnectAndGreetAsync(host.Agent, token);
        await using LinkTestClient bystander = await host.ConnectAsync();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 3, "all three connections to be registered");

        Assert.True(await host.Pairing.RevokeAsync(owner.Id, CancellationToken.None));

        Assert.Equal(CloseCodes.Unauthorized, await first.WaitForCloseAsync());
        Assert.Equal(CloseCodes.Unauthorized, await second.WaitForCloseAsync());
        JsonElement result = await bystander.RequestAsync(MessageTypes.GetManifest);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(bystander.IsOpen);
        Assert.Null(bystander.CloseStatus);
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 1, "the revoked connections to be removed");
    }

    [Theory]
    [InlineData(4, 8L * 1024 * 1024)]
    [InlineData(256, 1024L)]
    public async Task A_client_that_stops_reading_is_dropped_when_its_send_queue_is_full_and_nobody_else_is_affected(int capacity, long maxBytes)
    {
        await using var host = new LinkTestHost(new LinkOptions { SendQueueCapacity = capacity, SendQueueMaxBytes = maxBytes });
        (_, LinkClient owner) = await host.PairAsync();
        await using LinkTestClient healthy = await host.ConnectAsync();
        using var stalled = new StalledWebSocket();
        Task serving = host.Server.ServeAsync(stalled, owner, CancellationToken.None);
        stalled.Receive(LinkTestClient.ClientHello(ProtocolInfo.Version));
        await TestWait.UntilAsync(() => stalled.SendAttempts == 1, "hello to reach the stalled socket");
        Assert.Equal(2, host.Server.ConnectionCount);

        // Every request makes the agent queue a manifest for a client that does not read anything.
        for (int request = 1; request <= 12; request++)
        {
            stalled.Receive("{\"t\":\"get_manifest\",\"id\":" + request.ToString(CultureInfo.InvariantCulture) + "}");
        }

        await TestWait.WithTimeoutAsync(serving, "the stalled client to be dropped");
        Assert.True(stalled.WasAborted);
        Assert.Equal(1, stalled.SendAttempts);
        Assert.Equal(1, host.Server.ConnectionCount);
        JsonElement result = await healthy.RequestAsync(MessageTypes.GetManifest);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(healthy.IsOpen);
    }

    [Fact]
    public async Task A_client_whose_socket_does_not_accept_a_frame_in_time_is_dropped()
    {
        await using var host = new LinkTestHost(new LinkOptions { SendTimeout = TimeSpan.FromMilliseconds(300) });
        (_, LinkClient owner) = await host.PairAsync();
        using var stalled = new StalledWebSocket();

        Task serving = host.Server.ServeAsync(stalled, owner, CancellationToken.None);

        await TestWait.WithTimeoutAsync(serving, "the stalled client to be dropped");
        Assert.True(stalled.WasAborted);
        Assert.Equal(0, host.Server.ConnectionCount);
    }

    [Fact]
    public async Task A_client_that_does_not_take_the_bye_frame_does_not_hold_up_the_others()
    {
        await using var host = new LinkTestHost(new LinkOptions
        {
            ByeFlushTimeout = TimeSpan.FromMilliseconds(200),
            CloseTimeout = TimeSpan.FromMilliseconds(300),
        });
        (_, LinkClient owner) = await host.PairAsync();
        await using LinkTestClient healthy = await host.ConnectAsync();
        using var stalled = new StalledWebSocket();
        Task serving = host.Server.ServeAsync(stalled, owner, CancellationToken.None);
        stalled.Receive(LinkTestClient.ClientHello(ProtocolInfo.Version));
        await TestWait.UntilAsync(() => stalled.SendAttempts == 1, "hello to reach the stalled socket");

        int told = await TestWait.WithTimeoutAsync(
            host.Server.SayByeAsync(ByeReasons.Sleep, CancellationToken.None),
            "the bye frames to be handed over",
            TimeSpan.FromSeconds(5));

        Assert.Equal(2, told);
        JsonElement bye = await healthy.WaitForTypeAsync(MessageTypes.Bye);
        Assert.Equal(ByeReasons.Sleep, bye.GetProperty("reason").GetString());
        Assert.Equal(CloseCodes.GoingAway, await healthy.WaitForCloseAsync());
        stalled.Resume();
        await TestWait.WithTimeoutAsync(serving, "the stalled connection to end");
    }
}
