using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.SessionHost.Ipc;

namespace QoffeeAutomat.SessionHost.Hotkeys;

/// <summary>
/// Connects the hotkeys with the agent (PLAN.md S16, T708): the list in <c>hotkeys</c> is registered through the
/// <see cref="HotkeyManager"/>, the outcome of each hotkey goes back as <c>hotkey_state</c> so that the UI can show conflicts,
/// and every press becomes the event <c>hotkey_pressed</c> with the configured name. When the connection to the agent is lost
/// all hotkeys are released: a key that can no longer do anything must not be taken away from other programs; the agent sends
/// the list again after the next handshake.
/// </summary>
public sealed class HotkeyCoordinator : IDisposable
{
    private readonly SessionHostClient _client;
    private readonly HotkeyManager _manager;
    private readonly ISessionHostLog _log;
    private int _disposed;

    /// <summary>Creates the coordinator and starts listening.</summary>
    /// <param name="client">The connection to the agent.</param>
    /// <param name="manager">Registers the hotkeys.</param>
    /// <param name="log">The diagnostics log.</param>
    public HotkeyCoordinator(SessionHostClient client, HotkeyManager manager, ISessionHostLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(manager);

        _client = client;
        _manager = manager;
        _log = log ?? NullSessionHostLog.Instance;

        _client.HotkeysReceived += OnHotkeys;
        _client.Disconnected += OnDisconnected;
        _manager.HotkeyPressed += OnPressed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.HotkeysReceived -= OnHotkeys;
        _client.Disconnected -= OnDisconnected;
        _manager.HotkeyPressed -= OnPressed;
        _manager.Clear();
    }

    private void OnHotkeys(object? sender, SessionHotkeysEventArgs e)
    {
        IReadOnlyList<SessionHotkeyStatus> status = _manager.Apply(e.Message.Hotkeys);
        _ = ReportStateAsync(status);
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        _manager.Clear();
    }

    private void OnPressed(object? sender, HotkeyPressedEventArgs e)
    {
        _ = ReportPressAsync(e.Name);
    }

    private async Task ReportStateAsync(IReadOnlyList<SessionHotkeyStatus> status)
    {
        if (!await _client.ReportHotkeyStateAsync(status).ConfigureAwait(false))
        {
            _log.Warning("The state of the hotkeys was not sent: no connection to the agent.");
        }
    }

    private async Task ReportPressAsync(string name)
    {
        if (!await _client.ReportHotkeyPressedAsync(name).ConfigureAwait(false))
        {
            _log.Warning("A hotkey was pressed but the event was not sent: no connection to the agent.");
        }
    }
}
