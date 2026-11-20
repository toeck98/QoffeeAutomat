using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Hub.Tunnel;

/// <summary>
/// The agent's side of the management tunnel (PLAN.md S20, T912): feature <c>mgmt_tunnel</c> of the hub link. A <c>mgmt_request</c> is checked and then run through the
/// agent's own pipeline (<see cref="TunnelDispatcher"/>) with a principal built from the hub user and role; the answer goes back as the result with the HTTP status and
/// body. A <c>mgmt_stream_open</c> opens a Server-Sent-Events endpoint the same way and forwards its events as <c>mgmt_stream_data</c> until it ends
/// (<c>mgmt_stream_close</c>); the hub can end a stream with the same message. The rules, in the order they are applied:
/// <list type="number">
/// <item>The local switch: while the tunnel is off every request fails with <c>forbidden</c> ("switched off"), and the feature is not announced.</item>
/// <item>The hub must have announced the feature in <c>client_hello</c>, the frame must be valid and the path must start with <c>/api/v1/</c> (<c>bad_request</c> otherwise).</item>
/// <item>The role rules (<see cref="TunnelAccess"/>): a refusal is an ok result with status 403, or for a stream the error <c>forbidden</c>.</item>
/// <item>The management API's own rules apply to the dispatched request: the lock of the local configuration, read-only hub documents, validation.</item>
/// </list>
/// Every request is written to the agent's log with the hub user, role, method, path and status (never the query or the body). Requests run in the background, so a slow
/// call does not hold up the hub link; their number and the number of streams are limited per connection.
/// </summary>
public sealed class ManagementTunnelService
{
    private const int MaxUserLength = 128;

    private readonly HubLinkManager _manager;
    private readonly TunnelDispatcher _dispatcher;
    private readonly TunnelOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ManagementTunnelService> _logger;
    private readonly object _gate = new();
    private readonly List<TunnelSession> _sessions = [];

    /// <summary>Creates the service.</summary>
    /// <param name="manager">Tells whether the local switch allows the tunnel.</param>
    /// <param name="dispatcher">Runs requests through the pipeline.</param>
    /// <param name="options">The limits.</param>
    /// <param name="time">The clock, for pacing.</param>
    /// <param name="logger">The logger.</param>
    public ManagementTunnelService(
        HubLinkManager manager,
        TunnelDispatcher dispatcher,
        TunnelOptions options,
        TimeProvider time,
        ILogger<ManagementTunnelService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _dispatcher = dispatcher;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The connection is established; remembers whether the hub announced the feature.</summary>
    /// <param name="peerFeatures">The features of <c>client_hello</c>.</param>
    /// <param name="context">The connection.</param>
    public void OnEstablished(IReadOnlyList<string> peerFeatures, ILinkExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(peerFeatures);
        ArgumentNullException.ThrowIfNull(context);

        var session = new TunnelSession(context, peerFeatures.Contains(HubFeatures.MgmtTunnel, StringComparer.Ordinal), _options, _time);
        lock (_gate)
        {
            _sessions.Add(session);
        }
    }

    /// <summary>The connection ended: running requests are aborted and every stream is closed.</summary>
    /// <param name="context">The connection.</param>
    public void OnClosed(ILinkExtensionContext context)
    {
        TunnelSession? session;
        lock (_gate)
        {
            session = _sessions.Find(candidate => ReferenceEquals(candidate.Context, context));
            _ = session is not null && _sessions.Remove(session);
        }

        session?.Abort();
    }

    /// <summary>Handles <c>mgmt_request</c>, <c>mgmt_stream_open</c> and <c>mgmt_stream_close</c>.</summary>
    /// <param name="message">The frame.</param>
    /// <param name="context">The connection.</param>
    /// <param name="cancellationToken">Fires when the connection ends (unused: the session aborts its own work).</param>
    /// <returns>True when the frame is a message of this feature; false for any other type.</returns>
    public ValueTask<bool> TryHandleAsync(UnknownMessage message, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        switch (message.Type)
        {
            case HubMessageTypes.MgmtRequest:
            case HubMessageTypes.MgmtStreamOpen:
                Accept(message, context);
                return ValueTask.FromResult(true);
            case HubMessageTypes.MgmtStreamClose:
                CloseByHub(message, context);
                return ValueTask.FromResult(true);
            default:
                return ValueTask.FromResult(false);
        }
    }

    private void Accept(UnknownMessage message, ILinkExtensionContext context)
    {
        if (!TryReadId(message.Raw, out long id))
        {
            return;
        }

        bool stream = message.Type == HubMessageTypes.MgmtStreamOpen;
        if (!_manager.ManagementTunnelEnabled)
        {
            context.Send(ResultMessage.Failure(id, HubErrorCodes.Forbidden, "The management tunnel is switched off on this agent."));
            return;
        }

        TunnelSession? session = SessionOf(context);
        if (session is null || !session.Negotiated)
        {
            context.Send(ResultMessage.Failure(id, ErrorCodes.BadRequest, "The feature mgmt_tunnel was not announced in client_hello."));
            return;
        }

        TunnelCall? call;
        try
        {
            call = stream ? ReadStreamOpen(message.Raw) : ReadRequest(message.Raw);
        }
        catch (JsonException)
        {
            call = null;
        }

        if (call is null)
        {
            context.Send(ResultMessage.Failure(id, ErrorCodes.BadRequest, "The " + message.Type + " request is malformed."));
            return;
        }

        if (!TunnelAccess.IsValidPath(call.Path, out string? pathError))
        {
            // Everything outside /api/v1/ ends here, whatever the hub's role is.
            HubLog.TunnelDenied(_logger, call.Method, TunnelPathForLog(call.Path), call.Identity.User, call.Identity.Role, pathError ?? "invalid path");
            context.Send(ResultMessage.Failure(id, ErrorCodes.BadRequest, pathError ?? "The path is not valid."));
            return;
        }

        TunnelDecision decision = TunnelAccess.Check(call.Identity.Role, call.Method, call.Path);
        if (!decision.Allowed)
        {
            HubLog.TunnelDenied(_logger, call.Method, call.Path, call.Identity.User, call.Identity.Role, decision.Message ?? "refused");
            context.Send(stream
                ? ResultMessage.Failure(id, decision.Code ?? HubErrorCodes.Forbidden, decision.Status.ToString(CultureInfo.InvariantCulture) + ": " + decision.Message)
                : ResultMessage.Success(id, ToElement(DenialOf(decision))));
            return;
        }

        if (stream)
        {
            if (!session.TryReserveStream())
            {
                context.Send(ResultMessage.Failure(id, ErrorCodes.Busy, "Too many event streams are open on this agent."));
                return;
            }

            session.Track(Task.Run(() => RunStreamAsync(id, call, session), CancellationToken.None));
            return;
        }

        if (!session.TryReserveRequest())
        {
            context.Send(ResultMessage.Failure(id, ErrorCodes.Busy, "Too many tunnelled requests are running on this agent."));
            return;
        }

        session.Track(Task.Run(() => RunRequestAsync(id, call, session), CancellationToken.None));
    }

    private async Task RunRequestAsync(long id, TunnelCall call, TunnelSession session)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            TunnelResult result = await _dispatcher.SendAsync(call, session.Lifetime.Token).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                HubLog.TunnelRequest(
                    _logger,
                    call.Identity.User,
                    call.Identity.Role,
                    call.Method,
                    call.Path,
                    result.Status,
                    elapsedMs);
            }
            _ = session.Context.Send(ResultMessage.Success(id, ToElement(new MgmtRequestResultData(result.Status, result.Body))));
        }
        catch (OperationCanceledException)
        {
            // The hub link ended; nobody waits for the answer.
        }
        finally
        {
            session.ReleaseRequest();
        }
    }

    private async Task RunStreamAsync(long id, TunnelCall call, TunnelSession session)
    {
        TunnelStreamOpening opening;
        try
        {
            opening = await _dispatcher.OpenStreamAsync(call, session.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.ReleaseStream();
            return;
        }

        if (opening.Stream is null)
        {
            session.ReleaseStream();
            HubLog.TunnelRequest(_logger, call.Identity.User, call.Identity.Role, call.Method, call.Path, opening.Status, 0);
            _ = session.Context.Send(ResultMessage.Failure(id, CodeOf(opening.Status), DescribeFailure(opening)));
            return;
        }

        string sid = session.NewStreamId();
        var state = new StreamState(CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token));
        session.Streams[sid] = state;
        HubLog.TunnelStreamOpened(_logger, call.Identity.User, call.Identity.Role, sid, call.Path);

        // The result first: the hub registers the stream when it sees it, before the first event arrives behind it in the same queue.
        _ = session.Context.Send(ResultMessage.Success(id, ToElement(new MgmtStreamOpenResultData(sid))));
        string reason = "ended";
        try
        {
            await using TunnelEventReader stream = opening.Stream;
            await foreach (SseEvent item in stream.ReadAsync(state.Cancellation.Token).ConfigureAwait(false))
            {
                var data = new MgmtStreamDataMessage(sid, item.Name, item.Data);
                if (!await SendPacedAsync(session, Frame(HubMessageTypes.MgmtStreamData, JsonSerializer.SerializeToElement(data, HubJsonContext.Default.MgmtStreamDataMessage)), state.Cancellation.Token).ConfigureAwait(false))
                {
                    reason = "hub too slow";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = state.ClosedByHub ? "closed by the hub" : "connection ended";
        }
        catch (InvalidDataException)
        {
            reason = "event too large";
        }
        finally
        {
            _ = session.Streams.TryRemove(sid, out _);
            session.ReleaseStream();
            bool sendClose = !state.ClosedByHub && !session.Lifetime.IsCancellationRequested;
            state.Dispose();
            HubLog.TunnelStreamClosed(_logger, sid, call.Path, call.Identity.User, reason);
            if (sendClose)
            {
                _ = session.Context.Send(Frame(HubMessageTypes.MgmtStreamClose, JsonSerializer.SerializeToElement(new MgmtStreamCloseMessage(sid), HubJsonContext.Default.MgmtStreamCloseMessage)));
            }
        }
    }

    /// <summary>
    /// Queues an event frame. This is where back-pressure meets the hub link: while the send queue of the connection is full the pump waits (and, behind it, the bounded
    /// pipe fills and the endpoint's writes wait). If there is no room within <see cref="TunnelOptions.StreamSendTimeout"/> the stream ends. The pump also keeps below the
    /// number of frames per second that the hub tolerates.
    /// </summary>
    private async Task<bool> SendPacedAsync(TunnelSession session, UnknownMessage frame, CancellationToken cancellationToken)
    {
        await session.PaceAsync(cancellationToken).ConfigureAwait(false);
        long deadline = _time.GetTimestamp() + (long)(_options.StreamSendTimeout.TotalSeconds * _time.TimestampFrequency);
        while (!session.Context.Send(frame))
        {
            if (_time.GetTimestamp() >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), _time, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private void CloseByHub(UnknownMessage message, ILinkExtensionContext context)
    {
        TunnelSession? session = SessionOf(context);
        if (session is null)
        {
            return;
        }

        MgmtStreamCloseMessage? close = null;
        try
        {
            close = message.Raw.Deserialize(HubJsonContext.Default.MgmtStreamCloseMessage);
        }
        catch (JsonException)
        {
            // A close without a usable stream id closes nothing.
        }

        if (close is not null && session.Streams.TryGetValue(close.Sid, out StreamState? state))
        {
            state.CloseByHub();
        }
    }

    private TunnelSession? SessionOf(ILinkExtensionContext context)
    {
        lock (_gate)
        {
            return _sessions.Find(candidate => ReferenceEquals(candidate.Context, context));
        }
    }

    private static TunnelCall? ReadRequest(JsonElement raw)
    {
        MgmtRequestMessage? request = raw.Deserialize(HubJsonContext.Default.MgmtRequestMessage);
        return request is null || !TryIdentity(request.Principal, out TunnelIdentity? identity)
            ? null
            : new TunnelCall(request.Method.ToUpperInvariant(), request.Path, request.Query, request.Body, identity!);
    }

    private static TunnelCall? ReadStreamOpen(JsonElement raw)
    {
        MgmtStreamOpenMessage? open = raw.Deserialize(HubJsonContext.Default.MgmtStreamOpenMessage);
        return open is null || !TryIdentity(open.Principal, out TunnelIdentity? identity)
            ? null
            : new TunnelCall("GET", open.Path, open.Query, null, identity!);
    }

    private static bool TryIdentity(TunnelPrincipal? principal, out TunnelIdentity? identity)
    {
        identity = null;
        if (principal is null || !TunnelAccess.IsRole(principal.Role) || string.IsNullOrWhiteSpace(principal.User))
        {
            return false;
        }

        identity = new TunnelIdentity(SanitizeUser(principal.User), principal.Role);
        return true;
    }

    /// <summary>The user name comes from the hub and ends up in logs, so control characters are removed and the length is limited.</summary>
    private static string SanitizeUser(string user)
    {
        var text = new StringBuilder();
        foreach (char c in user)
        {
            if (text.Length >= MaxUserLength)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                _ = text.Append(c);
            }
        }

        return text.Length == 0 ? "unknown" : text.ToString();
    }

    private static string TunnelPathForLog(string? path)
    {
        // A path that failed validation may hold anything; only its beginning is logged, without control characters.
        var text = new StringBuilder();
        foreach (char c in path ?? string.Empty)
        {
            if (text.Length >= 64)
            {
                break;
            }

            _ = text.Append(char.IsControl(c) ? '?' : c);
        }

        return text.ToString();
    }

    private static TunnelResult DenialOf(TunnelDecision decision)
    {
        return TunnelDispatcher.Problem(decision.Status, decision.Code ?? HubErrorCodes.Forbidden, decision.Message ?? "The request is not allowed.");
    }

    private static string CodeOf(int status)
    {
        return status switch
        {
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => HubErrorCodes.Forbidden,
            StatusCodes.Status408RequestTimeout or StatusCodes.Status504GatewayTimeout => ErrorCodes.Timeout,
            StatusCodes.Status429TooManyRequests => ErrorCodes.Busy,
            >= 400 and < 500 => ErrorCodes.BadRequest,
            _ => ErrorCodes.Internal,
        };
    }

    /// <summary>
    /// The message of a stream that did not open starts with the HTTP status of the endpoint's answer and a colon (<c>404: There is no run with this id.</c>), because the
    /// error of a result has only a code and a text; the hub restores the status from it.
    /// </summary>
    private static string DescribeFailure(TunnelStreamOpening opening)
    {
        string detail = "The endpoint did not open an event stream.";
        if (opening.Problem is { ValueKind: JsonValueKind.Object } problem
            && problem.TryGetProperty("detail", out JsonElement text)
            && text.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(text.GetString()))
        {
            detail = text.GetString()!;
        }

        return opening.Status.ToString(CultureInfo.InvariantCulture) + ": " + detail;
    }

    private static UnknownMessage Frame(string type, JsonElement raw) => new(type, raw);

    private static JsonElement ToElement(TunnelResult denial) => ToElement(new MgmtRequestResultData(denial.Status, denial.Body));

    private static JsonElement ToElement(MgmtRequestResultData data) => JsonSerializer.SerializeToElement(data, HubJsonContext.Default.MgmtRequestResultData);

    private static JsonElement ToElement(MgmtStreamOpenResultData data) => JsonSerializer.SerializeToElement(data, HubJsonContext.Default.MgmtStreamOpenResultData);

    private static bool TryReadId(JsonElement raw, out long id)
    {
        id = 0;
        return raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("id", out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out id)
            && id >= 0;
    }

    /// <summary>The state of one open stream.</summary>
    private sealed class StreamState(CancellationTokenSource cancellation) : IDisposable
    {
        private int _closedByHub;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool ClosedByHub => Volatile.Read(ref _closedByHub) != 0;

        public void CloseByHub()
        {
            _ = Interlocked.Exchange(ref _closedByHub, 1);
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The stream ended by itself a moment ago.
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }

    /// <summary>What the service keeps for one hub connection.</summary>
    private sealed class TunnelSession
    {
        private readonly TunnelOptions _options;
        private readonly TimeProvider _time;
        private readonly object _paceGate = new();
        private readonly ConcurrentDictionary<Task, byte> _tasks = new();
        private long _windowStart;
        private int _windowCount;
        private long _lastStreamId;
        private int _requests;
        private int _streams;

        public TunnelSession(ILinkExtensionContext context, bool negotiated, TunnelOptions options, TimeProvider time)
        {
            Context = context;
            Negotiated = negotiated;
            _options = options;
            _time = time;
            _windowStart = time.GetTimestamp();
        }

        public ILinkExtensionContext Context { get; }

        public bool Negotiated { get; }

        public CancellationTokenSource Lifetime { get; } = new();

        public ConcurrentDictionary<string, StreamState> Streams { get; } = new(StringComparer.Ordinal);

        public bool TryReserveRequest() => TryReserve(ref _requests, _options.MaxConcurrentRequests);

        public void ReleaseRequest() => _ = Interlocked.Decrement(ref _requests);

        public bool TryReserveStream() => TryReserve(ref _streams, _options.MaxStreams);

        public void ReleaseStream() => _ = Interlocked.Decrement(ref _streams);

        public string NewStreamId() => "s" + Interlocked.Increment(ref _lastStreamId).ToString(CultureInfo.InvariantCulture);

        /// <summary>Remembers a background task so that it is not collected, and forgets it when it ends.</summary>
        public void Track(Task task)
        {
            _ = _tasks.TryAdd(task, 0);
            _ = task.ContinueWith(
                static (finished, state) => ((TunnelSession)state!)._tasks.TryRemove(finished, out _),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>Aborts the running requests and streams; their tasks end on their own.</summary>
        public void Abort()
        {
            try
            {
                Lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already aborted.
            }
        }

        /// <summary>Waits when more than <see cref="TunnelOptions.MaxEventsPerSecond"/> frames were sent in the current second.</summary>
        public async Task PaceAsync(CancellationToken cancellationToken)
        {
            TimeSpan wait = TimeSpan.Zero;
            lock (_paceGate)
            {
                long now = _time.GetTimestamp();
                if (now - _windowStart >= _time.TimestampFrequency)
                {
                    _windowStart = now;
                    _windowCount = 0;
                }

                if (++_windowCount > _options.MaxEventsPerSecond)
                {
                    wait = _time.GetElapsedTime(now, _windowStart + _time.TimestampFrequency);
                    _windowStart += _time.TimestampFrequency;
                    _windowCount = 1;
                }
            }

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool TryReserve(ref int counter, int limit)
        {
            if (Interlocked.Increment(ref counter) <= limit)
            {
                return true;
            }

            _ = Interlocked.Decrement(ref counter);
            return false;
        }
    }
}
