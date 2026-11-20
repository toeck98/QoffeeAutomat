using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Tests.Schema;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Tests.Enrollment;
using QoffeeAutomat.Hub.Tests.Gateway;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>The protocol schemas of the repository (protocol/schema), loaded once with the validator of the contracts tests.</summary>
internal static class FleetSchemas
{
    private const string SchemaBase = "https://github.com/toeck98/QoffeeAutomat/protocol/schema/";

    private static readonly Lazy<SchemaSet> Shared = new(() => SchemaSet.Load(Path.Combine(FindRoot(), "protocol", "schema")));

    /// <summary>The schema that dispatches the frames of the hub extensions on <c>t</c> (the agent-scoped ones on the member <c>a</c>, the fleet hello on <c>hub</c>).</summary>
    public static string HubMessage { get; } = SchemaBase + "hub/message.schema.json";

    /// <summary>The schema that dispatches the frames of QoffeeLink v1 on <c>t</c>; it knows <c>result</c>.</summary>
    public static string LinkMessage { get; } = SchemaBase + "message.schema.json";

    /// <summary>The response of <c>POST /hub/v1/pair</c>.</summary>
    public static string PairResponse { get; } = SchemaBase + "hub/pair_response.schema.json";

    /// <summary>A result whose data is the answer to <c>get_agents</c>.</summary>
    public static string GetAgentsResult { get; } = SchemaBase + "hub/result_get_agents.schema.json";

    /// <summary>A failed result with one of the error codes that the hub adds.</summary>
    public static string HubErrorResult { get; } = SchemaBase + "hub/result_hub_error.schema.json";

    /// <summary>Validates an instance; returns the errors, empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(JsonElement instance, string schemaId) => Shared.Value.Validate(instance, schemaId);

    private static string FindRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "global.json")) && Directory.Exists(Path.Combine(directory, "protocol")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("The repository root (global.json and protocol/) was not found.");
    }
}

/// <summary>A fleet token as a test holds it: the secret that the client presents and the id that the management API uses.</summary>
internal sealed record PairedToken(string Secret, string Id);

/// <summary>
/// A client of the fleet link in the tests (T913), like Home Assistant: it connects to the in-process hub with the real upgrade request, keeps every frame it receives and checks
/// each of them against the protocol schemas; disposing the client fails the test when the hub ever sent a frame that does not validate or that a client must never get. A reader
/// task takes the frames off the socket, so a test can wait for a frame, wait for the result of a request while the unsolicited frames that come meanwhile are kept in order, and
/// check that nothing arrives.
/// </summary>
internal sealed class FleetClient : IAsyncDisposable
{
    public const string Url = "wss://localhost" + HubProtocol.FleetLinkPath;

    // The types the hub may send on the fleet link; anything else means the hub invented a frame.
    private static readonly string[] HubTypes =
    [
        MessageTypes.Hello,
        MessageTypes.Result,
        HubMessageTypes.AgentAdded,
        HubMessageTypes.AgentRemoved,
        HubMessageTypes.AgentStatus,
        MessageTypes.ManifestChanged,
        MessageTypes.State,
        MessageTypes.Event,
    ];

    // The types whose frames are agent-scoped: they must carry the member a.
    private static readonly string[] ScopedTypes =
    [
        HubMessageTypes.AgentAdded,
        HubMessageTypes.AgentRemoved,
        HubMessageTypes.AgentStatus,
        MessageTypes.ManifestChanged,
        MessageTypes.State,
        MessageTypes.Event,
    ];

    private readonly WebSocket _socket;
    private readonly Channel<JsonElement> _frames = Channel.CreateUnbounded<JsonElement>();
    private readonly ConcurrentQueue<JsonElement> _all = new();
    private readonly ConcurrentQueue<string> _problems = new();
    private readonly Queue<JsonElement> _unread = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private long _lastId;

    private FleetClient(WebSocket socket)
    {
        _socket = socket;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>The <c>hello</c> that the hub sent first.</summary>
    public JsonElement Hello { get; private set; }

    /// <summary>Completes with the close code that the hub chose, when the connection ends (-1 when it was dropped without a close frame).</summary>
    public Task<int> Closed => _closed.Task;

    /// <summary>Every frame that the hub sent, oldest first.</summary>
    public IReadOnlyList<JsonElement> Frames => [.. _all];

    /// <summary>Opens the fleet link with a token and reads the <c>hello</c>.</summary>
    public static async Task<FleetClient> ConnectAsync(HubTestHost host, string token)
    {
        Microsoft.AspNetCore.TestHost.WebSocketClient client = host.CreateWebSocketClient();
        client.SubProtocols.Add(ProtocolInfo.SubProtocol);
        client.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer " + token;
        WebSocket socket = await client.ConnectAsync(new Uri(Url), CancellationToken.None);
        var fleet = new FleetClient(socket);
        fleet.Hello = await fleet.NextAsync();
        Assert.Equal(MessageTypes.Hello, TypeOf(fleet.Hello));
        return fleet;
    }

    public static string TypeOf(JsonElement frame) => HubTestLink.TypeOf(frame);

    /// <summary>The agent id of an agent-scoped frame, or an empty string.</summary>
    public static string AgentOf(JsonElement frame)
    {
        return frame.TryGetProperty("a", out JsonElement agent) && agent.ValueKind == JsonValueKind.String ? agent.GetString()! : string.Empty;
    }

    /// <summary>A request frame without its id (the client adds it).</summary>
    public static JsonObject Request(string type, string? agent = null)
    {
        var request = new JsonObject { ["t"] = type };
        if (agent is not null)
        {
            request["a"] = agent;
        }

        return request;
    }

    /// <summary>A <c>subscribe</c> request for the given metrics (or <c>"*"</c>).</summary>
    public static JsonObject Subscribe(string agent, params string[] metrics)
    {
        JsonObject request = Request(MessageTypes.Subscribe, agent);
        request["metrics"] = metrics is ["*"] ? JsonValue.Create("*") : new JsonArray([.. metrics.Select(metric => (JsonNode?)JsonValue.Create(metric))]);
        return request;
    }

    /// <summary>A <c>run</c> request.</summary>
    public static JsonObject Run(string agent, string command, string? userId = null, bool? wait = null, JsonObject? vars = null)
    {
        JsonObject request = Request(MessageTypes.Run, agent);
        request["command"] = command;
        if (vars is not null)
        {
            request["vars"] = vars;
        }

        if (wait is { } value)
        {
            request["wait"] = value;
        }

        if (userId is not null)
        {
            request["context"] = new JsonObject { ["user_id"] = userId };
        }

        return request;
    }

    /// <summary>Sends a request and waits for its result; the unsolicited frames that arrive meanwhile are kept for <see cref="NextAsync"/>.</summary>
    public async Task<JsonElement> RequestAsync(JsonObject request)
    {
        long id = await SendRequestAsync(request);
        return await AwaitResultAsync(id);
    }

    /// <summary>Sends a request and returns its id without waiting.</summary>
    public async Task<long> SendRequestAsync(JsonObject request)
    {
        long id = Interlocked.Increment(ref _lastId);
        request["id"] = id;
        await SendTextAsync(request.ToJsonString());
        return id;
    }

    /// <summary>Waits for the result of a request that was sent with <see cref="SendRequestAsync"/>.</summary>
    public async Task<JsonElement> AwaitResultAsync(long id, int timeoutMilliseconds = 10000)
    {
        int held = _unread.Count;
        for (int index = 0; index < held; index++)
        {
            JsonElement candidate = _unread.Dequeue();
            if (IsResultOf(candidate, id))
            {
                return candidate;
            }

            _unread.Enqueue(candidate);
        }

        while (true)
        {
            JsonElement frame = await ReadFromSocketAsync(timeoutMilliseconds);
            if (IsResultOf(frame, id))
            {
                return frame;
            }

            _unread.Enqueue(frame);
        }
    }

    /// <summary>The next frame that the hub sent, or a failed test when none comes in time.</summary>
    public async Task<JsonElement> NextAsync(int timeoutMilliseconds = 10000)
    {
        return _unread.Count > 0 ? _unread.Dequeue() : await ReadFromSocketAsync(timeoutMilliseconds);
    }

    /// <summary>The next frame of a type; the frames before it are skipped.</summary>
    public async Task<JsonElement> NextOfAsync(string type)
    {
        while (true)
        {
            JsonElement frame = await NextAsync();
            if (TypeOf(frame) == type)
            {
                return frame;
            }
        }
    }

    /// <summary>Waits until a frame that satisfies a condition arrives; the frames before it are skipped.</summary>
    public async Task<JsonElement> NextWhereAsync(Func<JsonElement, bool> condition)
    {
        while (true)
        {
            JsonElement frame = await NextAsync();
            if (condition(frame))
            {
                return frame;
            }
        }
    }

    /// <summary>Fails the test when a frame arrives within the time.</summary>
    public async Task ExpectNoFrameAsync(int milliseconds = 300)
    {
        if (_unread.Count > 0)
        {
            Assert.Fail("A frame was not expected: " + _unread.Peek());
        }

        using var limit = new CancellationTokenSource(milliseconds);
        try
        {
            JsonElement frame = await _frames.Reader.ReadAsync(limit.Token);
            Assert.Fail("A frame was not expected: " + frame);
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived, which is the point.
        }
    }

    /// <summary>Fails the test when a frame for <paramref name="agentId"/> arrives within the time, ignoring frames from other agents.</summary>
    public async Task ExpectNoFrameForAsync(string agentId, int milliseconds = 300)
    {
        foreach (JsonElement queued in _unread)
        {
            if (AgentOf(queued) == agentId || queued.GetRawText().Contains(agentId, StringComparison.Ordinal))
            {
                Assert.Fail("A frame for " + agentId + " was not expected: " + queued);
            }
        }

        using var limit = new CancellationTokenSource(milliseconds);
        try
        {
            while (true)
            {
                JsonElement frame = await _frames.Reader.ReadAsync(limit.Token);
                if (AgentOf(frame) == agentId || frame.GetRawText().Contains(agentId, StringComparison.Ordinal))
                {
                    Assert.Fail("A frame for " + agentId + " was not expected: " + frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived for the excluded agent, which is the point.
        }
    }

    /// <summary>Sends a text frame as it is.</summary>
    public async Task SendTextAsync(string json)
    {
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>Sends a binary frame.</summary>
    public async Task SendBinaryAsync(byte[] data)
    {
        await _socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>The data of an ok result; fails the test for an error result.</summary>
    public static JsonElement DataOf(JsonElement result)
    {
        Assert.True(result.GetProperty("ok").GetBoolean(), "The result is not ok: " + result);
        return result.GetProperty("data");
    }

    /// <summary>The error code of a failed result; fails the test for an ok result.</summary>
    public static string ErrorCodeOf(JsonElement result)
    {
        Assert.False(result.GetProperty("ok").GetBoolean(), "The result is ok: " + result);
        return result.GetProperty("error").GetProperty("code").GetString()!;
    }

    /// <summary>The agents of the result of <c>get_agents</c>, by id.</summary>
    public static Dictionary<string, JsonElement> AgentsOf(JsonElement result)
    {
        Assert.Empty(FleetSchemas.Validate(result, FleetSchemas.GetAgentsResult));
        return DataOf(result).GetProperty("agents").EnumerateArray().ToDictionary(agent => agent.GetProperty("id").GetString()!, agent => agent, StringComparer.Ordinal);
    }

    private static bool IsResultOf(JsonElement frame, long id)
    {
        return TypeOf(frame) == MessageTypes.Result && frame.TryGetProperty("id", out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.GetInt64() == id;
    }

    private async Task<JsonElement> ReadFromSocketAsync(int timeoutMilliseconds)
    {
        using var limit = new CancellationTokenSource(timeoutMilliseconds);
        try
        {
            return await _frames.Reader.ReadAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Timed out waiting for the hub to send a frame.");
            throw;
        }
        catch (ChannelClosedException)
        {
            Assert.Fail("The connection ended while a frame was expected.");
            throw;
        }
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _ = _closed.TrySetResult((int)(result.CloseStatus ?? WebSocketCloseStatus.Empty));
                    try
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    }
                    catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        // The hub has dropped the connection already.
                    }

                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);
                JsonElement frame = document.RootElement.Clone();
                Check(frame);
                _all.Enqueue(frame);
                _ = _frames.Writer.TryWrite(frame);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException or JsonException)
        {
            if (exception is JsonException)
            {
                _problems.Enqueue("The hub sent a frame that is not JSON.");
            }
        }
        finally
        {
            _ = _closed.TrySetResult(-1);
            _ = _frames.Writer.TryComplete();
        }
    }

    /// <summary>Checks a frame against the schemas and the rules of the fleet link; the problems fail the test when the client is disposed.</summary>
    private void Check(JsonElement frame)
    {
        string type = TypeOf(frame);
        if (!HubTypes.Contains(type, StringComparer.Ordinal))
        {
            _problems.Enqueue("The hub sent a frame of the type '" + type + "': " + frame);
            return;
        }

        if (ScopedTypes.Contains(type, StringComparer.Ordinal) && AgentOf(frame).Length == 0)
        {
            _problems.Enqueue("An agent-scoped frame has no agent id: " + frame);
        }

        if (type == MessageTypes.Hello && !frame.TryGetProperty("hub", out _))
        {
            _problems.Enqueue("The fleet hello has no hub: " + frame);
        }

        string schema = type == MessageTypes.Result ? FleetSchemas.LinkMessage : FleetSchemas.HubMessage;
        foreach (string error in FleetSchemas.Validate(frame, schema))
        {
            _problems.Enqueue(error + " in " + frame);
        }
    }

    /// <summary>Closes the connection in an orderly way and waits for the hub's close frame.</summary>
    public async Task CloseAsync()
    {
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed already.
        }

        _ = await Closed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The hub has gone away.
        }

        _ = await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(5)));
        await _stop.CancelAsync();
        _stop.Dispose();
        _socket.Dispose();

        // The hub never sends a frame that violates the protocol, whatever the test did.
        Assert.True(_problems.IsEmpty, "The hub sent frames that violate the schemas or the fleet link:\n" + string.Join('\n', _problems));
    }
}

/// <summary>
/// An agent that the test plays by hand and that stays connected to the hub (T913): it answers what the hub asks (<c>get_manifest</c> from its manifest, <c>subscribe</c> with a
/// result and a snapshot of the values it was given, <c>run</c> with a result) and sends state, events and <c>bye</c> when the test says so. What the hub asked is kept.
/// </summary>
internal sealed class ScriptedAgentLink : IAsyncDisposable
{
    private readonly GatewayRig _rig;
    private readonly ConcurrentQueue<JsonElement> _requests = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private string _rev;
    private string[] _metrics;
    private TaskCompletionSource? _runHold;
    private ScriptedAgentSocket _socket = new();
    private Task _serving = Task.CompletedTask;

    private ScriptedAgentLink(GatewayRig rig, string id, string rev, string[] metrics)
    {
        _rig = rig;
        Id = id;
        _rev = rev;
        _metrics = metrics;
    }

    public string Id { get; }

    /// <summary>The values that the agent reports in the snapshot after a <c>subscribe</c>, by metric id.</summary>
    public Dictionary<string, JsonElement> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>The error code that the agent answers a <c>run</c> with; null makes it succeed.</summary>
    public string? RunError { get; set; }

    /// <summary>The version of the agent that its next <c>hello</c> announces; change it before <see cref="ReconnectAsync"/> to play an agent that was updated.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>Everything the hub sent to the agent after its handshake, oldest first.</summary>
    public IReadOnlyList<JsonElement> Requests => [.. _requests];

    public JsonElement[] RequestsOf(string type) => [.. _requests.Where(frame => HubTestLink.TypeOf(frame) == type)];

    /// <summary>The metrics of the newest <c>subscribe</c> that the hub sent: ids in order, or <c>*</c>.</summary>
    public string[] LastSubscribed()
    {
        JsonElement[] subscribes = RequestsOf(MessageTypes.Subscribe);
        return subscribes.Length == 0 ? [] : MetricsOf(subscribes[^1]);
    }

    /// <summary>Connects an agent to the hub and waits until it is online with its manifest cached.</summary>
    public static async Task<ScriptedAgentLink> ConnectAsync(GatewayRig rig, string id, string rev = "aaaaaaaaaaaa", params string[] metrics)
    {
        var link = new ScriptedAgentLink(rig, id, rev, metrics);
        await link.StartAsync();
        return link;
    }

    /// <summary>Connects an agent that announces the given version in its <c>hello</c> (T917) and waits until it is online with its manifest cached.</summary>
    public static async Task<ScriptedAgentLink> ConnectWithVersionAsync(GatewayRig rig, string id, string version, string rev, string[] metrics)
    {
        var link = new ScriptedAgentLink(rig, id, rev, metrics) { Version = version };
        await link.StartAsync();
        return link;
    }

    public static JsonElement Number(double value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Null() => JsonSerializer.SerializeToElement<string?>(null);

    private static string[] MetricsOf(JsonElement subscribe)
    {
        JsonElement metrics = subscribe.GetProperty("metrics");
        return metrics.ValueKind == JsonValueKind.String ? ["*"] : [.. metrics.EnumerateArray().Select(metric => metric.GetString()!)];
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Connects again after <see cref="GoOfflineAsync"/> (or after the hub closed the link) and waits until the agent is online.</summary>
    public async Task ReconnectAsync()
    {
        _socket = new ScriptedAgentSocket();
        await StartAsync();
    }

    private async Task StartAsync()
    {
        ScriptedAgentSocket socket = _socket;
        _serving = _rig.Gateway.ServeAsync(socket, ScriptedAgent.Record(Id), CancellationToken.None);
        socket.SendText(Version == "1.0.0" ? ScriptedAgent.Hello(Id, _rev) : ScriptedAgent.HelloWithVersion(Id, _rev, Version));
        _ = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        _ = Task.Run(() => PumpAsync(socket));
        await TestWait.UntilAsync(
            () => _rig.Gateway.GetPresence(Id).Online && _rig.Gateway.GetManifest(Id)?.Rev == _rev,
            "the scripted agent " + Id + " to be online with its manifest",
            15000);
    }

    /// <summary>Answers what the hub asks, until the connection ends or the link is disposed.</summary>
    private async Task PumpAsync(ScriptedAgentSocket socket)
    {
        while (!_stop.IsCancellationRequested && !socket.Closed.IsCompleted)
        {
            // Polling: the socket's own wait fails the test after ten quiet seconds, which an idle agent must not do.
            if (socket.Pending == 0)
            {
                await Task.Delay(10);
                continue;
            }

            JsonElement frame = await socket.NextFrameAsync();
            _requests.Enqueue(frame);
            long id = frame.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.Number ? idElement.GetInt64() : 0;
            switch (HubTestLink.TypeOf(frame))
            {
                case MessageTypes.GetManifest:
                    string rev;
                    string[] metrics;
                    lock (_gate)
                    {
                        rev = _rev;
                        metrics = _metrics;
                    }

                    socket.SendText(ScriptedAgent.Ok(id, ScriptedAgent.ManifestOf(Id, rev, metrics)));
                    break;
                case MessageTypes.Subscribe:
                    socket.SendText(ScriptedAgent.Ok(id));
                    SendSnapshot(socket, MetricsOf(frame));
                    break;
                case MessageTypes.Run:
                    _ = Task.Run(() => AnswerRunAsync(socket, id, frame));
                    break;
            }
        }
    }

    private void SendSnapshot(ScriptedAgentSocket socket, string[] subscribed)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach ((string metric, JsonElement value) in Values)
            {
                if ((subscribed.Length == 1 && subscribed[0] == "*") || subscribed.Contains(metric, StringComparer.Ordinal))
                {
                    values[metric] = value;
                }
            }
        }

        if (values.Count > 0)
        {
            socket.SendText(LinkCodec.Serialize(new StateMessage(Now(), values)));
        }
    }

    private async Task AnswerRunAsync(ScriptedAgentSocket socket, long id, JsonElement frame)
    {
        TaskCompletionSource? hold = Volatile.Read(ref _runHold);
        if (hold is not null)
        {
            await hold.Task;
        }

        if (RunError is { } code)
        {
            socket.SendText(ScriptedAgent.Fail(id, code));
            return;
        }

        bool wait = !frame.TryGetProperty("wait", out JsonElement waitElement) || waitElement.ValueKind != JsonValueKind.False;
        string data = wait
            ? "{\"run_id\":\"run-1\",\"exit_code\":0,\"stdout\":\"done\",\"stderr\":\"\",\"duration_ms\":12,\"timed_out\":false}"
            : "{\"run_id\":\"run-1\"}";
        using JsonDocument document = JsonDocument.Parse(data);
        socket.SendText(ScriptedAgent.Ok(id, document.RootElement.Clone()));
    }

    /// <summary>Keeps the answers to <c>run</c> back until <see cref="ReleaseRuns"/> is called, like a command that takes long.</summary>
    public void HoldRuns() => Volatile.Write(ref _runHold, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Answers the held <c>run</c> requests.</summary>
    public void ReleaseRuns() => Volatile.Read(ref _runHold)?.TrySetResult();

    /// <summary>Reports changed values to the hub and remembers them for the next snapshot.</summary>
    public void SendState(params (string Metric, JsonElement Value)[] values)
    {
        var changed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach ((string metric, JsonElement value) in values)
            {
                Values[metric] = value;
                changed[metric] = value;
            }
        }

        _socket.SendText(LinkCodec.Serialize(new StateMessage(Now(), changed)));
    }

    /// <summary>Reports an event to the hub.</summary>
    public void SendEvent(string type, string? dataJson = null)
    {
        JsonElement? data = null;
        if (dataJson is not null)
        {
            using JsonDocument document = JsonDocument.Parse(dataJson);
            data = document.RootElement.Clone();
        }

        _socket.SendText(LinkCodec.Serialize(new EventMessage(type, Now(), data)));
    }

    /// <summary>Changes the manifest of the agent and tells the hub; waits until the hub has cached the new revision.</summary>
    public async Task ChangeManifestAsync(string rev, params string[] metrics)
    {
        lock (_gate)
        {
            _rev = rev;
            _metrics = metrics;
        }

        _socket.SendText(LinkCodec.Serialize(new ManifestChangedMessage(rev)));
        await TestWait.UntilAsync(() => _rig.Gateway.GetManifest(Id)?.Rev == rev, "the hub to cache the new manifest of " + Id, 15000);
    }

    /// <summary>The computer goes to sleep: the agent says <c>bye</c> and closes its link. Waits until the hub sees the agent offline.</summary>
    public async Task GoOfflineAsync(string reason = ByeReasons.Sleep)
    {
        _socket.SendText(LinkCodec.Serialize(new ByeMessage(reason)));
        _socket.SendClose();
        await _serving.WaitAsync(TimeSpan.FromSeconds(15));
        await TestWait.UntilAsync(() => !_rig.Gateway.GetPresence(Id).Online, "the hub to see " + Id + " offline", 15000);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (!_socket.Closed.IsCompleted)
        {
            _socket.SendClose();
        }

        try
        {
            await _serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // The hub is stopping anyway.
        }

        _stop.Dispose();
    }
}

/// <summary>The hub with the gateway, enrolled agents that the test plays by hand and clients of the fleet link.</summary>
internal sealed class FleetRig : IAsyncDisposable
{
    private readonly GatewayRig _gateway;
    private readonly List<ScriptedAgentLink> _agents = [];
    private readonly List<FleetClient> _clients = [];

    private FleetRig(GatewayRig gateway)
    {
        _gateway = gateway;
    }

    public HubTestHost Hub => _gateway.Hub;

    public AgentGateway Gateway => _gateway.Gateway;

    public GatewayRig Rig => _gateway;

    /// <summary>Starts the hub; <paramref name="configure"/> changes the service collection, for example to replace the options of the fleet link.</summary>
    public static async Task<FleetRig> StartAsync(Action<IServiceCollection>? configure = null, bool withUsers = false)
    {
        GatewayRig rig = await GatewayRig.StartAsync(withUsers: withUsers, configure: configure);
        return new FleetRig(rig);
    }

    /// <summary>Enrolls an agent at the hub (with the tags of its enrollment token) without connecting it.</summary>
    public async Task EnrollAsync(string agentId, string name, params string[] tags)
    {
        TestToken token = await EnrollmentKit.CreateTokenAsync(Hub, name: "token-" + agentId, maxUses: 1, tags: tags.Length == 0 ? null : tags);
        using HttpResponseMessage response = await EnrollmentKit.EnrollAsync(Hub, token.Secret, agentId, rawBody: EnrollmentKit.EnrollBody(token.Secret, agentId, name));
        JsonNode body = await HubAssert.JsonAsync(response);
        Assert.Equal("enrolled", body["state"]!.GetValue<string>());
    }

    /// <summary>Enrolls an agent and connects it.</summary>
    public async Task<ScriptedAgentLink> AddAgentAsync(string agentId, string name, string[]? tags = null, string[]? metrics = null, string rev = "aaaaaaaaaaaa", string version = "1.0.0")
    {
        await EnrollAsync(agentId, name, tags ?? []);
        if (version == "1.0.0")
        {
            return await ConnectAgentAsync(agentId, rev, metrics ?? []);
        }

        ScriptedAgentLink link = await ScriptedAgentLink.ConnectWithVersionAsync(_gateway, agentId, version, rev, metrics ?? []);
        _agents.Add(link);
        return link;
    }

    /// <summary>Connects an agent that is enrolled already.</summary>
    public async Task<ScriptedAgentLink> ConnectAgentAsync(string agentId, string rev = "aaaaaaaaaaaa", params string[] metrics)
    {
        ScriptedAgentLink link = await ScriptedAgentLink.ConnectAsync(_gateway, agentId, rev, metrics);
        _agents.Add(link);
        return link;
    }

    /// <summary>Creates a pairing code as the administrator and returns the answer.</summary>
    public async Task<JsonNode> CreatePairingAsync(string? scope = null, string[]? tags = null, string? name = null)
    {
        var body = new JsonObject();
        if (scope is not null)
        {
            body["scope"] = scope;
        }

        if (tags is not null)
        {
            body["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]);
        }

        if (name is not null)
        {
            body["name"] = name;
        }

        using HttpResponseMessage response = await Hub.Admin.PostAsync("/api/v1/fleet-pairings", body.ToJsonString());
        return await HubAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    /// <summary>Presents a pairing code at <c>POST /hub/v1/pair</c> as Home Assistant does (no session, no anti-forgery header).</summary>
    public async Task<HttpResponseMessage> PairRequestAsync(string code, string kind = "homeassistant", string name = "Home", string? remoteIp = null)
    {
        HubApiClient client = Hub.CreateClient();
        client.SendCsrf = false;
        client.RemoteIp = remoteIp;
        var body = new JsonObject
        {
            ["code"] = code,
            ["client"] = new JsonObject { ["kind"] = kind, ["name"] = name },
        };
        return await client.PostAsync("/hub/v1/pair", body.ToJsonString());
    }

    /// <summary>Creates a pairing code and exchanges it for a token.</summary>
    public async Task<PairedToken> PairAsync(string? scope = null, string[]? tags = null, string name = "Home")
    {
        JsonNode pairing = await CreatePairingAsync(scope, tags);
        using HttpResponseMessage response = await PairRequestAsync(pairing["code"]!.GetValue<string>(), name: name);
        JsonNode paired = await HubAssert.JsonAsync(response);
        string secret = paired["token"]!.GetValue<string>();
        return new PairedToken(secret, await TokenIdAsync(secret));
    }

    /// <summary>The id of a token that was paired, found by the prefix of its secret in the list of the management API.</summary>
    public async Task<string> TokenIdAsync(string secret)
    {
        using HttpResponseMessage response = await Hub.Admin.GetAsync("/api/v1/fleet-tokens");
        JsonNode list = await HubAssert.JsonAsync(response);
        JsonNode token = list["tokens"]!.AsArray().Single(candidate => candidate!["prefix"]!.GetValue<string>() == secret[..6])!;
        return token["id"]!.GetValue<string>();
    }

    /// <summary>
    /// Asks for the fleet link with a plain GET (no WebSocket upgrade), as a client does that presents a token that is refused, and reports the status and the error code of the
    /// answer. A token that is accepted gets as far as the check for the upgrade and is answered with 400 <c>bad_request</c>.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Code)> OpenLinkWithoutUpgradeAsync(string? token)
    {
        HubApiClient client = Hub.CreateClient();
        client.SendCsrf = false;
        client.BearerToken = token;
        using HttpResponseMessage response = await client.GetAsync(HubProtocol.FleetLinkPath);
        string text = await response.Content.ReadAsStringAsync();
        string code = JsonNode.Parse(text)?["error"]?["code"]?.GetValue<string>() ?? string.Empty;
        return (response.StatusCode, code);
    }

    /// <summary>Opens the fleet link with a token.</summary>
    public async Task<FleetClient> ConnectClientAsync(PairedToken token)
    {
        FleetClient client = await FleetClient.ConnectAsync(Hub, token.Secret);
        _clients.Add(client);
        return client;
    }

    /// <summary>Pairs a token and opens the fleet link with it.</summary>
    public async Task<FleetClient> ConnectClientAsync(string? scope = null, string[]? tags = null)
    {
        return await ConnectClientAsync(await PairAsync(scope, tags));
    }

    /// <summary>Replaces the tags of an agent through the management API.</summary>
    public async Task SetTagsAsync(string agentId, params string[] tags)
    {
        var body = new JsonObject { ["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode?)JsonValue.Create(tag))]) };
        using HttpResponseMessage response = await Hub.Admin.PatchAsync("/api/v1/agents/" + agentId, body.ToJsonString());
        _ = await HubAssert.JsonAsync(response);
    }

    /// <summary>The entries of the audit log for an action, newest first.</summary>
    public async Task<JsonArray> AuditAsync(string action)
    {
        using HttpResponseMessage response = await Hub.Admin.GetAsync("/api/v1/audit?action=" + action);
        JsonNode page = await HubAssert.JsonAsync(response);
        return page["entries"]!.AsArray();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (FleetClient client in _clients)
        {
            await client.DisposeAsync();
        }

        foreach (ScriptedAgentLink agent in _agents)
        {
            await agent.DisposeAsync();
        }

        await _gateway.DisposeAsync();
    }
}
