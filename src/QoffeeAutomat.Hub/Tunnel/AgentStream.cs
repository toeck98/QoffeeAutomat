using System.Threading.Channels;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Tunnel;

/// <summary>One Server-Sent-Event of a tunnelled stream.</summary>
/// <param name="Event">The event name.</param>
/// <param name="Data">The data field, lines joined with a line feed.</param>
public sealed record AgentStreamEvent(string Event, string Data);

/// <summary>Why a tunnelled stream ended, see <see cref="AgentStreamRelay.EndReason"/>.</summary>
public static class StreamEndReasons
{
    /// <summary>The agent closed the stream: the endpoint ended, for example because the run finished.</summary>
    public const string Completed = "completed";

    /// <summary>The consumer closed the stream (it was disposed).</summary>
    public const string Closed = "closed";

    /// <summary>The agent disconnected from the hub while the stream was open.</summary>
    public const string AgentOffline = "agent_offline";

    /// <summary>The consumer did not read the events in time; the hub closed the stream.</summary>
    public const string SlowConsumer = "slow_consumer";
}

/// <summary>
/// A Server-Sent-Events stream of an agent's management API that the hub opened through the tunnel (PLAN.md S20, T912): <see cref="Events"/> delivers what the agent sends in
/// <c>mgmt_stream_data</c> until the stream ends, and <see cref="EndReason"/> says why it did. The queue is bounded
/// (<see cref="Gateway.GatewayOptions.StreamQueueCapacity"/>); a consumer that does not read is cut off. Disposing the stream tells the agent to close it.
/// </summary>
public sealed class AgentStreamRelay : IAsyncDisposable
{
    private readonly Channel<AgentStreamEvent> _events;
    private readonly Action<AgentStreamRelay> _release;
    private int _ended;
    private int _released;
    private volatile string? _endReason;
    private volatile bool _agentKnows;

    internal AgentStreamRelay(string agentId, string sid, int capacity, Action<AgentStreamRelay> release)
    {
        AgentId = agentId;
        Sid = sid;
        _release = release;
        _events = Channel.CreateBounded<AgentStreamEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>The agent that serves the stream.</summary>
    public string AgentId { get; }

    /// <summary>The id of the stream, chosen by the agent.</summary>
    public string Sid { get; }

    /// <summary>The events, in order. The reader completes when the stream has ended.</summary>
    public ChannelReader<AgentStreamEvent> Events => _events.Reader;

    /// <summary>Why the stream ended (<see cref="StreamEndReasons"/>); null while it is open.</summary>
    public string? EndReason => _endReason;

    /// <summary>True when the stream ended for a reason other than the consumer closing it, so the agent knows (or is gone) and no close frame has to be sent.</summary>
    internal bool AgentKnowsItEnded => _agentKnows;

    /// <summary>Queues an event for the consumer, waiting up to <paramref name="wait"/> for room.</summary>
    /// <param name="item">The event.</param>
    /// <param name="wait">How long to wait for room.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>False when the queue stayed full for the whole time; true otherwise (also when the stream has ended and the event is dropped).</returns>
    internal async ValueTask<bool> PushAsync(AgentStreamEvent item, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (_events.Writer.TryWrite(item) || Volatile.Read(ref _ended) != 0)
        {
            return true;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(wait);
        try
        {
            await _events.Writer.WriteAsync(item, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return true;
        }
    }

    /// <summary>Ends the stream: the reader completes after the events that are queued. The first reason wins.</summary>
    /// <param name="reason">One of <see cref="StreamEndReasons"/>.</param>
    internal void Complete(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        // The agent ended it, is gone, or was told by the gateway already (a slow consumer); only a consumer that closes the stream owes the agent a close frame.
        _agentKnows = reason != StreamEndReasons.Closed;
        _endReason = reason;
        _ = _events.Writer.TryComplete();
    }

    /// <summary>Closes the stream and tells the agent, unless the agent closed it.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Complete(StreamEndReasons.Closed);
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release(this);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>The outcome of <see cref="IAgentStreamChannel.OpenStreamAsync"/>. A failure is a value, not an exception.</summary>
/// <param name="Stream">The open stream; null on failure.</param>
/// <param name="ErrorCode">The error code: an agent code, <see cref="HubErrorCodes.AgentOffline"/>, <c>timeout</c> or <c>busy</c>; null on success.</param>
/// <param name="ErrorMessage">What went wrong, for people.</param>
public sealed record AgentStreamOutcome(AgentStreamRelay? Stream, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>True when the stream is open.</summary>
    public bool Ok => Stream is not null;

    /// <summary>Creates a successful outcome.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The outcome.</returns>
    public static AgentStreamOutcome Success(AgentStreamRelay stream) => new(stream, null, null);

    /// <summary>Creates a failed outcome.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="message">What went wrong.</param>
    /// <returns>The outcome.</returns>
    public static AgentStreamOutcome Failure(string code, string message) => new(null, code, message);
}

/// <summary>Opens event streams of an agent's management API (T912); the gateway implements it, the tunnel proxy uses it.</summary>
public interface IAgentStreamChannel
{
    /// <summary>Opens a stream with <c>mgmt_stream_open</c> and waits for the agent's answer.</summary>
    /// <param name="agentId">The agent id.</param>
    /// <param name="connectionId">The connection the request is meant for (<see cref="Gateway.AgentPresence.ConnectionId"/>); 0 means any.</param>
    /// <param name="path">The path of the endpoint, starting with <c>/api/v1/</c>.</param>
    /// <param name="query">The decoded query parameters, or null.</param>
    /// <param name="principal">The hub user.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The outcome.</returns>
    Task<AgentStreamOutcome> OpenStreamAsync(
        string agentId,
        long connectionId,
        string path,
        IReadOnlyDictionary<string, string>? query,
        TunnelPrincipal principal,
        CancellationToken cancellationToken);
}
