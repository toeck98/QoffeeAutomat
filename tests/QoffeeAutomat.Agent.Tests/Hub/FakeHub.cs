using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Tests.Link;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Security;

namespace QoffeeAutomat.Agent.Tests.Hub;

/// <summary>
/// The hub for the tests of the agent side of the hub link (T905): a small server on a real loopback socket with TLS and a self-signed certificate, so that the rule
/// "the pinned fingerprint is the only certificate that is accepted" is exercised by the real connector and not by a stand-in. It answers
/// <c>POST /hub/v1/enroll</c> and <c>GET /hub/v1/agent</c> as the specification says (PLAN.md S20, T904), hands every accepted connection to the test as a
/// <see cref="LinkTestClient"/> that validates each frame of the agent against protocol/schema, and can stop and start again on the same port, which is what a
/// restart of the hub looks like to an agent.
/// </summary>
internal sealed class FakeHub : IAsyncDisposable
{
    public const string HubId = "0123456789abcdef0123456789abcdef";
    public const string HubName = "Test hub";
    public const string EnrollPath = "/hub/v1/enroll";
    public const string AgentPath = "/hub/v1/agent";

    private readonly X509Certificate2 _certificate;
    private readonly ECDsa _signing;
    private readonly int _port;
    private readonly Channel<LinkTestClient> _agents = Channel.CreateUnbounded<LinkTestClient>();
    private readonly List<string> _presented = [];
    private readonly HashSet<string> _credentials = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly List<WebSocket> _sockets = [];
    private readonly object _gate = new();
    private IHost? _host;
    private int _accepted;
    private int _enrollCalls;
    private bool _approved;

    private FakeHub(int port)
    {
        _port = port;
        _certificate = SelfSignedCertificateFactory.Load(SelfSignedCertificateFactory.CreatePfx("localhost", DateTimeOffset.UtcNow, TimeSpan.FromDays(30)));
        _signing = HubSigningKey.Generate();
        SigningKey = HubSigningKey.ExportPublicKey(_signing);
        ValidToken = NewSecret();
    }

    /// <summary>
    /// Signs a desired state with the key whose public half the enrollment string carries, as the hub does (T908). The agent id is signed but not sent.
    /// </summary>
    /// <param name="requestId">The request id of the frame.</param>
    /// <param name="agentId">The id of the agent the state is for.</param>
    /// <param name="generation">The generation.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="documents">The documents of the generation; the items carry their hashes.</param>
    /// <returns>The frame.</returns>
    public ConfigDesiredMessage Desired(long requestId, string agentId, long generation, PolicyInfo policy, params HubDocument[] documents)
    {
        var state = new DesiredState(agentId, generation, policy, DesiredState.SortItems(documents.Select(DocumentHasher.ToItem)));
        return DesiredStateSigner.CreateMessage(requestId, state, _signing);
    }

    /// <summary>Signs a desired state with a key that the agent does not know, as an attacker or a different hub would.</summary>
    /// <param name="requestId">The request id of the frame.</param>
    /// <param name="agentId">The id of the agent the state is for.</param>
    /// <param name="generation">The generation.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="documents">The documents of the generation.</param>
    /// <returns>The frame.</returns>
    public static ConfigDesiredMessage DesiredWithForeignKey(long requestId, string agentId, long generation, PolicyInfo policy, params HubDocument[] documents)
    {
        using ECDsa foreign = HubSigningKey.Generate();
        var state = new DesiredState(agentId, generation, policy, DesiredState.SortItems(documents.Select(DocumentHasher.ToItem)));
        return DesiredStateSigner.CreateMessage(requestId, state, foreign);
    }

    /// <summary>The enrollment token that the enroll endpoint accepts.</summary>
    public string ValidToken { get; set; }

    /// <summary>True makes the enroll endpoint answer <c>pending</c> until <see cref="Approve"/> is called.</summary>
    public bool RequireApproval { get; set; }

    /// <summary>True makes the enroll endpoint answer 403 <c>rejected</c>.</summary>
    public bool RejectEnrollment { get; set; }

    /// <summary>The public key of the hub for desired states, as the enrollment string carries it.</summary>
    public string SigningKey { get; }

    /// <summary>The base address of the hub.</summary>
    public Uri Url => new("https://127.0.0.1:" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The SHA-256 fingerprint of the certificate, formatted as the hub shows it (pairs separated by colons).</summary>
    public string Fingerprint => CertificateFingerprint.Compute(_certificate);

    /// <summary>The credential that the hub issued last.</summary>
    public string? LastCredential { get; private set; }

    /// <summary>The number of connections that passed the credential check and were upgraded.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>The number of enroll requests that reached the endpoint.</summary>
    public int EnrollCalls => Volatile.Read(ref _enrollCalls);

    /// <summary>The credentials that reached the agent endpoint (bearer tokens), oldest first, one entry per request, including refused ones.</summary>
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

    public static async Task<FakeHub> StartAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var hub = new FakeHub(port);
        await hub.StartHostAsync();
        return hub;
    }

    /// <summary>A random secret in the form of a credential or a token: 32 bytes, base64url without padding.</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The text of a <c>client_hello</c> as the hub sends it.</summary>
    public static string HubClientHello(params string[] features)
    {
        string list = features.Length == 0 ? string.Empty : ",\"features\":[" + string.Join(',', features.Select(feature => "\"" + feature + "\"")) + "]";
        return "{\"t\":\"client_hello\",\"protocol\":1,\"client\":{\"kind\":\"hub\",\"name\":\"" + HubName + "\",\"version\":\"1.0.0\"}" + list + "}";
    }

    /// <summary>The enrollment string that the hub would show for the current token.</summary>
    /// <param name="pin">True to carry the fingerprint of the certificate; false to leave it out (normal certificate validation).</param>
    /// <param name="fingerprint">A fingerprint to carry instead of the real one, for a certificate that does not match.</param>
    public string EnrollmentString(bool pin = true, string? fingerprint = null)
    {
        return QoffeeAutomat.Contracts.Hub.EnrollmentString.Encode(new EnrollmentPayload(Url, ValidToken, SigningKey, pin ? fingerprint ?? Fingerprint : null));
    }

    /// <summary>Lets the next enroll request of a pending agent succeed.</summary>
    public void Approve() => Volatile.Write(ref _approved, true);

    /// <summary>Makes a credential valid, as if the hub had issued it.</summary>
    public void AddCredential(string credential)
    {
        lock (_gate)
        {
            _ = _credentials.Add(credential);
        }
    }

    /// <summary>Makes a credential revoked: the agent endpoint answers it with 401 and the code <c>revoked</c>.</summary>
    public void Revoke(string credential)
    {
        lock (_gate)
        {
            _ = _credentials.Remove(credential);
            _ = _revoked.Add(credential);
        }
    }

    /// <summary>Forgets which credentials were revoked, as a hub does that lost its database: they are unknown from now on, which is answered with <c>unauthorized</c>.</summary>
    public void ForgetRevocations()
    {
        lock (_gate)
        {
            _revoked.Clear();
        }
    }

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
            Assert.Fail("Timed out waiting for the agent to connect to the hub.");
            throw;
        }
    }

    /// <summary>Stops the hub and drops every connection; the port stays reserved for <see cref="StartAgainAsync"/>.</summary>
    public async Task StopAsync()
    {
        IHost? host = _host;
        _host = null;
        if (host is null)
        {
            return;
        }

        // Kestrel would wait for open WebSockets; a hub that goes away does not.
        WebSocket[] open;
        lock (_gate)
        {
            open = [.. _sockets];
            _sockets.Clear();
        }

        foreach (WebSocket socket in open)
        {
            socket.Abort();
        }

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await host.StopAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // The connections are gone; the rest does not matter for a test.
        }

        host.Dispose();
    }

    /// <summary>Starts the hub again on the same port with the same credentials and the same certificate.</summary>
    public Task StartAgainAsync() => StartHostAsync();

    /// <summary>A restart: the hub goes away and comes back.</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        await StartHostAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _agents.Writer.TryComplete();
        await StopAsync();
        _certificate.Dispose();
        _signing.Dispose();
    }

    private async Task StartHostAsync()
    {
        IHost host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(options => options.Listen(IPAddress.Loopback, _port, listen => listen.UseHttps(_certificate)))
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(HandleAsync);
                }))
            .Build();
        await host.StartAsync();
        _host = host;
    }

    private Task HandleAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        if (HttpMethods.IsPost(context.Request.Method) && string.Equals(path, EnrollPath, StringComparison.Ordinal))
        {
            return EnrollAsync(context);
        }

        if (string.Equals(path, AgentPath, StringComparison.Ordinal))
        {
            return LinkAsync(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private async Task EnrollAsync(HttpContext context)
    {
        Interlocked.Increment(ref _enrollCalls);
        EnrollRequest? request = await JsonSerializer.DeserializeAsync(context.Request.Body, HubJsonContext.Default.EnrollRequest, context.RequestAborted);
        if (request is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request");
            return;
        }

        if (!string.Equals(request.Token, ValidToken, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }

        if (RejectEnrollment)
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "rejected");
            return;
        }

        var hub = new EnrollHubInfo(HubId, HubName);
        EnrollResponse response;
        if (RequireApproval && !Volatile.Read(ref _approved))
        {
            response = new EnrollResponse(EnrollStates.Pending, hub, RetryAfter: 1);
        }
        else
        {
            string credential = NewSecret();
            lock (_gate)
            {
                _ = _credentials.Add(credential);
                LastCredential = credential;
            }

            response = new EnrollResponse(EnrollStates.Enrolled, hub, credential);
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response, HubJsonContext.Default.EnrollResponse), context.RequestAborted);
    }

    private async Task LinkAsync(HttpContext context)
    {
        string header = context.Request.Headers.Authorization.ToString();
        string credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : string.Empty;
        bool known;
        bool revoked;
        lock (_gate)
        {
            _presented.Add(credential);
            known = _credentials.Contains(credential);
            revoked = _revoked.Contains(credential);
        }

        if (revoked)
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "revoked");
            return;
        }

        if (!known)
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "bad_request");
            return;
        }

        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(ProtocolInfo.SubProtocol);
        Interlocked.Increment(ref _accepted);
        lock (_gate)
        {
            _sockets.Add(socket);
        }

        LinkTestClient peer = await LinkTestClient.AttachAsync(socket);
        await _agents.Writer.WriteAsync(peer);

        // The socket belongs to the test now; the request ends when the connection does.
        while (!peer.HasEnded && !context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(20);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        string json = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + code + "\"}}";
        await context.Response.WriteAsync(json, Encoding.UTF8, context.RequestAborted);
    }
}
