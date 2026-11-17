using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>
/// The dial-out loop that the reverse link and the hub link share (ADR 0003, T710, T905): when it dials, how it backs off, what ends it and
/// what a rejected credential does. The peer is the fake Home Assistant endpoint; the session is a stand-in that only holds the socket open.
/// </summary>
[Trait("Category", "OutboundLink")]
public sealed class OutboundConnectorTests
{
    [Fact]
    public async Task The_connector_dials_with_the_credential_and_runs_the_session_on_the_open_socket()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        bool sessionStarted = false;
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), async (socket, token) =>
        {
            sessionStarted = true;
            return await HoldAsync(socket, token);
        });

        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Connected);

        Assert.True(sessionStarted);
        OutboundEndpoint dialled = Assert.Single(peer.Dials);
        Assert.Equal(FakeHomeAssistant.Url, dialled.Url.AbsoluteUri);
        Assert.Equal("Bearer " + peer.Token, Assert.Single(peer.Presented));
        Assert.Equal(1, peer.Accepted);
        Assert.Equal(0, connector.FailedAttempts);
    }

    [Fact]
    public async Task The_connector_stays_idle_until_there_is_an_endpoint_to_dial()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        bool configured = false;
        await using OutboundConnector connector = Create(peer, () => configured ? Endpoint(peer.Token) : null, HoldAsync);

        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Idle);
        Assert.Empty(peer.Dials);

        configured = true;
        connector.Reconfigure();

        await UntilAsync(connector, OutboundLinkState.Connected);
        Assert.Single(peer.Dials);
    }

    [Fact]
    public async Task A_peer_that_cannot_be_reached_is_retried_and_the_delays_start_again_once_it_is_back()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        peer.Online = false;
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), HoldAsync);

        connector.Start();
        await TestWait.UntilAsync(() => peer.Dials.Length >= 3, "three failed attempts");
        Assert.True(connector.FailedAttempts >= 2);
        Assert.Contains(connector.State, new[] { OutboundLinkState.Waiting, OutboundLinkState.Connecting });
        Assert.Equal(0, peer.Accepted);

        peer.Online = true;
        await UntilAsync(connector, OutboundLinkState.Connected);

        Assert.Equal(0, connector.FailedAttempts);
    }

    [Fact]
    public async Task A_connection_that_ends_is_followed_by_a_new_attempt()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        peer.CloseNextConnectionsWith(1000);
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), HoldAsync);

        connector.Start();

        await TestWait.UntilAsync(() => peer.Accepted >= 3, "the connector to dial again after the peer closed the connection");
        Assert.NotEqual(OutboundLinkState.Rejected, connector.State);
    }

    [Fact]
    public async Task A_credential_that_the_peer_rejects_ends_the_dialling_until_the_configuration_changes()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        string credential = ReverseLinkEnrollment.CreateToken(new byte[32]);
        await using OutboundConnector connector = Create(peer, () => Endpoint(credential), HoldAsync);

        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Rejected);
        int attempts = peer.Dials.Length;
        Assert.Equal(0, peer.Accepted);
        Assert.Contains("401", connector.Detail, StringComparison.Ordinal);

        // Time passes and the network changes: dialling again with the same credential would be refused again, so nothing happens.
        await Task.Delay(300);
        connector.Nudge();
        await Task.Delay(300);
        Assert.Equal(attempts, peer.Dials.Length);
        Assert.Equal(OutboundLinkState.Rejected, connector.State);

        // The link is enrolled again with a credential that Home Assistant knows.
        credential = peer.Token;
        connector.Reconfigure();

        await UntilAsync(connector, OutboundLinkState.Connected);
        Assert.Equal(attempts + 1, peer.Dials.Length);
    }

    [Fact]
    public async Task A_close_with_code_4401_after_the_upgrade_is_a_rejection_too()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        peer.CloseNextConnectionsWith(CloseCodes.Unauthorized);
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), HoldAsync);

        connector.Start();

        await UntilAsync(connector, OutboundLinkState.Rejected);
        await Task.Delay(300);
        Assert.Equal(1, peer.Accepted);
        Assert.Equal(OutboundLinkState.Rejected, connector.State);
    }

    [Fact]
    public async Task A_nudge_ends_the_delay_so_that_a_new_network_is_used_at_once()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        peer.Online = false;
        var slow = new OutboundOptions
        {
            BackoffMin = TimeSpan.FromMinutes(1),
            BackoffMax = TimeSpan.FromMinutes(5),
            Random = () => 1.0,
        };
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), HoldAsync, slow);

        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Waiting);
        Assert.Single(peer.Dials);

        peer.Online = true;
        connector.Nudge();

        await UntilAsync(connector, OutboundLinkState.Connected);
        Assert.Equal(0, connector.FailedAttempts);
    }

    [Fact]
    public async Task Reconfiguring_ends_the_open_connection_and_dials_again_with_what_is_configured_now()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        bool enabled = true;
        await using OutboundConnector connector = Create(peer, () => enabled ? Endpoint(peer.Token) : null, HoldAsync);
        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Connected);
        Assert.Equal(1, peer.Accepted);

        // The link is left: the connection ends and nothing is dialled.
        enabled = false;
        connector.Reconfigure();
        await UntilAsync(connector, OutboundLinkState.Idle);
        Assert.Single(peer.Dials);

        // The link is joined again.
        enabled = true;
        connector.Reconfigure();

        await TestWait.UntilAsync(() => peer.Accepted == 2 && connector.State == OutboundLinkState.Connected, "the second connection");
    }

    [Fact]
    public async Task Stopping_ends_the_session_and_the_loop()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        var sessionEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), async (socket, token) =>
        {
            try
            {
                return await HoldAsync(socket, token);
            }
            finally
            {
                sessionEnded.TrySetResult();
            }
        });
        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Connected);

        await TestWait.WithTimeoutAsync(connector.StopAsync(), "the connector to stop");

        await TestWait.WithTimeoutAsync(sessionEnded.Task, "the session to end");
        Assert.Equal(OutboundLinkState.Stopped, connector.State);
        int dials = peer.Dials.Length;
        await Task.Delay(200);
        Assert.Equal(dials, peer.Dials.Length);
    }

    [Fact]
    public async Task A_failure_of_the_session_is_logged_and_the_connector_dials_again()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        int sessions = 0;
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), async (socket, token) =>
        {
            if (Interlocked.Increment(ref sessions) == 1)
            {
                throw new InvalidOperationException("The first session breaks.");
            }

            return await HoldAsync(socket, token);
        });

        connector.Start();

        await TestWait.UntilAsync(() => Volatile.Read(ref sessions) >= 2, "a second session after the first one failed");
        await UntilAsync(connector, OutboundLinkState.Connected);
    }

    [Fact]
    public async Task The_state_changes_are_reported_with_their_reason()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        peer.Online = false;
        var changes = new List<OutboundStateChange>();
        await using OutboundConnector connector = Create(peer, () => Endpoint(peer.Token), HoldAsync);
        connector.StateChanged += (_, change) =>
        {
            lock (changes)
            {
                changes.Add(change);
            }
        };

        connector.Start();
        await TestWait.UntilAsync(() => peer.Dials.Length >= 2, "two attempts");
        peer.Online = true;
        await UntilAsync(connector, OutboundLinkState.Connected);

        lock (changes)
        {
            Assert.Contains(changes, change => change.State == OutboundLinkState.Waiting && change.Detail == "The peer cannot be reached.");
            Assert.Equal(OutboundLinkState.Connected, changes[^1].State);
        }
    }

    [Fact]
    public async Task The_credential_never_appears_in_the_state_of_the_connector()
    {
        await using FakeHomeAssistant peer = await FakeHomeAssistant.StartAsync();
        peer.ExpectHello = false;
        string credential = ReverseLinkEnrollment.CreateToken(new byte[32]);
        await using OutboundConnector connector = Create(peer, () => Endpoint(credential), HoldAsync);

        connector.Start();
        await UntilAsync(connector, OutboundLinkState.Rejected);

        Assert.DoesNotContain(credential, connector.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    private static OutboundEndpoint Endpoint(string credential) => new(new Uri(FakeHomeAssistant.Url), credential);

    private static OutboundConnector Create(
        FakeHomeAssistant peer,
        Func<OutboundEndpoint?> endpoint,
        Func<WebSocket, CancellationToken, Task<int?>> session,
        OutboundOptions? options = null)
    {
        return new OutboundConnector(
            _ => ValueTask.FromResult(endpoint()),
            peer.Connector,
            session,
            options ?? FakeHomeAssistant.FastOptions(),
            TimeProvider.System,
            NullLogger.Instance);
    }

    private static Task UntilAsync(OutboundConnector connector, OutboundLinkState state)
    {
        return TestWait.UntilAsync(() => connector.State == state, "the connector to be " + state);
    }

    /// <summary>A session that does nothing but read until the peer closes the socket; returns the close code of the peer.</summary>
    private static async Task<int?> HoldAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    int? closeCode = socket.CloseStatus is { } status ? (int)status : null;
                    if (socket.State == WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }

                    return closeCode;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer went away, or the connector stops.
        }

        return null;
    }
}
