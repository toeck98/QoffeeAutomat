using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>
/// The Home Assistant side of the reverse link for the tests (ADR 0003): an in-process server that exposes the WebSocket endpoint
/// <c>/api/qoffeeautomat/reverse/v1</c>, checks the bearer token of the upgrade request like the integration does (HTTP 401 without an upgrade
/// when it is wrong) and hands every accepted connection to the test as a <see cref="LinkTestClient"/> that validates each frame of the agent
/// against protocol/schema. It also provides the connector that the agent dials with, so no test opens a real socket, and it can pretend to be
/// unreachable or to revoke the token.
/// </summary>
internal sealed class FakeHomeAssistant : IAsyncDisposable
{
    public const string Path = "/api/qoffeeautomat/reverse/v1";

    public const string Url = "ws://localhost" + Path;

    private readonly IHost _host;
    private readonly TestServer _server;
    private readonly Channel<LinkTestClient> _agents = Channel.CreateUnbounded<LinkTestClient>();
    private readonly List<OutboundEndpoint> _dials = [];
    private readonly List<string> _presented = [];
    private readonly object _gate = new();
    private int _closeWith = -1;
    private int _accepted;

    private FakeHomeAssistant(IHost host, TestServer server)
    {
        _host = host;
        _server = server;
        Connector = new TestServerConnector(this);
    }

    /// <summary>The token that the endpoint accepts; changing it revokes the old one.</summary>
    public string Token { get; set; } = ReverseLinkEnrollment.CreateToken(RandomNumberGenerator.GetBytes(32));

    /// <summary>False makes every dial fail as if the peer could not be reached.</summary>
    public bool Online { get; set; } = true;

    /// <summary>True (the default): accepted connections are handed to <see cref="WaitForAgentAsync"/> and must start with <c>hello</c>. False: they are only held open.</summary>
    public bool ExpectHello { get; set; } = true;

    /// <summary>The number of connections that passed the token check and were upgraded.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>The connector for the agent under test.</summary>
    public IOutboundWebSocketConnector Connector { get; }

    /// <summary>The endpoints that the agent dialled, oldest first (one entry per attempt, including failed ones).</summary>
    public OutboundEndpoint[] Dials
    {
        get
        {
            lock (_gate)
            {
                return [.. _dials];
            }
        }
    }

    /// <summary>The <c>Authorization</c> headers that reached the endpoint, oldest first.</summary>
    public string[] Presented
    {
        get
        {
            lock (_gate)
            {
                return [.. _presented];
            }
        }
    }

    /// <summary>The enrollment string that Home Assistant would show for the current token and the URL of this server.</summary>
    public string EnrollmentString => new ReverseLinkEnrollment(new Uri(Url), Token, null).ToEnrollmentString();

    public static async Task<FakeHomeAssistant> StartAsync()
    {
        FakeHomeAssistant? created = null;
        IHost host = await new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(context => created!.HandleAsync(context));
                }))
            .StartAsync();
        created = new FakeHomeAssistant(host, host.GetTestServer());
        return created;
    }

    /// <summary>Makes the endpoint close the next accepted connections with a close code right after the upgrade, as Home Assistant does when the token was revoked (4401).</summary>
    public void CloseNextConnectionsWith(int closeCode) => Volatile.Write(ref _closeWith, closeCode);

    /// <summary>Registers the options and the connector in the services of the agent under test.</summary>
    public void Configure(IServiceCollection services, OutboundOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<OutboundOptions>();
        services.AddSingleton(options ?? FastOptions());
        services.RemoveAll<IOutboundWebSocketConnector>();
        services.AddSingleton(Connector);
    }

    /// <summary>Delays short enough for a test and without jitter, so that the number of attempts is predictable.</summary>
    public static OutboundOptions FastOptions() => new()
    {
        BackoffMin = TimeSpan.FromMilliseconds(20),
        BackoffMax = TimeSpan.FromMilliseconds(80),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Random = () => 1.0,
    };

    /// <summary>Waits for an accepted connection whose <c>hello</c> has arrived.</summary>
    public async Task<LinkTestClient> WaitForAgentAsync(TimeSpan? timeout = null)
    {
        using var limit = new CancellationTokenSource(timeout ?? TestWait.DefaultTimeout);
        try
        {
            return await _agents.Reader.ReadAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Timed out waiting for the agent to connect to the reverse-link endpoint.");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _agents.Writer.TryComplete();
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task HandleAsync(HttpContext context)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        lock (_gate)
        {
            _presented.Add(authorization);
        }

        if (!string.Equals(context.Request.Path.Value, Path, StringComparison.Ordinal) || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!string.Equals(authorization, "Bearer " + Token, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(ProtocolInfo.SubProtocol);
        Interlocked.Increment(ref _accepted);
        int closeWith = Volatile.Read(ref _closeWith);
        if (closeWith >= 0)
        {
            using var timeout = new CancellationTokenSource(TestWait.DefaultTimeout);
            await socket.CloseAsync((WebSocketCloseStatus)closeWith, "revoked", timeout.Token);
            return;
        }

        if (!ExpectHello)
        {
            await HoldAsync(socket, context.RequestAborted);
            return;
        }

        LinkTestClient peer = await LinkTestClient.AttachAsync(socket);
        await _agents.Writer.WriteAsync(peer);

        // The socket belongs to the test now; the request ends when the connection does.
        while (!peer.HasEnded && !context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>Keeps a connection open without saying anything until the other side closes it; for tests of the connector, which speaks no QoffeeLink.</summary>
    private static async Task HoldAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The agent went away.
        }
    }

    private void Record(OutboundEndpoint endpoint)
    {
        lock (_gate)
        {
            _dials.Add(endpoint);
        }
    }

    private sealed class TestServerConnector(FakeHomeAssistant owner) : IOutboundWebSocketConnector
    {
        public async Task<WebSocket> ConnectAsync(OutboundEndpoint endpoint, CancellationToken cancellationToken)
        {
            owner.Record(endpoint);
            if (!owner.Online)
            {
                throw new OutboundDialException(OutboundFailure.Unreachable, "The peer cannot be reached.");
            }

            WebSocketClient client = owner._server.CreateWebSocketClient();
            client.SubProtocols.Add(ProtocolInfo.SubProtocol);
            client.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer " + endpoint.Credential;
            try
            {
                return await client.ConnectAsync(endpoint.Url, cancellationToken);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("401", StringComparison.Ordinal))
            {
                throw new OutboundDialException(OutboundFailure.Unauthorized, "The peer rejected the credential (HTTP 401).", exception);
            }
            catch (InvalidOperationException exception)
            {
                throw new OutboundDialException(OutboundFailure.Refused, "The peer refused the connection.", exception);
            }
        }
    }
}
