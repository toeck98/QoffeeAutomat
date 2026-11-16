using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// The agent's half of the tray icon (PLAN.md S11, T310). It keeps every connected SessionHost informed about what its icon
/// shows (<c>tray_state</c>: agent name, link status, pause flag, whether to show the icon, the address of the UI) and turns
/// the menu entries Pause commands and Resume commands (<c>set_pause</c>) into the global pause flag of the command engine,
/// which rejects runs from every source with the error <c>paused</c> and which <c>/api/v1/status</c> reports.
/// </summary>
/// <remarks>
/// The state is pushed when the connections or the pause flag change and refreshed at a fixed interval, because the number of
/// paired clients and open link connections has no change notification; a message is sent only when it differs from the last
/// one of that connection. Only the SessionHost of the active console session may change the pause flag: another user's
/// session (a disconnected or remote one) has no business switching off the commands of the PC.
/// </remarks>
public sealed class TrayStateService : BackgroundService
{
    /// <summary>The default interval at which the state is read again.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly WindowsSessionManager _manager;
    private readonly WindowsSessionBridge _bridge;
    private readonly AgentPause _pause;
    private readonly ITrayStatusSource _source;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private readonly HashSet<SessionConnection> _attached = [];
    private readonly ConcurrentDictionary<SessionConnection, SessionTrayStateMessage> _sent = new();

    /// <summary>Creates the service.</summary>
    /// <param name="manager">Holds the connections of the SessionHosts.</param>
    /// <param name="bridge">Knows which connection belongs to the active console session.</param>
    /// <param name="pause">The global pause flag of the command engine.</param>
    /// <param name="source">Reports the settings and the link status.</param>
    /// <param name="time">The clock of the refresh interval; null uses the system clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="refreshInterval">How often the state is read again; null uses <see cref="DefaultRefreshInterval"/>.</param>
    public TrayStateService(
        WindowsSessionManager manager,
        WindowsSessionBridge bridge,
        AgentPause pause,
        ITrayStatusSource source,
        TimeProvider? time,
        ILogger logger,
        TimeSpan? refreshInterval = null)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _bridge = bridge;
        _pause = pause;
        _source = source;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _manager.ConnectionsChanged += OnStateChanged;
        _pause.Changed += OnStateChanged;
        try
        {
            AttachToConnections();
            while (!stoppingToken.IsCancellationRequested)
            {
                await PushAsync(stoppingToken).ConfigureAwait(false);
                await WaitForChangeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The agent stops.
        }
        finally
        {
            _manager.ConnectionsChanged -= OnStateChanged;
            _pause.Changed -= OnStateChanged;
            DetachFromConnections();
        }
    }

    private async Task WaitForChangeAsync(CancellationToken stoppingToken)
    {
        using var interval = new CancellationTokenSource(_refreshInterval, _time);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, interval.Token);
        try
        {
            await _signal.WaitAsync(either.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The interval elapsed: read the state again.
        }

        // Several changes in a row are one refresh.
        while (await _signal.WaitAsync(TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false))
        {
            // Drained.
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        AttachToConnections();
        Signal();
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // The service ended; nobody waits for the signal any more.
        }
    }

    private void AttachToConnections()
    {
        IReadOnlyList<SessionConnection> open = _manager.GetConnections();
        lock (_gate)
        {
            foreach (SessionConnection connection in open)
            {
                if (_attached.Add(connection))
                {
                    connection.PauseRequested += OnPauseRequested;
                }
            }

            // A connection that is gone: nothing more to receive from it, nothing to remember about it.
            foreach (SessionConnection connection in _attached.Where(connection => !open.Contains(connection)).ToArray())
            {
                connection.PauseRequested -= OnPauseRequested;
                _attached.Remove(connection);
                _sent.TryRemove(connection, out _);
            }
        }
    }

    private void DetachFromConnections()
    {
        lock (_gate)
        {
            foreach (SessionConnection connection in _attached)
            {
                connection.PauseRequested -= OnPauseRequested;
            }

            _attached.Clear();
            _sent.Clear();
        }
    }

    private void OnPauseRequested(object? sender, SessionPauseRequestedEventArgs e)
    {
        if (sender is not SessionConnection connection)
        {
            return;
        }

        if (ReferenceEquals(_bridge.ActiveConnection, connection))
        {
            _pause.SetPaused(e.Paused);
            SessionLog.PauseChangedFromTray(_logger, e.Paused ? "paused" : "resumed", connection.SessionId, connection.UserName);
        }
        else
        {
            SessionLog.PauseRefused(_logger, connection.SessionId, connection.UserName);
        }

        // Whatever happened, the icon that asked shows the real state afterwards.
        _sent.TryRemove(connection, out _);
        Signal();
    }

    private async Task PushAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionConnection> connections = _manager.GetConnections();
        if (connections.Count == 0)
        {
            return;
        }

        TrayStatus status;
        try
        {
            status = await _source.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SessionLog.TrayStateUnavailable(_logger, exception.Message);
            return;
        }

        var message = new SessionTrayStateMessage(
            status.AgentName,
            _pause.IsPaused,
            status.PairedClients,
            status.ConnectedClients,
            status.ShowTrayIcon,
            status.UiUrl);

        foreach (SessionConnection connection in connections)
        {
            if (_sent.TryGetValue(connection, out SessionTrayStateMessage? previous) && previous == message)
            {
                continue;
            }

            try
            {
                await connection.SendTrayStateAsync(message, cancellationToken).ConfigureAwait(false);
                _sent[connection] = message;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is ending; the manager reports it and the next push goes to its successor.
                _sent.TryRemove(connection, out _);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _signal.Dispose();
    }
}
