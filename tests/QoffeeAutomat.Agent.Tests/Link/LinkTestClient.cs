using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Tests.Schema;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>The protocol schemas of the repository (protocol/schema), loaded once with the validator of the contracts tests.</summary>
internal static class LinkSchemas
{
    private const string SchemaBase = "https://github.com/toeck98/QoffeeAutomat/protocol/schema/";

    private static readonly Lazy<SchemaSet> Shared = new(() => SchemaSet.Load(Path.Combine(FindRoot(), "protocol", "schema")));

    /// <summary>The schema that dispatches on <c>t</c>.</summary>
    public static string Message { get; } = SchemaBase + "message.schema.json";

    /// <summary>The schema of the manifest.</summary>
    public static string Manifest { get; } = SchemaBase + "manifest.schema.json";

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

/// <summary>
/// An in-process QoffeeLink client (T210). It connects to the test server of the agent with the real
/// upgrade request, keeps every frame it receives and checks each of them against the protocol schemas;
/// disposing the client fails the test when the agent ever sent a frame that does not validate. The client
/// answers a close frame of the agent and closes its own side in an orderly way when it is disposed.
/// </summary>
internal sealed class LinkTestClient : IAsyncDisposable
{
    public const string Url = "wss://localhost" + ProtocolInfo.LinkPath;

    private const int BufferSize = 64 * 1024;

    // The types an agent may send; anything else means the agent invented a frame.
    private static readonly string[] AgentTypes =
    [
        MessageTypes.Hello,
        MessageTypes.Result,
        MessageTypes.State,
        MessageTypes.Event,
        MessageTypes.Bye,
        MessageTypes.ManifestChanged,
    ];

    private readonly object _gate = new();
    private readonly WebSocket _socket;
    private readonly List<JsonElement> _frames = [];
    private readonly List<string> _problems = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _reader = Task.CompletedTask;
    private long _lastId;
    private int _closeStatus = -1;
    private string _closeDescription = string.Empty;
    private bool _disposed;

    private LinkTestClient(WebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>The close code that the agent sent, or null while no close frame has arrived.</summary>
    public int? CloseStatus
    {
        get
        {
            int status = Volatile.Read(ref _closeStatus);
            return status < 0 ? null : status;
        }
    }

    /// <summary>The close description that the agent sent.</summary>
    public string CloseDescription
    {
        get
        {
            lock (_gate)
            {
                return _closeDescription;
            }
        }
    }

    /// <summary>The sub-protocol that the agent selected.</summary>
    public string? SubProtocol => _socket.SubProtocol;

    /// <summary>True while the connection can carry traffic.</summary>
    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>True once the agent has closed the connection, orderly or not.</summary>
    public bool HasEnded => _reader.IsCompleted;

    /// <summary>The number of frames received so far.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>A copy of the frames received so far, oldest first.</summary>
    public JsonElement[] Frames
    {
        get
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }
    }

    /// <summary>Problems found in the frames of the agent (schema violations, unknown types, binary frames).</summary>
    public string[] Problems
    {
        get
        {
            lock (_gate)
            {
                return [.. _problems];
            }
        }
    }

    /// <summary>Opens the WebSocket as an upgrade request with a bearer token; throws when the agent refuses the upgrade.</summary>
    /// <param name="agent">The test agent.</param>
    /// <param name="token">The link token; null sends no Authorization header.</param>
    /// <param name="subProtocols">The offered sub-protocols; the default offers the one of QoffeeLink.</param>
    /// <returns>The open socket.</returns>
    /// <exception cref="InvalidOperationException">The agent answered with an HTTP error; the message names the status code.</exception>
    public static async Task<WebSocket> OpenSocketAsync(TestAgent agent, string? token, params string[] subProtocols)
    {
        ArgumentNullException.ThrowIfNull(agent);

        WebSocketClient webSocket = agent.Server.CreateWebSocketClient();
        foreach (string subProtocol in subProtocols.Length == 0 ? [ProtocolInfo.SubProtocol] : subProtocols)
        {
            webSocket.SubProtocols.Add(subProtocol);
        }

        webSocket.ConfigureRequest = request =>
        {
            if (token is not null)
            {
                request.Headers["Authorization"] = "Bearer " + token;
            }
        };

        using var timeout = new CancellationTokenSource(TestWait.DefaultTimeout);
        return await webSocket.ConnectAsync(new Uri(Url), timeout.Token);
    }

    /// <summary>Connects with a link token and waits for <c>hello</c>; the client does not answer it.</summary>
    /// <param name="agent">The test agent.</param>
    /// <param name="token">The link token.</param>
    /// <returns>The connected client.</returns>
    public static async Task<LinkTestClient> ConnectAsync(TestAgent agent, string token)
    {
        WebSocket socket = await OpenSocketAsync(agent, token);
        var client = new LinkTestClient(socket);
        client._reader = Task.Run(client.ReadLoopAsync);
        await client.WaitForAsync(frame => TypeOf(frame) == MessageTypes.Hello, "hello");
        return client;
    }

    /// <summary>
    /// Wraps a socket that the agent opened itself (the reverse link, T710): the test plays the peer that accepted it. Waits for <c>hello</c>,
    /// which the agent sends first no matter who dialled; the client does not answer it.
    /// </summary>
    /// <param name="socket">The accepted socket.</param>
    /// <returns>The client that reads the frames of the agent.</returns>
    public static async Task<LinkTestClient> AttachAsync(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        var client = new LinkTestClient(socket);
        client._reader = Task.Run(client.ReadLoopAsync);
        await client.WaitForAsync(frame => TypeOf(frame) == MessageTypes.Hello, "hello");
        return client;
    }

    /// <summary>Connects and answers <c>hello</c> with <c>client_hello</c> for protocol version 1.</summary>
    /// <param name="agent">The test agent.</param>
    /// <param name="token">The link token.</param>
    /// <returns>The client, ready for requests.</returns>
    public static async Task<LinkTestClient> ConnectAndGreetAsync(TestAgent agent, string token)
    {
        LinkTestClient client = await ConnectAsync(agent, token);
        await client.SendAsync(ClientHello(ProtocolInfo.Version));
        return client;
    }

    /// <summary>The text of a <c>client_hello</c> frame.</summary>
    public static string ClientHello(int protocol)
    {
        return "{\"t\":\"client_hello\",\"protocol\":" + protocol.ToString(CultureInfo.InvariantCulture)
            + ",\"client\":{\"kind\":\"homeassistant\",\"name\":\"Test client\",\"version\":\"1.0.0\"}}";
    }

    /// <summary>The message type of a frame, or an empty string.</summary>
    public static string TypeOf(JsonElement frame)
    {
        return frame.ValueKind == JsonValueKind.Object
            && frame.TryGetProperty("t", out JsonElement type)
            && type.ValueKind == JsonValueKind.String
                ? type.GetString()!
                : string.Empty;
    }

    /// <summary>True when the frame is the result with the given request id.</summary>
    public static bool IsResult(JsonElement frame, long id)
    {
        return TypeOf(frame) == MessageTypes.Result
            && frame.TryGetProperty("id", out JsonElement value)
            && value.TryGetInt64(out long actual)
            && actual == id;
    }

    /// <summary>The error code of a failed result, or null.</summary>
    public static string? ErrorCodeOf(JsonElement frame)
    {
        return frame.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("code", out JsonElement code)
            ? code.GetString()
            : null;
    }

    /// <summary>The metric ids that a <c>state</c> frame carries.</summary>
    public static string[] MetricsOf(JsonElement frame)
    {
        return [.. frame.GetProperty("v").EnumerateObject().Select(member => member.Name)];
    }

    /// <summary>Sends one text frame.</summary>
    public async Task SendAsync(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using var timeout = new CancellationTokenSource(TestWait.DefaultTimeout);
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
    }

    /// <summary>Sends a request with a new id and returns the id without waiting for the answer.</summary>
    /// <param name="type">The message type.</param>
    /// <param name="members">More members of the object as JSON text without braces, for example <c>"metrics":"*"</c>.</param>
    public async Task<long> SendRequestAsync(string type, string? members = null)
    {
        long id = Interlocked.Increment(ref _lastId);
        string json = "{\"t\":\"" + type + "\",\"id\":" + id.ToString(CultureInfo.InvariantCulture)
            + (members is null ? string.Empty : "," + members) + "}";
        await SendAsync(json);
        return id;
    }

    /// <summary>Sends a request and waits for its result.</summary>
    public async Task<JsonElement> RequestAsync(string type, string? members = null)
    {
        long id = await SendRequestAsync(type, members);
        return await WaitForResultAsync(id);
    }

    /// <summary>Waits for the result of a request.</summary>
    public Task<JsonElement> WaitForResultAsync(long id)
    {
        return WaitForAsync(frame => IsResult(frame, id), "the result of request " + id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Waits until a frame that matches has arrived and returns it; frames that arrived earlier count too.</summary>
    /// <param name="predicate">Selects the frame.</param>
    /// <param name="what">Names the frame in the failure message.</param>
    /// <param name="after">Only frames at this index or later are considered; use <see cref="Count"/> taken earlier.</param>
    /// <param name="timeout">How long to wait; the default is ten seconds.</param>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, string what, int after = 0, TimeSpan? timeout = null)
    {
        JsonElement found = default;
        await TestWait.UntilAsync(() => TryFind(predicate, after, out found), what, timeout);
        return found;
    }

    /// <summary>Waits until a frame of a type has arrived.</summary>
    public Task<JsonElement> WaitForTypeAsync(string type, int after = 0, TimeSpan? timeout = null)
    {
        return WaitForAsync(frame => TypeOf(frame) == type, "a " + type + " frame", after, timeout);
    }

    /// <summary>The position of a frame that this client received; -1 for any other element.</summary>
    public int IndexOf(JsonElement frame)
    {
        lock (_gate)
        {
            return _frames.FindIndex(candidate => candidate.Equals(frame));
        }
    }

    /// <summary>The frames from an index on that match.</summary>
    public JsonElement[] FramesOf(string type, int after = 0)
    {
        lock (_gate)
        {
            return [.. _frames.Skip(after).Where(frame => TypeOf(frame) == type)];
        }
    }

    /// <summary>Waits until the agent has closed the connection and returns the close code (null when it ended without a close frame).</summary>
    public async Task<int?> WaitForCloseAsync()
    {
        await TestWait.WithTimeoutAsync(_reader, "the agent to close the connection");
        return CloseStatus;
    }

    /// <summary>Closes the connection in an orderly way: sends the close frame and waits for the one of the agent.</summary>
    public async Task CloseAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TestWait.DefaultTimeout);
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The agent closed first; the reader has seen it.
            }
        }

        await TestWait.WithTimeoutAsync(_reader, "the connection to end after the close frame");
    }

    /// <summary>Closes the connection with a close code, as a peer does that rejects the agent (4401), and waits for the close frame of the agent.</summary>
    /// <param name="code">The close code.</param>
    /// <param name="reason">The close reason.</param>
    public async Task CloseWithAsync(int code, string reason)
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TestWait.DefaultTimeout);
            await _socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token);
        }

        await TestWait.WithTimeoutAsync(_reader, "the connection to end after the close frame");
    }

    /// <summary>Closes the connection, then fails the test when a frame of the agent did not validate.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await CloseAsync();
        }
        finally
        {
            await _stop.CancelAsync();
            _socket.Dispose();
            _stop.Dispose();
        }

        Assert.Empty(Problems);
    }

    private bool TryFind(Func<JsonElement, bool> predicate, int after, out JsonElement found)
    {
        lock (_gate)
        {
            for (int index = Math.Max(after, 0); index < _frames.Count; index++)
            {
                if (predicate(_frames[index]))
                {
                    found = _frames[index];
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await AnswerCloseAsync(result);
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Accept(result.MessageType, message.ToArray());
            }
        }
        catch (Exception exception) when (exception is WebSocketException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The connection ended without a close frame, or the client is being disposed.
        }
    }

    private async Task AnswerCloseAsync(WebSocketReceiveResult close)
    {
        Volatile.Write(ref _closeStatus, (int)(close.CloseStatus ?? WebSocketCloseStatus.Empty));
        lock (_gate)
        {
            _closeDescription = close.CloseStatusDescription ?? string.Empty;
        }

        if (_socket.State == WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TestWait.DefaultTimeout);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        }
    }

    private void Accept(WebSocketMessageType type, byte[] data)
    {
        if (type != WebSocketMessageType.Text)
        {
            AddProblem("The agent sent a binary frame.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement frame = document.RootElement.Clone();
            string messageType = TypeOf(frame);
            if (!AgentTypes.Contains(messageType))
            {
                AddProblem("The agent sent a frame of the type '" + messageType + "'.");
            }

            foreach (string error in LinkSchemas.Validate(frame, LinkSchemas.Message))
            {
                AddProblem(messageType + ": " + error);
            }

            lock (_gate)
            {
                _frames.Add(frame);
            }
        }
        catch (JsonException exception)
        {
            AddProblem("The agent sent a frame that is not JSON: " + exception.Message);
        }
    }

    private void AddProblem(string problem)
    {
        lock (_gate)
        {
            _problems.Add(problem);
        }
    }
}
