using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>Who the helper is, for the <c>hello</c> it sends.</summary>
/// <param name="UserName">The user the helper runs as.</param>
/// <param name="Version">The product version.</param>
public sealed record SessionHelperIdentity(string UserName, string Version);

/// <summary>Facts about the user that runs the helper.</summary>
public static class UnixUser
{
    /// <summary>Reads the effective user id of this process from <c>/proc/self/status</c>.</summary>
    /// <returns>The user id; null where <c>/proc</c> does not tell (not Linux).</returns>
    public static uint? EffectiveUserId()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                // Uid: real effective saved filesystem
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 3 && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint effective) ? effective : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}

/// <summary>
/// The session helper's end of the Unix domain socket (PLAN.md S11, S17, T803): it listens on the socket in the runtime directory of its
/// user, accepts the agent, and serves it with the session protocol of the Windows SessionHost: <c>hello</c> in both directions, then
/// <c>run</c> (processes, bash scripts and built-in actions of the desktop, in the user's context), <c>cancel</c>, <c>metrics_subscribe</c> with
/// <c>metrics_state</c> in return, <c>notify</c> and <c>shutdown</c>. The socket is created in a directory with mode 0700 and gets mode 0600,
/// and only a peer whose kernel-reported user is root (the agent) or the helper's own user is served; everybody else is dropped before a
/// byte is read. A new connection of the agent replaces the previous one. The helper keeps listening when the agent says goodbye, so the
/// agent can come back.
/// </summary>
public sealed partial class SessionHelperServer : IAsyncDisposable
{
    /// <summary>How long the agent may take to answer the <c>hello</c> of the helper.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the helper waits for the runs of a connection to end after the connection closed.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan SampleTick = TimeSpan.FromSeconds(1);

    private readonly string _socketPath;
    private readonly LinuxUserCommandExecutor _executor;
    private readonly IDesktopMetricSource _metrics;
    private readonly Func<string, string, CancellationToken, ValueTask> _notify;
    private readonly SessionHelperIdentity _identity;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IPeerCredentialReader _peers;
    private readonly HashSet<uint> _allowedPeers;
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private CancellationTokenSource? _currentConnection;
    private Task? _currentTask;

    /// <summary>Creates the server; call <see cref="RunAsync"/> to bind and serve.</summary>
    /// <param name="socketPath">The path of the socket; its directory is created with mode 0700.</param>
    /// <param name="executor">Executes the runs of the agent.</param>
    /// <param name="metrics">Samples the desktop metrics.</param>
    /// <param name="notify">Shows a notification (title, text) for a <c>notify</c> message.</param>
    /// <param name="identity">The user and the version for the <c>hello</c>.</param>
    /// <param name="logger">Receives the messages.</param>
    /// <param name="time">The clock; null uses the system clock.</param>
    /// <param name="peers">Reads the user of a connected peer; null uses <c>SO_PEERCRED</c>.</param>
    /// <param name="allowedPeers">
    /// The user ids that may connect; null allows root and the user of the helper. A peer whose user cannot be read (not Linux) is served
    /// only when this set is null.
    /// </param>
    public SessionHelperServer(
        string socketPath,
        LinuxUserCommandExecutor executor,
        IDesktopMetricSource metrics,
        Func<string, string, CancellationToken, ValueTask> notify,
        SessionHelperIdentity identity,
        ILogger logger,
        TimeProvider? time = null,
        IPeerCredentialReader? peers = null,
        IReadOnlyCollection<uint>? allowedPeers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(notify);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        if (socketPath.Length > SessionSocketPaths.MaxPathLength)
        {
            throw new ArgumentException($"The socket path is longer than {SessionSocketPaths.MaxPathLength} characters.", nameof(socketPath));
        }

        _socketPath = socketPath;
        _executor = executor;
        _metrics = metrics;
        _notify = notify;
        _identity = identity;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _peers = peers ?? new LinuxPeerCredentialReader();
        _allowedPeers = DefaultPeers();
        if (allowedPeers is not null)
        {
            _allowedPeers.Clear();
            foreach (uint peer in allowedPeers)
            {
                _allowedPeers.Add(peer);
            }
        }

        UsesDefaultPeers = allowedPeers is null;
    }

    /// <summary>The path of the socket.</summary>
    public string SocketPath => _socketPath;

    private bool UsesDefaultPeers { get; }

    /// <summary>
    /// Binds the socket and serves until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Stops the server.</param>
    /// <returns><see cref="SessionHostExitCodes.Ok"/> after a stop; <see cref="SessionHostExitCodes.AlreadyRunning"/> when another helper of this user listens on the socket.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        CancellationToken token = linked.Token;

        if (!await TryBindAsync(token).ConfigureAwait(false))
        {
            return SessionHostExitCodes.AlreadyRunning;
        }

        Listening(_logger, _socketPath);
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket client = await _listener!.AcceptAsync(token).ConfigureAwait(false);
                await AcceptAsync(client, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
            {
                ListenerFailed(_logger, exception.Message);
                return SessionHostExitCodes.Failure;
            }
        }
        finally
        {
            await EndCurrentAsync().ConfigureAwait(false);
            CloseListener();
        }

        return SessionHostExitCodes.Ok;
    }

    /// <summary>Stops the server and removes the socket.</summary>
    /// <returns>A task that completes when the server has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        await EndCurrentAsync().ConfigureAwait(false);
        CloseListener();
        _stop.Dispose();
    }

    [LoggerMessage(EventId = 3340, Level = LogLevel.Information, Message = "The session helper listens on {Path}.")]
    private static partial void Listening(ILogger logger, string path);

    [LoggerMessage(EventId = 3341, Level = LogLevel.Warning, Message = "The listener of the session helper failed: {Reason}")]
    private static partial void ListenerFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 3342, Level = LogLevel.Warning, Message = "A connection to the session socket was dropped: {Reason}")]
    private static partial void ConnectionDropped(ILogger logger, string reason);

    [LoggerMessage(EventId = 3343, Level = LogLevel.Information, Message = "The agent is connected (process {ProcessId}).")]
    private static partial void AgentConnected(ILogger logger, int processId);

    [LoggerMessage(EventId = 3344, Level = LogLevel.Information, Message = "The agent is gone.")]
    private static partial void AgentGone(ILogger logger);

    private static HashSet<uint> DefaultPeers()
    {
        var allowed = new HashSet<uint> { 0 };
        if (UnixUser.EffectiveUserId() is { } own)
        {
            allowed.Add(own);
        }

        return allowed;
    }

    private static JsonElement ToElement(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            switch (value)
            {
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case double number when double.IsFinite(number):
                    writer.WriteNumberValue(number);
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task SendAsync(Stream stream, SemaphoreSlim gate, SessionMessage message, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SessionFrameCodec.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Creates the directory and the socket with the modes that make the socket private, or finds another helper on it.</summary>
    private async Task<bool> TryBindAsync(CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_socketPath)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(directory, Private);

            // An existing directory keeps the mode it had; the socket must not be reachable through it for anybody else.
            File.SetUnixFileMode(directory, Private);
        }

        if (File.Exists(_socketPath))
        {
            if (await IsAnotherHelperAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            File.Delete(_socketPath);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            listener.Listen(1);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        _listener = listener;
        return true;
    }

    private async Task<bool> IsAnotherHelperAsync(CancellationToken cancellationToken)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Nobody answers: the file is left from a helper that died.
            return false;
        }
    }

    private void CloseListener()
    {
        Socket? listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null)
        {
            return;
        }

        listener.Dispose();
        try
        {
            File.Delete(_socketPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover socket file is replaced by the next start.
        }
    }

    /// <summary>Checks the peer, then lets the new connection replace the old one.</summary>
    private async Task AcceptAsync(Socket client, CancellationToken cancellationToken)
    {
        PeerCredentials? peer = _peers.Read(client);
        bool allowed = peer is null ? UsesDefaultPeers : _allowedPeers.Contains(peer.UserId);
        if (!allowed)
        {
            ConnectionDropped(_logger, peer is null ? "the user of the peer is unknown" : $"user {peer.UserId} is not the agent or the owner of the helper");
            client.Dispose();
            return;
        }

        await EndCurrentAsync().ConfigureAwait(false);

        var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentConnection = connection;
        _currentTask = Task.Run(() => ServeAsync(client, connection), CancellationToken.None);
    }

    private async Task EndCurrentAsync()
    {
        CancellationTokenSource? connection = Interlocked.Exchange(ref _currentConnection, null);
        Task? task = _currentTask;
        _currentTask = null;
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.CancelAsync().ConfigureAwait(false);
            if (task is not null)
            {
                await task.WaitAsync(DrainTimeout, _time).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection did not end in time; it is abandoned (its socket is closed by the task when it ends).
        }
        finally
        {
            connection.Dispose();
        }
    }

    private async Task ServeAsync(Socket client, CancellationTokenSource connection)
    {
        CancellationToken token = connection.Token;
        using var gate = new SemaphoreSlim(1, 1);
        await using var stream = new NetworkStream(client, ownsSocket: true);
        var runs = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var tasks = new ConcurrentDictionary<Task, byte>();
        var subscription = new Subscription();
        Task? sampler = null;
        try
        {
            if (!await HandshakeAsync(stream, gate, token).ConfigureAwait(false))
            {
                return;
            }

            sampler = Task.Run(() => SampleLoopAsync(stream, gate, subscription, token), CancellationToken.None);
            while (!token.IsCancellationRequested)
            {
                SessionMessage? message = await SessionFrameCodec.ReadAsync(stream, token).ConfigureAwait(false);
                if (message is null || message is SessionShutdownMessage)
                {
                    break;
                }

                switch (message)
                {
                    case SessionRunMessage run:
                        Task running = Task.Run(() => RunAsync(run, stream, gate, runs, token), CancellationToken.None);
                        tasks[running] = 0;
                        _ = running.ContinueWith(finished => tasks.TryRemove(finished, out _), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                        break;
                    case SessionCancelMessage cancel:
                        if (runs.TryGetValue(cancel.RunId, out CancellationTokenSource? target))
                        {
                            await CancelQuietlyAsync(target).ConfigureAwait(false);
                        }

                        break;
                    case SessionMetricsSubscribeMessage subscribe:
                        subscription.Set(subscribe.MetricIds);
                        break;
                    case SessionNotifyMessage notify:
                        _ = ShowAsync(notify, token);
                        break;
                    default:
                        // The tray, hotkeys and login token messages belong to the Windows SessionHost; nothing here answers them.
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server stops or another connection replaces this one.
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SessionProtocolException or SocketException)
        {
            ConnectionDropped(_logger, exception.Message);
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            foreach (CancellationTokenSource run in runs.Values)
            {
                await CancelQuietlyAsync(run).ConfigureAwait(false);
            }

            try
            {
                var pending = new List<Task>(tasks.Keys);
                if (sampler is not null)
                {
                    pending.Add(sampler);
                }

                await Task.WhenAll(pending).WaitAsync(DrainTimeout, _time).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The runs did not end in time; the helper goes on without them.
            }

            AgentGone(_logger);
        }
    }

    private static async Task CancelQuietlyAsync(CancellationTokenSource source)
    {
        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The run ended in the meantime.
        }
    }

    /// <summary>The helper speaks first; the agent answers, and both agree on the protocol version.</summary>
    private async Task<bool> HandshakeAsync(Stream stream, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await SendAsync(
            stream,
            gate,
            new SessionHelloMessage(
                SessionIpc.ProtocolVersion,
                SessionRoles.SessionHost,
                Environment.ProcessId,
                0,
                _identity.Version,
                _identity.UserName,
                IsAdministrator: false),
            cancellationToken).ConfigureAwait(false);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(HandshakeTimeout);
        SessionMessage? answer;
        try
        {
            answer = await SessionFrameCodec.ReadAsync(stream, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ConnectionDropped(_logger, "the peer did not answer the hello in time");
            return false;
        }

        if (answer is not SessionHelloMessage { Role: SessionRoles.Agent } hello || hello.ProtocolVersion != SessionIpc.ProtocolVersion)
        {
            ConnectionDropped(_logger, "the peer is not an agent that speaks this protocol version");
            await SendAsync(stream, gate, new SessionShutdownMessage(SessionShutdownReasons.Rejected), cancellationToken).ConfigureAwait(false);
            return false;
        }

        AgentConnected(_logger, hello.ProcessId);
        return true;
    }

    private async Task RunAsync(
        SessionRunMessage run,
        Stream stream,
        SemaphoreSlim gate,
        ConcurrentDictionary<string, CancellationTokenSource> runs,
        CancellationToken connectionToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        runs[run.RunId] = cancellation;
        try
        {
            Func<ProcessOutputChunk, ValueTask>? output = run.CaptureOutput
                ? chunk => new ValueTask(SendAsync(
                    stream,
                    gate,
                    new SessionRunOutputMessage(
                        run.RunId,
                        chunk.Source == ProcessOutputSource.StandardError ? SessionOutputStreams.StandardError : SessionOutputStreams.StandardOutput,
                        chunk.Text,
                        chunk.Timestamp.ToUnixTimeMilliseconds()),
                    CancellationToken.None))
                : null;

            SessionRunResultMessage result = await _executor.ExecuteAsync(run, output, cancellation.Token).ConfigureAwait(false);
            await SendAsync(stream, gate, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SessionProtocolException or OperationCanceledException)
        {
            // The agent is gone; nobody waits for the result.
        }
        finally
        {
            runs.TryRemove(run.RunId, out _);
        }
    }

    private async Task ShowAsync(SessionNotifyMessage message, CancellationToken cancellationToken)
    {
#pragma warning disable CA1031 // A notification that cannot be shown must not end the connection.
        try
        {
            await _notify(message.Title, message.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to tell the agent: a notification is best effort.
        }
#pragma warning restore CA1031
    }

    /// <summary>Samples the subscribed metrics at their intervals and sends the values that changed (all of them after a new subscription).</summary>
    private async Task SampleLoopAsync(Stream stream, SemaphoreSlim gate, Subscription subscription, CancellationToken cancellationToken)
    {
        var last = new Dictionary<string, object?>(StringComparer.Ordinal);
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        int version = -1;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (IReadOnlyList<string> ids, int current) = subscription.Read();
                if (current != version)
                {
                    version = current;
                    last.Clear();
                    due.Clear();
                }

                DateTimeOffset now = _time.GetUtcNow();
                string[] ready = [.. ids.Where(id => LinuxSessionMetricCatalog.IsHelperMetric(id) && (!due.TryGetValue(id, out DateTimeOffset at) || at <= now))];
                if (ready.Length > 0)
                {
                    IReadOnlyDictionary<string, object?> sampled = await _metrics.SampleAsync(ready, cancellationToken).ConfigureAwait(false);
                    var changed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (string id in ready)
                    {
                        object? value = sampled.GetValueOrDefault(id);
                        due[id] = now.AddSeconds(Math.Max(1, SessionMetricCatalog.IntervalSecondsOf(id)));
                        if (!last.TryGetValue(id, out object? before) || !Equals(before, value))
                        {
                            last[id] = value;
                            changed[id] = ToElement(value);
                        }
                    }

                    if (changed.Count > 0)
                    {
                        await SendAsync(stream, gate, new SessionMetricsStateMessage(now.ToUnixTimeMilliseconds(), changed), cancellationToken).ConfigureAwait(false);
                    }
                }

                await Task.Delay(SampleTick, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SessionProtocolException)
        {
            // The connection ended.
        }
    }

    /// <summary>The metrics that the agent wants, with a version that changes with every new list.</summary>
    private sealed class Subscription
    {
        private readonly object _gate = new();
        private IReadOnlyList<string> _ids = [];
        private int _version;

        public void Set(IReadOnlyList<string> ids)
        {
            lock (_gate)
            {
                _ids = [.. ids.Distinct(StringComparer.Ordinal)];
                _version++;
            }
        }

        public (IReadOnlyList<string> Ids, int Version) Read()
        {
            lock (_gate)
            {
                return (_ids, _version);
            }
        }
    }
}
