using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Tests.Enrollment;
using QoffeeAutomat.Hub.Tests.Security;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Hub.Tests.Gateway;

/// <summary>Records what a WebSocket receives (the text frames that the hub sent to the agent) and forwards everything else untouched.</summary>
internal sealed class RecordingWebSocket(WebSocket inner, Action<JsonElement> onFrame) : WebSocket
{
    private readonly MemoryStream _partial = new();

    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;

    public override string? CloseStatusDescription => inner.CloseStatusDescription;

    public override WebSocketState State => inner.State;

    public override string? SubProtocol => inner.SubProtocol;

    public override void Abort() => inner.Abort();

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Dispose()
    {
        _partial.Dispose();
        inner.Dispose();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        WebSocketReceiveResult result = await inner.ReceiveAsync(buffer, cancellationToken);
        Record(buffer.AsSpan(0, result.Count), result.MessageType, result.EndOfMessage);
        return result;
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ValueWebSocketReceiveResult result = await inner.ReceiveAsync(buffer, cancellationToken);
        Record(buffer.Span[..result.Count], result.MessageType, result.EndOfMessage);
        return result;
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    private void Record(ReadOnlySpan<byte> data, WebSocketMessageType type, bool endOfMessage)
    {
        if (type != WebSocketMessageType.Text)
        {
            _partial.SetLength(0);
            return;
        }

        _partial.Write(data);
        if (!endOfMessage)
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(_partial.ToArray());
            onFrame(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            // Not JSON: the test that cares looks at the agent's reaction.
        }
        finally
        {
            _partial.SetLength(0);
        }
    }
}

/// <summary>Replaces the process launcher of an agent: no process runs, every start is recorded, and a test can hold the launches back to keep a run in progress.</summary>
internal sealed class QuietLauncher : IProcessLauncher
{
    private TaskCompletionSource? _hold;

    public ConcurrentQueue<ProcessLaunchRequest> Launches { get; } = new();

    /// <summary>Keeps every launch that follows waiting until <see cref="Release"/> is called.</summary>
    public void Hold() => Volatile.Write(ref _hold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Lets the held launches complete.</summary>
    public void Release() => Volatile.Read(ref _hold)?.TrySetResult();

    public async ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        Launches.Enqueue(request);
        TaskCompletionSource? hold = Volatile.Read(ref _hold);
        if (hold is not null)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return new ExecutionResult(0, "ran " + string.Join(' ', request.Arguments), string.Empty);
    }
}

/// <summary>Frames and records for an agent that a test plays by hand (see <see cref="ScriptedAgentSocket"/>).</summary>
internal static class ScriptedAgent
{
    public static AgentInfo Info(string id, string version = "1.0.0") => new(id, "Scripted agent", version, "windows", "x64", "SCRIPT-PC", []);

    /// <summary>The <c>hello</c> of an agent that runs another version than 1.0.0 (T917: an agent that came back from its update).</summary>
    public static string HelloWithVersion(string id, string rev, string version)
    {
        return LinkCodec.Serialize(new HelloMessage(new ProtocolRange(1, 1), Info(id, version), rev, "boot-1", null));
    }

    /// <summary>The <c>hello</c> that an agent sends first.</summary>
    public static string Hello(string id, string rev, int min = 1, int max = 1, params string[] features)
    {
        return LinkCodec.Serialize(new HelloMessage(new ProtocolRange(min, max), Info(id), rev, "boot-1", features.Length == 0 ? null : features));
    }

    /// <summary>The registry record that the endpoint would hand to the gateway for the agent.</summary>
    public static AgentRecord Record(string id)
    {
        return new AgentRecord(
            id,
            "Scripted agent",
            "SCRIPT-PC",
            "windows",
            null,
            "x64",
            "1.0.0",
            [],
            null,
            [],
            AgentStatuses.Enrolled,
            new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero),
            null,
            null,
            null,
            null,
            false);
    }

    /// <summary>A manifest with the given metrics, as the data of the result of <c>get_manifest</c>.</summary>
    public static JsonElement ManifestOf(string id, string rev, params string[] metrics)
    {
        var manifest = new Manifest(
            rev,
            Info(id),
            [.. metrics.Select(metric => new MetricDescriptor(metric, metric, "sensor", "number"))],
            []);
        return LinkCodec.ToElement(manifest);
    }

    /// <summary>The text of a successful result.</summary>
    public static string Ok(long id, JsonElement? data = null)
    {
        return LinkCodec.Serialize(ResultMessage.Success(id, data ?? EmptyObject()));
    }

    private static JsonElement EmptyObject()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>The text of a failed result.</summary>
    public static string Fail(long id, string code) => LinkCodec.Serialize(ResultMessage.Failure(id, code, "failed on purpose"));

    /// <summary>The id of a request frame that the hub sent.</summary>
    public static long IdOf(JsonElement request) => request.GetProperty("id").GetInt64();
}

/// <summary>An agent never sees a network change in these tests.</summary>
internal sealed class NoNetworkChanges : INetworkChangeSource
{
    public IDisposable Watch(Action changed) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// What an agent in the tests dials with: the in-process hub instead of the network. It opens the WebSocket on the hub's test server (the upgrade request carries the
/// credential like the real connector does), hands the agent a socket that records what the hub sends, and plays the HTTP client that the agent uses to learn why a
/// link was refused and to enroll. A test can switch the hub off for this agent.
/// </summary>
internal sealed class HubTestLink : IOutboundWebSocketConnector, IHubHttpClientFactory
{
    private readonly HubTestHost _hub;
    private readonly List<JsonElement> _received = [];
    private readonly object _gate = new();
    private int _dials;

    public HubTestLink(HubTestHost hub)
    {
        _hub = hub;
    }

    /// <summary>False makes every dial fail as if the hub could not be reached.</summary>
    public bool Online { get; set; } = true;

    /// <summary>The number of dial attempts.</summary>
    public int Dials => Volatile.Read(ref _dials);

    /// <summary>The frames that the hub sent to the agent, over all connections, oldest first.</summary>
    public JsonElement[] Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    /// <summary>The frames of a type that the hub sent to the agent.</summary>
    public JsonElement[] ReceivedOf(string type) => [.. Received.Where(frame => TypeOf(frame) == type)];

    public static string TypeOf(JsonElement frame)
    {
        return frame.TryGetProperty("t", out JsonElement type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : string.Empty;
    }

    public async Task<WebSocket> ConnectAsync(OutboundEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        _ = Interlocked.Increment(ref _dials);
        if (!Online)
        {
            throw new OutboundDialException(OutboundFailure.Unreachable, "The hub cannot be reached.");
        }

        Microsoft.AspNetCore.TestHost.WebSocketClient client = _hub.CreateWebSocketClient();
        client.SubProtocols.Add(ProtocolInfo.SubProtocol);
        client.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer " + endpoint.Credential;
        try
        {
            WebSocket socket = await client.ConnectAsync(endpoint.Url, cancellationToken);
            return new RecordingWebSocket(socket, Record);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("401", StringComparison.Ordinal))
        {
            throw new OutboundDialException(OutboundFailure.Unauthorized, "The hub rejected the credential (HTTP 401).", exception);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("403", StringComparison.Ordinal))
        {
            throw new OutboundDialException(OutboundFailure.Unauthorized, "The hub rejected the credential (HTTP 403).", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new OutboundDialException(OutboundFailure.Refused, "The hub refused the connection.", exception);
        }
    }

    public HttpClient Create(string? pinnedFingerprint) => new(_hub.CreateHandler(), disposeHandler: false);

    private void Record(JsonElement frame)
    {
        lock (_gate)
        {
            _received.Add(frame);
        }
    }
}

/// <summary>The agent as a test hosts it: the whole agent on the simulated platform in a temporary data directory, dialling the in-process hub.</summary>
internal sealed class SimulatedAgent : IAsyncDisposable
{
    public const string CommandId = "set_level";

    private readonly TempDirectory _data = new();
    private readonly AgentFactory _factory;

    public SimulatedAgent(HubTestHost hub)
    {
        Link = new HubTestLink(hub);
        _factory = new AgentFactory(_data.Path, Link, Launcher);
    }

    public HubTestLink Link { get; }

    public QuietLauncher Launcher { get; } = new();

    public IServiceProvider Services => _factory.Services;

    public string Id => Services.GetRequiredService<AgentIdentity>().Current().Id;

    public HubLinkManager Manager => Services.GetRequiredService<HubLinkManager>();

    public SimulatedPlatform Platform => Services.GetRequiredService<SimulatedPlatform>();

    public IConfigStore Config => Services.GetRequiredService<IConfigStore>();

    public QoffeeAutomat.Core.Manifest.IManifestSource ManifestSource => Services.GetRequiredService<QoffeeAutomat.Core.Manifest.IManifestSource>();

    public QoffeeAutomat.Core.Events.IEventBus Events => Services.GetRequiredService<QoffeeAutomat.Core.Events.IEventBus>();

    public string? Credential => Manager.GetStateAsync(CancellationToken.None).GetAwaiter().GetResult()?.Credential;

    /// <summary>Joins the hub with an enrollment string.</summary>
    public async Task JoinAsync(string enrollmentString)
    {
        HubJoinResult joined = await Manager.JoinAsync(enrollmentString, CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
    }

    /// <summary>Saves an enabled, exposed process command with one integer variable <c>Level</c> between 1 and 5.</summary>
    /// <param name="id">The id of the command; a new id changes the manifest of the agent.</param>
    public async Task<string> AddCommandAsync(string id = CommandId)
    {
        var command = new CommandDocument(
            SchemaVersion: 1,
            Id: id,
            Name: "Set level",
            Enabled: true,
            ExposeToHa: true,
            RunAs: RunAsValues.System,
            Action: new ProcessAction("level-tool", ["--level", "{{Level}}"]),
            Parameters: [new ParameterDefinition("Level", ParameterTypes.WholeNumber, Required: true, Min: 1, Max: 5)],
            CaptureOutput: true);
        SaveResult<CommandDocument> saved = await Config.Local.SaveAsync(command, null, CancellationToken.None);
        Assert.True(saved.IsApplied, "The command was not saved: " + saved.Status);
        return ManifestSource.Current.Rev;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _data.Dispose();
    }

    /// <summary>The agent in an in-process test server, without anything that touches the network of the machine.</summary>
    private sealed class AgentFactory : WebApplicationFactory<QoffeeAutomat.Agent.Program>
    {
        private readonly string _dataDirectory;
        private readonly HubTestLink _link;
        private readonly QuietLauncher _launcher;

        static AgentFactory()
        {
            // Prevent WebApplication.CreateBuilder from allocating Linux inotify FileSystemWatchers on every test host.
            Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");

            // The entry point starts the host with an empty command line, which on Linux would select the real Linux platform; the tests run on the simulated one.
            Environment.SetEnvironmentVariable("QOFFEE_PLATFORM", "simulated");
        }

        public AgentFactory(string dataDirectory, HubTestLink link, QuietLauncher launcher)
        {
            _dataDirectory = dataDirectory;
            _link = link;
            _launcher = launcher;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAppPaths>();
                services.AddSingleton<IAppPaths>(AppPaths.ForRoot(_dataDirectory));

                // Nothing touches the network of the machine: no multicast DNS, no network change events, no real process.
                services.RemoveAll<IServiceAdvertiser>();
                services.AddSingleton<IServiceAdvertiser>(provider => provider.GetRequiredService<SimulatedPlatform>().Advertiser);
                services.RemoveAll<INetworkChangeSource>();
                services.AddSingleton<INetworkChangeSource, NoNetworkChanges>();
                services.RemoveAll<IProcessLauncher>();
                services.AddSingleton<IProcessLauncher>(_launcher);

                // The hub link dials the in-process hub, quickly and without jitter.
                services.RemoveAll<OutboundOptions>();
                services.AddSingleton(new OutboundOptions
                {
                    BackoffMin = TimeSpan.FromMilliseconds(20),
                    BackoffMax = TimeSpan.FromMilliseconds(80),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    Random = () => 1.0,
                });
                services.RemoveAll<IOutboundWebSocketConnector>();
                services.AddSingleton<IOutboundWebSocketConnector>(_link);
                services.RemoveAll<IHubHttpClientFactory>();
                services.AddSingleton<IHubHttpClientFactory>(_link);
                services.RemoveAll<HubLinkOptions>();
                services.AddSingleton(new HubLinkOptions { PendingDelayScale = 0.01 });
            });
        }
    }
}

/// <summary>Collects the notifications of the gateway.</summary>
internal sealed class RecordingObserver : IAgentGatewayObserver
{
    public ConcurrentQueue<AgentPresence> Presence { get; } = new();

    public ConcurrentQueue<(string AgentId, CachedManifest Manifest)> Manifests { get; } = new();

    public ConcurrentQueue<AgentStateUpdate> States { get; } = new();

    public ConcurrentQueue<StoredAgentEvent> Events { get; } = new();

    public ConcurrentQueue<string> Removed { get; } = new();

    public bool ThrowOnPresence { get; set; }

    public void OnPresenceChanged(AgentPresence presence)
    {
        Presence.Enqueue(presence);
        if (ThrowOnPresence)
        {
            throw new InvalidOperationException("An observer that fails.");
        }
    }

    public void OnManifestChanged(string agentId, CachedManifest manifest) => Manifests.Enqueue((agentId, manifest));

    public void OnStateChanged(AgentStateUpdate update) => States.Enqueue(update);

    public void OnEvent(StoredAgentEvent agentEvent) => Events.Enqueue(agentEvent);

    public void OnAgentRemoved(string agentId) => Removed.Enqueue(agentId);
}

/// <summary>The hub with the gateway and as many simulated agents as a test needs.</summary>
internal sealed class GatewayRig : IAsyncDisposable
{
    private readonly List<SimulatedAgent> _agents = [];

    private GatewayRig(HubTestHost hub)
    {
        Hub = hub;
    }

    public HubTestHost Hub { get; }

    public AgentGateway Gateway => Hub.Service<AgentGateway>();

    /// <summary>Timings that make a test fast: a short handshake, a short close wait and the limits of the specification otherwise.</summary>
    public static GatewayOptions FastOptions() => new()
    {
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        CloseTimeout = TimeSpan.FromMilliseconds(500),
        RequestTimeout = TimeSpan.FromSeconds(10),
        RunTimeout = TimeSpan.FromSeconds(20),
    };

    /// <param name="options">The options of the gateway; null uses <see cref="FastOptions"/>.</param>
    /// <param name="withUsers">True to create an operator and a viewer next to the administrator; each account costs a password hash.</param>
    /// <param name="configure">Changes the service collection of the hub after the gateway options are set, for example to replace the metric options.</param>
    public static async Task<GatewayRig> StartAsync(GatewayOptions? options = null, bool withUsers = true, Action<IServiceCollection>? configure = null)
    {
        HubTestHost hub = await HubTestHost.StartAsync(
            withUsers: withUsers,
            configure: services =>
            {
                services.RemoveAll<GatewayOptions>();
                services.AddSingleton(options ?? FastOptions());
                services.RemoveAll<HubMetricsOptions>();
                services.AddSingleton(new HubMetricsOptions { DefaultPolicy = MetricCollectionPolicy.Nothing });
                configure?.Invoke(services);
            });
        return new GatewayRig(hub);
    }

    /// <summary>Creates an agent, enrolls it at the hub and waits until its link is open and its manifest is cached.</summary>
    /// <param name="withCommand">True to give the agent the command <see cref="SimulatedAgent.CommandId"/> before it joins.</param>
    public async Task<SimulatedAgent> AddAgentAsync(bool withCommand = true)
    {
        SimulatedAgent agent = await CreateAgentAsync(withCommand);
        await JoinAsync(agent);
        await WaitOnlineAsync(agent);
        return agent;
    }

    /// <summary>Creates an agent that has not joined the hub.</summary>
    public async Task<SimulatedAgent> CreateAgentAsync(bool withCommand = true)
    {
        var agent = new SimulatedAgent(Hub);
        _agents.Add(agent);
        if (withCommand)
        {
            _ = await agent.AddCommandAsync();
        }

        return agent;
    }

    /// <summary>Creates an enrollment token at the hub and joins the agent with it.</summary>
    public async Task JoinAsync(SimulatedAgent agent, bool approval = false, string[]? tags = null)
    {
        TestToken token = await EnrollmentKit.CreateTokenAsync(Hub, approval: approval, tags: tags);
        await agent.JoinAsync(token.EnrollmentString);
    }

    public async Task WaitOnlineAsync(SimulatedAgent agent)
    {
        await TestWait.UntilAsync(() => Gateway.GetPresence(agent.Id).Online, "the agent to be online", 15000);
        await TestWait.UntilAsync(() => Gateway.GetManifest(agent.Id) is not null, "the manifest of the agent to be cached", 15000);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (SimulatedAgent agent in _agents)
        {
            await agent.DisposeAsync();
        }

        await Hub.DisposeAsync();
    }
}

/// <summary>
/// An agent that the test plays by hand, on a WebSocket of its own that needs no network: what the hub receives is what the test puts in, what the hub sends is
/// what the test reads. It is for what a real agent never does: a frame that is not JSON, too many frames, a <c>hello</c> for another agent, an agent that
/// stops reading. The hub serves it through <see cref="AgentGateway.ServeAsync"/> like the socket of the endpoint.
/// </summary>
internal sealed class ScriptedAgentSocket : WebSocket
{
    private readonly object _gate = new();
    private readonly System.Threading.Channels.Channel<Incoming> _toHub = System.Threading.Channels.Channel.CreateUnbounded<Incoming>();
    private readonly System.Threading.Channels.Channel<byte[]> _fromHub = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Incoming? _current;
    private int _offset;
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;
    private bool _closeSent;
    private bool _closeReceived;

    private readonly record struct Incoming(WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? Status = null, string? Description = null);

    /// <summary>True makes every send of the hub wait forever, like an agent that does not read.</summary>
    public bool StallSends { get; set; }

    /// <summary>True makes the socket ignore the close frame of the hub: the peer never answers it.</summary>
    public bool IgnoreClose { get; set; }

    /// <summary>The close code that the hub sent, or null while it has not closed.</summary>
    public override WebSocketCloseStatus? CloseStatus => _closeStatus;

    public override string? CloseStatusDescription => _closeDescription;

    public override WebSocketState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public override string? SubProtocol => ProtocolInfo.SubProtocol;

    /// <summary>Completes with the close code that the hub chose, when it closes or aborts the connection (-1 for an abort).</summary>
    public Task<int> Closed => _closed.Task;

    /// <summary>Makes the hub receive a text frame.</summary>
    public void SendText(string json) => _toHub.Writer.TryWrite(new Incoming(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json)));

    /// <summary>Makes the hub receive a binary frame.</summary>
    public void SendBinary(byte[] data) => _toHub.Writer.TryWrite(new Incoming(WebSocketMessageType.Binary, data));

    /// <summary>Makes the hub receive the close frame of the peer.</summary>
    public void SendClose(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure) =>
        _toHub.Writer.TryWrite(new Incoming(WebSocketMessageType.Close, [], status, string.Empty));

    /// <summary>Waits for the next frame that the hub sent.</summary>
    public async Task<JsonElement> NextFrameAsync()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            byte[] data = await _fromHub.Reader.ReadAsync(limit.Token);
            using JsonDocument document = JsonDocument.Parse(data);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Timed out waiting for the hub to send a frame.");
            throw;
        }
    }

    /// <summary>Waits for a frame of a type; other frames are skipped.</summary>
    public async Task<JsonElement> NextFrameOfAsync(string type)
    {
        while (true)
        {
            JsonElement frame = await NextFrameAsync();
            if (HubTestLink.TypeOf(frame) == type)
            {
                return frame;
            }
        }
    }

    /// <summary>The frames that the hub sent and the test has not read yet.</summary>
    public int Pending => _fromHub.Reader.Count;

    public override void Abort()
    {
        lock (_gate)
        {
            _state = WebSocketState.Aborted;
        }

        _ = _toHub.Writer.TryComplete(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        _ = _closed.TrySetResult(-1);
        _ = _aborted.TrySetResult();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _closeSent = true;
            _closeStatus = closeStatus;
            _closeDescription = statusDescription;
            _state = _closeReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
        }

        _ = _closed.TrySetResult((int)closeStatus);

        // A polite peer answers a close frame with its own.
        if (!IgnoreClose)
        {
            SendClose(closeStatus);
        }

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            _current = await _toHub.Reader.ReadAsync(cancellationToken);
            _offset = 0;
        }

        Incoming message = _current.Value;
        if (message.Type == WebSocketMessageType.Close)
        {
            _current = null;
            lock (_gate)
            {
                _closeReceived = true;
                _state = _closeSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
            }

            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, message.Status, message.Description);
        }

        int count = Math.Min(buffer.Count, message.Data.Length - _offset);
        Array.Copy(message.Data, _offset, buffer.Array!, buffer.Offset, count);
        _offset += count;
        bool end = _offset >= message.Data.Length;
        if (end)
        {
            _current = null;
        }

        return new WebSocketReceiveResult(count, message.Type, end);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (StallSends)
        {
            // Like a peer that does not read: the send ends only when the caller gives up or the connection is aborted.
            Task stalled = await Task.WhenAny(Task.Delay(Timeout.Infinite, cancellationToken), _aborted.Task);
            if (ReferenceEquals(stalled, _aborted.Task))
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        _ = _fromHub.Writer.TryWrite(buffer.ToArray());
    }
}
