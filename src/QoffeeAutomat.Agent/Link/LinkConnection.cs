using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Link;

/// <summary>
/// One accepted QoffeeLink connection (PLAN.md S07, T210). The connection owns a bounded queue of
/// serialised frames and one writer that drains it, so no other component ever waits for a slow client:
/// a client whose queue overflows, or whose socket does not accept a frame in time, is dropped. A single
/// reader parses the frames of the client; <c>get_manifest</c> and <c>subscribe</c> are answered in order,
/// <c>run</c> requests run in the background so that a long command does not block the connection.
/// The metric feed and the event feed of the connection end with it, which is what stops the sampling
/// of metrics that nobody else wants (PLAN.md D-007).
/// </summary>
internal sealed class LinkConnection : ILinkConnection, ILinkExtensionContext, IAsyncDisposable
{
    private static readonly string[] EventTypeNames = [.. EventCatalog.Types.Select(entry => entry.Type)];

    private readonly LinkServer _server;
    private readonly WebSocket _socket;
    private readonly LinkClient _client;

    // False for a connection that the agent opened itself (ADR 0003): there is no link token that the pairing service could revoke.
    private readonly bool _isPaired;

    // The additions of the hub link (T905); null for every other connection.
    private readonly ILinkExtension? _extension;
    private readonly LinkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<OutboundFrame> _outbound;
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _feedGate = new(1, 1);
    private readonly HashSet<Task> _runs = [];
    private readonly object _gate = new();
    private readonly byte[] _receiveBuffer;

    private Task _writer = Task.CompletedTask;
    private Task _watchdog = Task.CompletedTask;
    private long _queuedBytes;
    private volatile bool _closeRequested;
    private volatile bool _handshakeDone;
    private volatile bool _handshakeReceived;
    private int _closeCode = CloseCodes.Normal;
    private string _closeReason = string.Empty;
    private int _aborted;
    private int _peerCloseStatus = -1;
    private int _manifestHandlerAttached;
    private string _announcedRev = string.Empty;
    private IReadOnlyList<string> _peerFeatures = [];
    private IDisposable? _registration;
    private MetricSubscription? _metricSubscription;
    private CancellationTokenSource? _metricPumpStop;
    private Task? _metricPump;
    private EventSubscription? _eventSubscription;
    private Task? _eventPump;

    public LinkConnection(LinkServer server, WebSocket socket, LinkClient client, bool isPaired = true, ILinkExtension? extension = null)
    {
        _server = server;
        _socket = socket;
        _client = client;
        _isPaired = isPaired;
        _extension = extension;
        _options = server.Options;
        _time = server.Time;
        _logger = server.Logger;
        _receiveBuffer = new byte[_options.ReceiveBufferBytes];
        _outbound = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(_options.SendQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Completes when the connection has ended and everything it started is gone.</summary>
    public Task Ended => _ended.Task;

    /// <summary>Completes when the writer has sent every queued frame and the close frame (or has given up).</summary>
    public Task Flushed => _flushed.Task;

    /// <summary>The id of the paired client that owns the connection.</summary>
    public string ClientId => _client.Id;

    /// <summary>The close code that the peer sent, or null while none was received; read it after <see cref="RunAsync"/> returned.</summary>
    public int? PeerCloseStatus
    {
        get
        {
            int status = Volatile.Read(ref _peerCloseStatus);
            return status < 0 ? null : status;
        }
    }

    /// <summary>True once the handshake is complete and the connection is open for traffic.</summary>
    public bool IsEstablished => _handshakeDone && !_closeRequested;

    /// <summary>True for a connection that a paired client opened to the agent (the direct link); false for connections that the agent dialled itself (reverse link, hub link).</summary>
    public bool IsDirect => _isPaired;

    /// <summary>Closes the connection after the queued frames because the hub's policy denies direct links (T908).</summary>
    /// <param name="reason">A short reason for the close frame.</param>
    public void CloseForPolicy(string reason) => RequestClose(CloseCodes.Normal, reason);

    /// <inheritdoc />
    public async ValueTask CloseAsync(int closeCode, string reason, CancellationToken cancellationToken)
    {
        RequestClose(closeCode, reason);
        await _ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells the client that the agent goes away and closes the connection with code 1001 after the queued frames.</summary>
    /// <param name="reason">One of <see cref="ByeReasons"/>.</param>
    public void SayBye(string reason)
    {
        Send(new ByeMessage(reason));
        RequestClose(CloseCodes.GoingAway, "The agent goes away.");
    }

    /// <summary>Drops the connection at once, without a close handshake.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        _closeRequested = true;
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
            // The connection has ended already.
        }
    }

    /// <summary>Runs the connection until it ends.</summary>
    /// <param name="requestAborted">Fires when the transport under the socket is gone.</param>
    /// <returns>A task that completes when the connection has ended and its resources are released.</returns>
    public async Task RunAsync(CancellationToken requestAborted)
    {
        using CancellationTokenRegistration aborted = requestAborted.Register(static state => ((LinkConnection)state!).Abort(), this);
        _writer = Task.Run(WriteLoopAsync, CancellationToken.None);
        try
        {
            _registration = _isPaired
                ? await _server.Pairing.RegisterConnectionAsync(_client, this, _lifetime.Token).ConfigureAwait(false)
                : NoRegistration.Instance;
            if (_registration is null)
            {
                LinkLog.Refused(_logger, "the link token was revoked");
                RequestClose(CloseCodes.Unauthorized, "The link token was revoked.");
                await ReceiveUntilClosedAsync().ConfigureAwait(false);
                return;
            }

            LinkLog.Connected(_logger, _client.Id, _client.Kind);
            _server.Manifest.Changed += OnManifestChanged;
            Interlocked.Exchange(ref _manifestHandlerAttached, 1);

            SendHello();
            if (await ReceiveHandshakeAsync().ConfigureAwait(false))
            {
                StartEventFeed();
                _handshakeDone = true;
                AnnounceManifest();
                if (_extension is not null)
                {
                    // The hub link sends its first config_status here (T908), after the features of the peer are known.
                    await _extension.OnEstablishedAsync(_peerFeatures, this, _lifetime.Token).ConfigureAwait(false);
                }

                await ReceiveLoopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException)
        {
            // The peer went away or the agent is stopping; cleanup does the rest.
        }
#pragma warning disable CA1031 // A failing connection must not take the host down; it is closed with 1011 and logged.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            string failure = exception.GetType().Name;
            LinkLog.Fault(_logger, _client.Id, failure);
            RequestClose(CloseCodes.InternalError, "Internal error.");
        }
        finally
        {
            _extension?.OnClosed(this);
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _lifetime.Dispose();
        _feedGate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Handshake
    // ------------------------------------------------------------------

    private void SendHello()
    {
        HelloMessage hello = _server.CreateHello(_extension?.HelloFeatures);
        lock (_gate)
        {
            _announcedRev = hello.ManifestRev;
        }

        Send(hello);
    }

    private async Task<bool> ReceiveHandshakeAsync()
    {
        // The answer must arrive in time. The timer closes the connection with 4400 instead of cancelling the
        // read, because a cancelled read aborts the socket and no close frame could be sent any more.
        using ITimer timer = _time.CreateTimer(
            static state => ((LinkConnection)state!).OnHandshakeTimeout(),
            this,
            _options.HandshakeTimeout,
            Timeout.InfiniteTimeSpan);

        Frame frame = await ReadFrameAsync(_lifetime.Token).ConfigureAwait(false);
        _handshakeReceived = true;
        if (frame.Kind == FrameKind.Closed)
        {
            return false;
        }

        if (_closeRequested)
        {
            // The handshake timer or a revocation closed the connection while the frame was on its way.
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return false;
        }

        switch (frame.Kind)
        {
            case FrameKind.TooLarge:
                Violation("the first frame is too large", CloseCodes.MessageTooBig, "Frame too large.");
                await ReceiveUntilClosedAsync().ConfigureAwait(false);
                return false;
            case FrameKind.Binary:
                Violation("the first frame is binary", CloseCodes.BadRequest, "A client_hello text frame is expected.");
                await ReceiveUntilClosedAsync().ConfigureAwait(false);
                return false;
        }

        LinkMessage message;
        try
        {
            message = LinkCodec.Parse(frame.Data.Span);
        }
        catch (JsonException)
        {
            Violation("the first frame is not valid", CloseCodes.BadRequest, "A valid client_hello frame is expected.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return false;
        }

        if (message is not ClientHelloMessage hello)
        {
            Violation("the first frame is not client_hello", CloseCodes.BadRequest, "A client_hello frame is expected first.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return false;
        }

        if (hello.Protocol != ProtocolInfo.Version)
        {
            // A client_hello has no id, so the error travels in a result with id 0 (see protocol/README.md).
            Send(ResultMessage.Failure(0, ErrorCodes.UnsupportedVersion, $"The agent speaks protocol version {ProtocolInfo.Version}; the client asked for {hello.Protocol}."));
            RequestClose(CloseCodes.UnsupportedVersion, "Unsupported protocol version.");
            await ReceiveUntilClosedAsync().ConfigureAwait(false);
            return false;
        }

        _peerFeatures = hello.Features ?? [];
        return true;
    }

    private void OnHandshakeTimeout()
    {
        if (_handshakeReceived)
        {
            return;
        }

        LinkLog.ProtocolViolation(_logger, _client.Id, "no client_hello in time");
        RequestClose(CloseCodes.BadRequest, "client_hello expected.");
    }

    // ------------------------------------------------------------------
    // Receiving
    // ------------------------------------------------------------------

    private async Task ReceiveLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Frame frame = await ReadFrameAsync(_lifetime.Token).ConfigureAwait(false);
            if (frame.Kind == FrameKind.Closed)
            {
                return;
            }

            if (_closeRequested)
            {
                // The close handshake is under way; wait for the peer's close frame and ignore the rest.
                continue;
            }

            switch (frame.Kind)
            {
                case FrameKind.TooLarge:
                    Violation("frame larger than the limit", CloseCodes.MessageTooBig, "Frame too large.");
                    break;
                case FrameKind.Binary:
                    LinkLog.Ignored(_logger, _client.Id, "binary frame");
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
            Frame frame = await ReadFrameAsync(_lifetime.Token).ConfigureAwait(false);
            if (frame.Kind == FrameKind.Closed)
            {
                return;
            }
        }
    }

    private async Task<Frame> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var message = new ArrayBufferWriter<byte>();
        bool binary = false;
        while (true)
        {
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(_receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (_socket.CloseStatus is { } closeStatus)
                {
                    Volatile.Write(ref _peerCloseStatus, (int)closeStatus);
                }

                return new Frame(FrameKind.Closed, ReadOnlyMemory<byte>.Empty);
            }

            if ((long)message.WrittenCount + result.Count > _options.MaxFrameBytes)
            {
                return new Frame(FrameKind.TooLarge, ReadOnlyMemory<byte>.Empty);
            }

            message.Write(_receiveBuffer.AsSpan(0, result.Count));
            binary = result.MessageType == WebSocketMessageType.Binary;
            if (result.EndOfMessage)
            {
                return new Frame(binary ? FrameKind.Binary : FrameKind.Text, message.WrittenMemory.ToArray());
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
            HandleMalformed(data);
            return;
        }

        switch (message)
        {
            case GetManifestMessage getManifest:
                Send(ResultMessage.Success(getManifest.Id, LinkCodec.ToElement(_server.Manifest.Current)));
                break;
            case SubscribeMessage subscribe:
                await HandleSubscribeAsync(subscribe).ConfigureAwait(false);
                break;
            case RunMessage run:
                StartRun(run);
                break;
            default:
                // The hub link claims its own message types (credential_rotate, unenroll, ...) first (T905).
                if (message is UnknownMessage unknown
                    && _extension is not null
                    && await _extension.TryHandleAsync(unknown, this, _lifetime.Token).ConfigureAwait(false))
                {
                    break;
                }

                // client_hello again, answers to requests that the agent never sends, frames that only the agent
                // sends and message types of a later protocol version are ignored (PLAN.md S07).
                string ignoredType = message.T;
                LinkLog.Ignored(_logger, _client.Id, ignoredType);
                break;
        }
    }

    private void HandleMalformed(ReadOnlyMemory<byte> data)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("t", out JsonElement type)
                && type.ValueKind == JsonValueKind.String)
            {
                string? name = type.GetString();
                if (root.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.Number
                    && id.TryGetInt64(out long requestId)
                    && requestId >= 0
                    && name is MessageTypes.GetManifest or MessageTypes.Subscribe or MessageTypes.Run)
                {
                    Send(ResultMessage.Failure(requestId, ErrorCodes.BadRequest, "The request is malformed."));
                    return;
                }

                // A known frame without a usable id cannot be answered; it is dropped like an unknown one.
                LinkLog.Ignored(_logger, _client.Id, "invalid frame");
                return;
            }
        }
        catch (JsonException)
        {
            // Not JSON at all: handled below.
        }

        Violation("the frame is not a QoffeeLink message", CloseCodes.BadRequest, "Malformed frame.");
    }

    private void Violation(string reason, int closeCode, string closeReason)
    {
        LinkLog.ProtocolViolation(_logger, _client.Id, reason);
        RequestClose(closeCode, closeReason);
    }

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    private async Task HandleSubscribeAsync(SubscribeMessage message)
    {
        await _feedGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            // The old feed ends first and the result is queued before the new feed starts, so the client sees
            // the result and then the full snapshot, and nothing of the old subscription after the result.
            await StopMetricFeedCoreAsync().ConfigureAwait(false);
            Send(ResultMessage.Success(message.Id, LinkValues.EmptyObject));
            if (message.Metrics.All || message.Metrics.Ids.Count > 0)
            {
                StartMetricFeedCore(message.Metrics, message.Intervals);
            }
        }
        finally
        {
            _feedGate.Release();
        }
    }

    private void StartMetricFeedCore(MetricSelection selection, IReadOnlyDictionary<string, double>? intervals)
    {
        MetricSubscription subscription = _server.Metrics.Subscribe(selection, intervals);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _metricSubscription = subscription;
        _metricPumpStop = stop;
        _metricPump = Task.Run(() => PumpMetricsAsync(subscription, stop.Token), CancellationToken.None);
    }

    private async Task StopMetricFeedCoreAsync()
    {
        MetricSubscription? subscription = _metricSubscription;
        CancellationTokenSource? stop = _metricPumpStop;
        Task? pump = _metricPump;
        _metricSubscription = null;
        _metricPumpStop = null;
        _metricPump = null;
        if (subscription is null)
        {
            return;
        }

        try
        {
            stop!.Cancel();
            subscription.Dispose();
            await pump!.ConfigureAwait(false);
        }
        finally
        {
            stop!.Dispose();
        }
    }

    private async Task PumpMetricsAsync(MetricSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (MetricUpdate update in subscription.Updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (update.Values.Count == 0)
                {
                    continue;
                }

                var values = new Dictionary<string, JsonElement>(update.Values.Count, StringComparer.Ordinal);
                foreach ((string id, object? value) in update.Values)
                {
                    values[id] = LinkValues.ToElement(value);
                }

                Send(new StateMessage(update.Timestamp.ToUnixTimeMilliseconds(), values));
            }
        }
        catch (OperationCanceledException)
        {
            // The feed was replaced or the connection ended.
        }
    }

    private void StartEventFeed()
    {
        EventSubscription subscription = _server.Events.Subscribe(EventTypeNames);
        _eventSubscription = subscription;
        _eventPump = Task.Run(() => PumpEventsAsync(subscription, _lifetime.Token), CancellationToken.None);
    }

    private async Task PumpEventsAsync(EventSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AgentEvent agentEvent in subscription.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Send(agentEvent.ToMessage());
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended.
        }
    }

    private void StartRun(RunMessage message)
    {
        lock (_gate)
        {
            if (_runs.Count >= _options.MaxPendingRequests)
            {
                Send(ResultMessage.Failure(message.Id, ErrorCodes.Busy, "Too many requests are in progress on this connection."));
                return;
            }

            Task task = Task.Run(() => RunCommandAsync(message), CancellationToken.None);
            _runs.Add(task);
            _ = task.ContinueWith(
                static (finished, state) => ((LinkConnection)state!).ForgetRun(finished),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ForgetRun(Task task)
    {
        lock (_gate)
        {
            _runs.Remove(task);
        }
    }

    private async Task RunCommandAsync(RunMessage message)
    {
        CancellationToken cancellationToken = _lifetime.Token;
        try
        {
            var request = new RunRequest(
                message.Command,
                message.Vars,
                SourceOf(_client.Kind),
                _client.Id,
                message.Context?.UserId);
            RunHandle handle = await _server.Commands.StartAsync(request, cancellationToken).ConfigureAwait(false);

            if (handle.IsAccepted && !message.ShouldWait)
            {
                Send(ResultMessage.Success(message.Id, LinkCodec.ToElement(new RunResultData(handle.RunId))));
                return;
            }

            // The wait ends with the connection; the run itself goes on and ends up in the history.
            RunResult result = await handle.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            Send(ToResult(message.Id, result));
        }
        catch (OperationCanceledException)
        {
            // The connection ended while the run was in progress.
        }
        catch (ObjectDisposedException)
        {
            // The agent is stopping.
        }
#pragma warning disable CA1031 // A failing request is answered with an error; it must not end the connection.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            string failure = exception.GetType().Name;
            LinkLog.Fault(_logger, _client.Id, failure);
            Send(ResultMessage.Failure(message.Id, ErrorCodes.Internal, "The command could not be run."));
        }
    }

    private static string SourceOf(string clientKind)
    {
        // The link has three kinds of clients; a tool is a script or command-line client of the owner.
        return clientKind switch
        {
            "homeassistant" => RunSources.HomeAssistant,
            "hub" => RunSources.Hub,
            _ => RunSources.Cli,
        };
    }

    /// <summary>
    /// Maps the outcome of a run to the result frame. A rejected run and a run that could not complete (no
    /// session, no elevation, cancelled, internal error) are errors; a run that ended, with any exit code or by
    /// its timeout, is a success whose data says how it ended (PLAN.md S07).
    /// </summary>
    internal static ResultMessage ToResult(long id, RunResult result)
    {
        if (result.ErrorCode is { } code && !result.TimedOut)
        {
            return ResultMessage.Failure(id, code, result.ErrorMessage ?? "The command did not run.");
        }

        return ResultMessage.Success(id, LinkCodec.ToElement(result.ToLinkData()));
    }

    // ------------------------------------------------------------------
    // Manifest
    // ------------------------------------------------------------------

    private void OnManifestChanged(object? sender, ManifestChangedEventArgs e)
    {
        AnnounceManifest();
    }

    private void AnnounceManifest()
    {
        if (!_handshakeDone)
        {
            return;
        }

        string rev = _server.Manifest.Current.Rev;
        lock (_gate)
        {
            if (string.Equals(rev, _announcedRev, StringComparison.Ordinal))
            {
                return;
            }

            _announcedRev = rev;
            Send(new ManifestChangedMessage(rev));
        }
    }

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    /// <inheritdoc />
    bool ILinkExtensionContext.Send(LinkMessage message) => Send(message);

    /// <inheritdoc />
    void ILinkExtensionContext.Close(int closeCode, string reason) => RequestClose(closeCode, reason);

    private bool Send(LinkMessage message)
    {
        byte[] payload = LinkCodec.SerializeToUtf8(message);
        if (payload.Length > _options.MaxFrameBytes)
        {
            LinkLog.FrameTooLarge(_logger, message.T, _client.Id);
            if (message is not ResultMessage { Ok: true } result)
            {
                return false;
            }

            payload = LinkCodec.SerializeToUtf8(
                ResultMessage.Failure(result.Id, ErrorCodes.Internal, "The response is larger than the maximum frame size."));
        }

        return Enqueue(payload);
    }

    private bool Enqueue(byte[] payload)
    {
        if (_closeRequested)
        {
            return false;
        }

        long queued = Interlocked.Add(ref _queuedBytes, payload.Length);
        bool tooMuch = queued > _options.SendQueueMaxBytes && queued > payload.Length;
        if (tooMuch || !_outbound.Writer.TryWrite(new OutboundFrame(payload)))
        {
            Interlocked.Add(ref _queuedBytes, -payload.Length);
            if (!_closeRequested)
            {
                LinkLog.SlowClient(_logger, _client.Id);
                Abort();
            }

            return false;
        }

        return true;
    }

    private void RequestClose(int code, string reason)
    {
        lock (_gate)
        {
            if (_closeRequested)
            {
                return;
            }

            _closeRequested = true;
            _closeCode = code;
            _closeReason = reason;
        }

        _outbound.Writer.TryComplete();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (OutboundFrame frame in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                Interlocked.Add(ref _queuedBytes, -frame.Payload.Length);
                using var timeout = new CancellationTokenSource(_options.SendTimeout, _time);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, timeout.Token);
                try
                {
                    await _socket.SendAsync(frame.Payload, WebSocketMessageType.Text, endOfMessage: true, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    LinkLog.SlowClient(_logger, _client.Id);
                    Abort();
                    return;
                }
            }

            await CloseOutputAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            Abort();
        }
        finally
        {
            _flushed.TrySetResult();
        }
    }

    private async Task CloseOutputAsync()
    {
        if (Volatile.Read(ref _aborted) != 0 || _socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_options.SendTimeout, _time);
        await _socket.CloseOutputAsync((WebSocketCloseStatus)_closeCode, _closeReason, timeout.Token).ConfigureAwait(false);

        if (_socket.State != WebSocketState.Closed)
        {
            _watchdog = Task.Run(WatchCloseAsync, CancellationToken.None);
        }
    }

    private async Task WatchCloseAsync()
    {
        try
        {
            await Task.Delay(_options.CloseTimeout, _time, _lifetime.Token).ConfigureAwait(false);
            Abort();
        }
        catch (OperationCanceledException)
        {
            // The peer answered, or the connection ended otherwise.
        }
    }

    // ------------------------------------------------------------------
    // End
    // ------------------------------------------------------------------

    private async Task CleanupAsync()
    {
        try
        {
            // The peer closed or the loops ended: close our side after the frames that are still queued.
            RequestClose(CloseCodes.Normal, string.Empty);
            await _writer.ConfigureAwait(false);

            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _manifestHandlerAttached, 0) == 1)
            {
                _server.Manifest.Changed -= OnManifestChanged;
            }

            await _feedGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopMetricFeedCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _feedGate.Release();
            }

            if (_eventSubscription is not null)
            {
                _eventSubscription.Dispose();
                await _eventPump!.ConfigureAwait(false);
            }

            Task[] runs;
            lock (_gate)
            {
                runs = [.. _runs];
            }

            await Task.WhenAll(runs).ConfigureAwait(false);
            await _watchdog.ConfigureAwait(false);

            if (_registration is not null)
            {
                _registration.Dispose();
                LinkLog.Disconnected(_logger, _client.Id);
            }

            try
            {
                _socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }
        finally
        {
            _flushed.TrySetResult();
            _ended.TrySetResult();
        }
    }

    private readonly record struct OutboundFrame(byte[] Payload);

    /// <summary>The registration of a connection that the pairing service does not know (the agent dialled out).</summary>
    private sealed class NoRegistration : IDisposable
    {
        public static readonly NoRegistration Instance = new();

        public void Dispose()
        {
            // Nothing to unregister.
        }
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
