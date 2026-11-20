using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Gateway;

/// <summary>What <see cref="AgentSession.Send(byte[])"/> did with a frame.</summary>
internal enum SendOutcome
{
    /// <summary>The frame is queued.</summary>
    Sent,

    /// <summary>The connection is closing or closed.</summary>
    Closed,

    /// <summary>The queue is full: the agent does not read its frames. The session has been closed.</summary>
    Overflow,

    /// <summary>The frame is larger than the limit and was not sent.</summary>
    TooLarge,
}

/// <summary>The agent is not connected (any more): thrown by a request that cannot be sent or whose connection ended before the result came.</summary>
internal sealed class AgentOfflineException : Exception
{
    public AgentOfflineException()
        : base("The agent is not connected.")
    {
    }
}

/// <summary>Too many requests to the same agent are in progress.</summary>
internal sealed class GatewayBusyException : Exception
{
    public GatewayBusyException()
        : base("Too many requests to this agent are in progress.")
    {
    }
}

/// <summary>
/// One agent link as the hub sees it (PLAN.md S07, S20, T906): the accepted WebSocket of an agent whose credential was checked. The agent speaks first (<c>hello</c>); the
/// hub answers with <c>client_hello</c> as a client of the kind <c>hub</c> and then uses the standard messages. The session owns the bounded send queue, the correlation of
/// requests and results and the rules for an agent that misbehaves: a frame that is not valid, too large, binary or too frequent, a handshake that violates the protocol,
/// a queue that overflows, a metric table that grows without bound. Such an agent is disconnected with a close code and the reason is logged. The session does not know
/// what the frames mean; it hands them to the <see cref="AgentGateway"/>.
/// </summary>
internal sealed class AgentSession : IAsyncDisposable
{
    private const int PolicyViolation = (int)WebSocketCloseStatus.PolicyViolation;

    private readonly AgentGateway _gateway;
    private readonly WebSocket _socket;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _outbound;
    private readonly byte[] _receiveBuffer;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<string, AgentStreamRelay> _streams = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task _writer = Task.CompletedTask;
    private ITimer? _closeTimer;
    private long _lastRequestId;
    private long _lastFrameTicks;
    private long _windowStart;
    private int _windowCount;
    private int _unsolicitedResults;
    private int _closeRequested;
    private int _aborted;
    private int _closeCode = CloseCodes.Normal;
    private string _closeReason = string.Empty;
    private bool _handshakeReceived;
    private volatile bool _established;

    public AgentSession(AgentGateway gateway, string agentId, WebSocket socket, GatewayOptions options, TimeProvider time, ILogger logger)
    {
        _gateway = gateway;
        AgentId = agentId;
        _socket = socket;
        _options = options;
        _time = time;
        _logger = logger;
        _receiveBuffer = new byte[options.ReceiveBufferBytes];
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.SendQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _lastFrameTicks = time.GetUtcNow().UtcTicks;
        _windowStart = time.GetTimestamp();
    }

    /// <summary>The agent whose credential opened the link.</summary>
    public string AgentId { get; }

    /// <summary>Completes when the connection has ended and the session has told the gateway.</summary>
    public Task Ended => _ended.Task;

    /// <summary>True once the handshake is complete and while the connection is not closing.</summary>
    public bool IsOpen => _established && Volatile.Read(ref _closeRequested) == 0;

    /// <summary>When the last frame arrived.</summary>
    public DateTimeOffset LastFrameAt => new(Interlocked.Read(ref _lastFrameTicks), TimeSpan.Zero);

    /// <summary>The number of tunnelled event streams that are open on this connection.</summary>
    public int StreamCount => _streams.Count;

    /// <summary>Registers an open stream; the receive loop delivers its events from now on.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>False when a stream with the same id is registered already (the agent reused an id).</returns>
    public bool TryAddStream(AgentStreamRelay stream) => _streams.TryAdd(stream.Sid, stream);

    /// <summary>Forgets a stream; nothing is sent.</summary>
    /// <param name="stream">The stream.</param>
    public void RemoveStream(AgentStreamRelay stream) => _ = _streams.TryRemove(new KeyValuePair<string, AgentStreamRelay>(stream.Sid, stream));

    /// <summary>Runs the session until the connection ends.</summary>
    /// <param name="requestAborted">Fires when the transport under the socket is gone.</param>
    /// <returns>A task that completes when the connection has ended and the session has cleaned up.</returns>
    public async Task RunAsync(CancellationToken requestAborted)
    {
        using CancellationTokenRegistration aborted = requestAborted.Register(static state => ((AgentSession)state!).Abort(), this);
        _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        bool attached = false;
        try
        {
            HelloMessage? hello = await ReceiveHelloAsync().ConfigureAwait(false);
            if (hello is not null)
            {
                // The answer first, so that the agent can start to talk; then this session replaces an older one of the same agent.
                _ = Send(_gateway.CreateClientHello());
                _gateway.Attach(this, hello);
                attached = true;
                _established = true;
                _gateway.StartSynchronisation(this, hello.ManifestRev);
                await ReceiveLoopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            // The agent went away or the hub is stopping; cleanup does the rest.
        }
#pragma warning disable CA1031 // A failing connection must not take the hub down; it is closed with 1011 and logged.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            GatewayLog.Fault(_logger, AgentId, exception.GetType().Name);
            Close(CloseCodes.InternalError, "Internal error.");
        }
        finally
        {
            await CleanupAsync(attached).ConfigureAwait(false);
        }
    }

    /// <summary>Queues a message.</summary>
    /// <param name="message">The message.</param>
    /// <returns>What became of it.</returns>
    public SendOutcome Send(LinkMessage message)
    {
        return Send(LinkCodec.SerializeToUtf8(message));
    }

    /// <summary>Queues a frame. An agent whose queue is full is disconnected.</summary>
    /// <param name="frame">The UTF-8 JSON of the frame.</param>
    /// <returns>What became of it.</returns>
    public SendOutcome Send(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Length > _options.MaxFrameBytes)
        {
            return SendOutcome.TooLarge;
        }

        if (Volatile.Read(ref _closeRequested) != 0)
        {
            return SendOutcome.Closed;
        }

        if (_outbound.Writer.TryWrite(frame))
        {
            return SendOutcome.Sent;
        }

        if (Volatile.Read(ref _closeRequested) != 0)
        {
            return SendOutcome.Closed;
        }

        Violation("its send queue is full: the agent does not read its frames", PolicyViolation, "The agent does not read its frames.");
        return SendOutcome.Overflow;
    }

    /// <summary>Sends a request and waits for its result.</summary>
    /// <param name="build">Creates the frame for the request id that the session chose.</param>
    /// <param name="timeout">How long to wait for the result.</param>
    /// <param name="cancellationToken">Cancels the wait; the request itself cannot be taken back.</param>
    /// <param name="onResult">Runs on the receive loop when the result arrives, before the caller continues and before the next frame is read; the gateway uses it to be ready for what follows.</param>
    /// <returns>The result of the agent; an error result is returned, not thrown.</returns>
    /// <exception cref="AgentOfflineException">The frame could not be sent or the connection ended before the result.</exception>
    /// <exception cref="GatewayBusyException">Too many requests are in progress.</exception>
    /// <exception cref="TimeoutException">No result arrived within <paramref name="timeout"/>.</exception>
    /// <exception cref="InvalidOperationException">The frame is larger than the limit.</exception>
    public async Task<ResultMessage> RequestAsync(
        Func<long, byte[]> build,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<ResultMessage>? onResult = null)
    {
        ArgumentNullException.ThrowIfNull(build);

        if (_pending.Count >= _options.MaxPendingRequests)
        {
            throw new GatewayBusyException();
        }

        long id = Interlocked.Increment(ref _lastRequestId);
        var pending = new PendingRequest(onResult);
        _pending[id] = pending;
        try
        {
            switch (Send(build(id)))
            {
                case SendOutcome.Sent:
                    break;
                case SendOutcome.TooLarge:
                    throw new InvalidOperationException("The request is larger than a frame may be.");
                default:
                    throw new AgentOfflineException();
            }

            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Closes the connection with a close code after the queued frames.</summary>
    /// <param name="closeCode">The WebSocket close code.</param>
    /// <param name="reason">A short reason for the agent (at most 123 bytes of UTF-8).</param>
    public void Close(int closeCode, string reason)
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) != 0)
        {
            return;
        }

        _closeCode = closeCode;
        _closeReason = reason;
        _outbound.Writer.TryComplete();

        // A peer that does not answer the close frame is dropped.
        _closeTimer = _time.CreateTimer(static state => ((AgentSession)state!).Abort(), this, _options.CloseTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Drops the connection at once, without a close handshake.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        _ = Interlocked.Exchange(ref _closeRequested, 1);
        _outbound.Writer.TryComplete();
        try
        {
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // The socket is already gone.
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session has ended already.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _closeTimer?.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Handshake
    // ------------------------------------------------------------------

    private async Task<HelloMessage?> ReceiveHelloAsync()
    {
        // The agent must speak first and in time. The timer closes the connection with 4400 instead of cancelling the read, because a cancelled read aborts the
        // socket and no close frame could be sent any more.
        using ITimer timer = _time.CreateTimer(
            static state => ((AgentSession)state!).OnHandshakeTimeout(),
            this,
            _options.HandshakeTimeout,
            Timeout.InfiniteTimeSpan);

        Frame frame = await ReadFrameAsync().ConfigureAwait(false);
        _handshakeReceived = true;
        if (frame.Kind == FrameKind.Closed)
        {
            return null;
        }

        if (Volatile.Read(ref _closeRequested) != 0)
        {
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return null;
        }

        switch (frame.Kind)
        {
            case FrameKind.TooLarge:
                Violation("the first frame is too large", CloseCodes.MessageTooBig, "Frame too large.");
                await ReceiveUntilClosedAsync().ConfigureAwait(false);
                return null;
            case FrameKind.Binary:
                Violation("the first frame is binary", CloseCodes.BadRequest, "A hello text frame is expected.");
                await ReceiveUntilClosedAsync().ConfigureAwait(false);
                return null;
        }

        LinkMessage message;
        try
        {
            message = LinkCodec.Parse(frame.Data.Span);
        }
        catch (JsonException)
        {
            Violation("the first frame is not valid", CloseCodes.BadRequest, "A valid hello frame is expected.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return null;
        }

        if (message is not HelloMessage hello)
        {
            Violation("the first frame is not hello", CloseCodes.BadRequest, "A hello frame is expected first.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return null;
        }

        if (hello.Protocol.Min > ProtocolInfo.Version || hello.Protocol.Max < ProtocolInfo.Version)
        {
            Violation("the agent does not speak protocol version " + ProtocolInfo.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), CloseCodes.UnsupportedVersion, "Unsupported protocol version.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return null;
        }

        // The credential belongs to one agent; a hello that names another one is a lie.
        if (!string.Equals(hello.Agent.Id, AgentId, StringComparison.Ordinal))
        {
            Violation("the hello names another agent than the credential", CloseCodes.BadRequest, "The agent id does not match the credential.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return null;
        }

        return hello;
    }

    private void OnHandshakeTimeout()
    {
        if (_handshakeReceived)
        {
            return;
        }

        Violation("no hello in time", CloseCodes.BadRequest, "hello expected.");
    }

    // ------------------------------------------------------------------
    // Receiving
    // ------------------------------------------------------------------

    private async Task ReceiveLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Frame frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame.Kind == FrameKind.Closed)
            {
                return;
            }

            if (Volatile.Read(ref _closeRequested) != 0)
            {
                // The close handshake is under way; wait for the close frame of the agent and ignore the rest.
                continue;
            }

            _ = Interlocked.Exchange(ref _lastFrameTicks, _time.GetUtcNow().UtcTicks);
            if (!CountFrame())
            {
                Violation("it sends more than " + _options.MaxFramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture) + " frames per second", PolicyViolation, "Too many frames.");
                continue;
            }

            switch (frame.Kind)
            {
                case FrameKind.TooLarge:
                    Violation("a frame is larger than the limit", CloseCodes.MessageTooBig, "Frame too large.");
                    break;
                case FrameKind.Binary:
                    Violation("it sent a binary frame", CloseCodes.BadRequest, "Only text frames are allowed.");
                    break;
                default:
                    await HandleTextFrameAsync(frame.Data).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task ReceiveUntilClosedAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Frame frame = await ReadFrameAsync().ConfigureAwait(false);
            if (frame.Kind == FrameKind.Closed)
            {
                return;
            }
        }
    }

    private bool CountFrame()
    {
        long now = _time.GetTimestamp();
        if (now - _windowStart >= _time.TimestampFrequency)
        {
            _windowStart = now;
            _windowCount = 0;
        }

        _windowCount++;
        return _windowCount <= _options.MaxFramesPerSecond;
    }

    private async Task<Frame> ReadFrameAsync()
    {
        var message = new ArrayBufferWriter<byte>();
        while (true)
        {
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(_receiveBuffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Answer the close frame of the agent so that the close handshake completes.
                Close(CloseCodes.Normal, string.Empty);
                return new Frame(FrameKind.Closed, ReadOnlyMemory<byte>.Empty);
            }

            if ((long)message.WrittenCount + result.Count > _options.MaxFrameBytes)
            {
                return new Frame(FrameKind.TooLarge, ReadOnlyMemory<byte>.Empty);
            }

            message.Write(_receiveBuffer.AsSpan(0, result.Count));
            if (result.EndOfMessage)
            {
                return new Frame(
                    result.MessageType == WebSocketMessageType.Binary ? FrameKind.Binary : FrameKind.Text,
                    message.WrittenMemory.ToArray());
            }
        }
    }

    private async Task HandleTextFrameAsync(ReadOnlyMemory<byte> data)
    {
        LinkMessage message;
        try
        {
            message = LinkCodec.Parse(data.Span);
        }
        catch (JsonException)
        {
            Violation("it sent a frame that is not valid QoffeeLink", CloseCodes.BadRequest, "Invalid frame.");
            return;
        }

        switch (message)
        {
            case ResultMessage result:
                HandleResult(result);
                break;
            case StateMessage state:
                if (!_gateway.OnState(this, state))
                {
                    Violation("it reports more metrics than the hub keeps", PolicyViolation, "Too many metrics.");
                }

                break;
            case EventMessage agentEvent:
                await _gateway.OnEventAsync(this, agentEvent, _lifetime.Token).ConfigureAwait(false);
                break;
            case ManifestChangedMessage changed:
                _gateway.OnManifestChanged(this, changed.Rev);
                break;
            case ByeMessage bye:
                _gateway.OnBye(this, bye.Reason);
                break;
            case UnknownMessage { Type: HubMessageTypes.ConfigStatus } configStatus:
                _gateway.OnConfigStatus(this, configStatus.Raw);
                break;
            case UnknownMessage { Type: HubMessageTypes.MgmtStreamData or HubMessageTypes.MgmtStreamClose } streamFrame:
                await HandleStreamFrameAsync(streamFrame).ConfigureAwait(false);
                break;
            default:
                // A second hello, frames that only the hub sends, features that were not negotiated and message types of a later protocol version are ignored (S07).
                break;
        }
    }

    /// <summary>
    /// Delivers an event of a tunnelled stream to its consumer, or ends the stream (T912). A frame of a stream that is not open (it was closed a moment ago) is ignored.
    /// When the consumer's queue stays full for <see cref="GatewayOptions.StreamBackpressureTimeout"/> the stream is closed: waiting here holds back the agent's frames
    /// (the back-pressure), but only for that long, because everything else the agent sends waits behind it.
    /// </summary>
    private async Task HandleStreamFrameAsync(UnknownMessage frame)
    {
        // A message of a feature that was not negotiated is ignored (S20).
        if (!_gateway.IsNegotiated(this, HubFeatures.MgmtTunnel))
        {
            return;
        }

        try
        {
            if (frame.Type == HubMessageTypes.MgmtStreamClose)
            {
                MgmtStreamCloseMessage? close = frame.Raw.Deserialize(HubJsonContext.Default.MgmtStreamCloseMessage);
                if (close is not null && _streams.TryRemove(close.Sid, out AgentStreamRelay? ended))
                {
                    ended.Complete(StreamEndReasons.Completed);
                }

                return;
            }

            MgmtStreamDataMessage? data = frame.Raw.Deserialize(HubJsonContext.Default.MgmtStreamDataMessage);
            if (data is null || !_streams.TryGetValue(data.Sid, out AgentStreamRelay? stream))
            {
                return;
            }

            if (!await stream.PushAsync(new AgentStreamEvent(data.Event, data.Data), _options.StreamBackpressureTimeout, _lifetime.Token).ConfigureAwait(false))
            {
                GatewayLog.StreamOverflow(_logger, AgentId, stream.Sid);
                RemoveStream(stream);
                stream.Complete(StreamEndReasons.SlowConsumer);
                _ = Send(JsonSerializer.SerializeToUtf8Bytes(new MgmtStreamCloseMessage(stream.Sid), HubJsonContext.Default.MgmtStreamCloseMessage));
            }
        }
        catch (JsonException)
        {
            // A frame of the feature that is not valid carries nothing the hub can use; the agent is not punished for it.
        }
    }

    private void HandleResult(ResultMessage result)
    {
        if (!_pending.TryGetValue(result.Id, out PendingRequest? pending))
        {
            // A late result of a request that timed out is harmless; a stream of them is not.
            if (++_unsolicitedResults > _options.MaxUnsolicitedResults)
            {
                Violation("it answers requests that the hub did not make", CloseCodes.BadRequest, "Unexpected results.");
            }

            return;
        }

        try
        {
            pending.OnResult?.Invoke(result);
        }
#pragma warning disable CA1031 // The handler is the gateway's own; a failure must not end the connection, the request still gets its result.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            GatewayLog.Fault(_logger, AgentId, exception.GetType().Name);
        }

        _ = pending.Completion.TrySetResult(result);
    }

    private void Violation(string reason, int closeCode, string closeText)
    {
        if (Volatile.Read(ref _closeRequested) != 0)
        {
            return;
        }

        GatewayLog.Violation(_logger, AgentId, reason);
        Close(closeCode, closeText);
    }

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] frame in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref _aborted) != 0)
                {
                    return;
                }

                using var timeout = new CancellationTokenSource(_options.SendTimeout);
                await _socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _aborted) == 0 && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(_options.SendTimeout);
                await _socket.CloseOutputAsync((WebSocketCloseStatus)_closeCode, _closeReason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The agent does not take its frames in time: it is stalled.
            if (Volatile.Read(ref _aborted) == 0)
            {
                GatewayLog.Violation(_logger, AgentId, "it does not accept frames in time");
            }

            Abort();
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            Abort();
        }
    }

    private async Task CleanupAsync(bool attached)
    {
        // Whatever ended the connection, the writer must send the close frame if the socket is still open.
        Close(CloseCodes.Normal, string.Empty);

        foreach (PendingRequest pending in _pending.Values)
        {
            _ = pending.Completion.TrySetException(new AgentOfflineException());
        }

        // The streams end with the connection; their consumers see agent_offline.
        foreach (AgentStreamRelay stream in _streams.Values)
        {
            stream.Complete(StreamEndReasons.AgentOffline);
        }

        _streams.Clear();

        if (attached)
        {
            await _gateway.DetachAsync(this).ConfigureAwait(false);
        }

        try
        {
            await _writer.WaitAsync(_options.CloseTimeout + _options.SendTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abort();
        }

        Abort();
        _ = _ended.TrySetResult();
    }

    private sealed class PendingRequest(Action<ResultMessage>? onResult)
    {
        public Action<ResultMessage>? OnResult { get; } = onResult;

        public TaskCompletionSource<ResultMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum FrameKind
    {
        Text,
        Binary,
        TooLarge,
        Closed,
    }

    private readonly record struct Frame(FrameKind Kind, ReadOnlyMemory<byte> Data);
}
