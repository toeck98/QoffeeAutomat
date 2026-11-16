using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>Settings of <see cref="SessionPipeServer"/>.</summary>
public sealed class SessionPipeServerOptions
{
    /// <summary>The pipe name; <see cref="SessionIpc.PipeName"/> in production, unique per test elsewhere.</summary>
    public string PipeName { get; init; } = SessionIpc.PipeName;

    /// <summary>The path of the installed SessionHost executable; a client whose image is another file is rejected (PLAN.md S09 item 7).</summary>
    public required string SessionHostPath { get; init; }

    /// <summary>The scripts directory that the SessionHost uses for <c>source: file</c> scripts of <c>runAs: user</c>.</summary>
    public string? ScriptsDirectory { get; init; }

    /// <summary>The product version that the agent announces in its <c>hello</c>.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>How long a client may take to send its <c>hello</c> after it connected.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Restricts the pipe to SYSTEM and interactive users and denies network clients. Always on in production; tests that
    /// run in a service or batch logon (no interactive token) switch it off.
    /// </summary>
    public bool ApplyPipeSecurity { get; init; } = true;

    /// <summary>How long a cancelled run waits for the session helper to report that the process tree is gone; null uses the default.</summary>
    public TimeSpan? CancelGrace { get; init; }
}

/// <summary>Raised when a SessionHost completed the handshake.</summary>
public sealed class SessionConnectionEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="connection">The new connection; the receiver owns it.</param>
    public SessionConnectionEventArgs(SessionConnection connection)
    {
        Connection = connection;
    }

    /// <summary>The new connection.</summary>
    public SessionConnection Connection { get; }
}

/// <summary>
/// The agent's end of the session pipe (PLAN.md D-009, S09 item 7, S11): listens on the pipe, identifies every client
/// through the operating system and accepts it only when its process image is the installed SessionHost, runs in an
/// interactive session (never session 0) and sends a <c>hello</c> of the right protocol version that names the same
/// session. After the agent answered with its own <c>hello</c> the handshake is complete and
/// <see cref="ConnectionEstablished"/> is raised. The pipe itself allows only SYSTEM and interactive users to connect.
/// </summary>
public sealed class SessionPipeServer : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly SessionPipeServerOptions _options;
    private readonly IPipePeerInspector _inspector;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;

    /// <summary>Creates the server; call <see cref="Start"/> to listen.</summary>
    /// <param name="options">The settings.</param>
    /// <param name="inspector">Identifies clients.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SessionPipeServer(SessionPipeServerOptions options, IPipePeerInspector inspector, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _inspector = inspector;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised on a thread pool thread after a SessionHost completed the handshake.</summary>
    public event EventHandler<SessionConnectionEventArgs>? ConnectionEstablished;

    /// <summary>Starts listening; calling it twice does nothing.</summary>
    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>Stops listening; connections that were established stay open (their owner closes them).</summary>
    /// <returns>A task that completes when the listener ended.</returns>
    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is { } loop)
        {
            await loop.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private static NamedPipeServerStream CreateSecuredPipe(string name, PipeOptions options)
    {
        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            SessionPipeSecurity.Create());
    }

    private NamedPipeServerStream CreatePipe(bool first)
    {
        // The first instance must be ours: another process that created the pipe earlier would receive the agent's traffic.
        PipeOptions options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (OperatingSystem.IsWindows() && _options.ApplyPipeSecurity)
        {
            return CreateSecuredPipe(_options.PipeName, options);
        }

        return new NamedPipeServerStream(
            _options.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options);
    }

    private async Task AcceptLoopAsync(CancellationToken stop)
    {
        bool first = true;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(first);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SessionLog.PipeUnavailable(_logger, exception.Message, (int)RetryDelay.TotalSeconds);
                try
                {
                    await Task.Delay(RetryDelay, _time, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            first = false;
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                // The client went away before it was accepted.
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = HandleAsync(pipe, stop);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        bool handedOver = false;
        try
        {
            PipePeer peer = _inspector.Inspect(pipe);
            string? rejection = CheckPeer(peer);
            if (rejection is not null)
            {
                SessionLog.PeerRejected(_logger, rejection);
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(_options.HandshakeTimeout);

            SessionMessage? first = await SessionFrameCodec.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (first is not SessionHelloMessage hello)
            {
                SessionLog.HandshakeFailed(_logger, "The first message was not hello.");
                return;
            }

            string? problem = CheckHello(hello, peer);
            if (problem is not null)
            {
                SessionLog.HandshakeFailed(_logger, problem);
                await SessionFrameCodec.WriteAsync(pipe, new SessionShutdownMessage(SessionShutdownReasons.Rejected), timeout.Token).ConfigureAwait(false);
                return;
            }

            var answer = new SessionHelloMessage(
                SessionIpc.ProtocolVersion,
                SessionRoles.Agent,
                Environment.ProcessId,
                SessionId: 0,
                _options.Version,
                ScriptsDirectory: _options.ScriptsDirectory);
            await SessionFrameCodec.WriteAsync(pipe, answer, timeout.Token).ConfigureAwait(false);

            var connection = new SessionConnection(pipe, hello, peer, _time, _logger, _options.CancelGrace);
            handedOver = true;
            connection.Start();
            ConnectionEstablished?.Invoke(this, new SessionConnectionEventArgs(connection));
        }
        catch (OperationCanceledException)
        {
            SessionLog.HandshakeFailed(_logger, "The client did not complete the handshake in time.");
        }
        catch (Exception exception) when (exception is IOException or SessionProtocolException or InvalidOperationException or ObjectDisposedException)
        {
            SessionLog.HandshakeFailed(_logger, exception.Message);
        }
        finally
        {
            if (!handedOver)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string? CheckPeer(PipePeer peer)
    {
        if (peer.SessionId == 0)
        {
            return $"process {peer.ProcessId} runs in the services session.";
        }

        if (!ImagePaths.AreSame(peer.ImagePath, _options.SessionHostPath))
        {
            return $"process {peer.ProcessId} is not the installed session helper (its image is '{peer.ImagePath ?? "unknown"}').";
        }

        return null;
    }

    private static string? CheckHello(SessionHelloMessage hello, PipePeer peer)
    {
        if (hello.Role != SessionRoles.SessionHost)
        {
            return $"The role '{hello.Role}' is not '{SessionRoles.SessionHost}'.";
        }

        if (hello.ProtocolVersion != SessionIpc.ProtocolVersion)
        {
            return $"The session helper speaks protocol {hello.ProtocolVersion}, this agent speaks {SessionIpc.ProtocolVersion}.";
        }

        if (hello.SessionId != peer.SessionId)
        {
            return $"The session helper claims session {hello.SessionId} but runs in session {peer.SessionId}.";
        }

        return null;
    }
}

/// <summary>The access rules of the session pipe: SYSTEM has full control, interactive users may connect, the network may not.</summary>
internal static class SessionPipeSecurity
{
    /// <summary>Creates the security descriptor.</summary>
    /// <returns>The pipe security.</returns>
    public static PipeSecurity Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        return security;
    }
}
