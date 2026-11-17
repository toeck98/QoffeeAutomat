using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.SessionHost.Ipc;

namespace QoffeeAutomat.SessionHost.Tray;

/// <summary>Raised by the tray shell when the user chose Pause commands or Resume commands.</summary>
public sealed class TrayPauseRequestedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="paused">The wanted value of the global pause flag.</param>
    public TrayPauseRequestedEventArgs(bool paused)
    {
        Paused = paused;
    }

    /// <summary>The wanted value of the global pause flag.</summary>
    public bool Paused { get; }
}

/// <summary>The part of the tray icon that touches the desktop; <see cref="NotifyIconShell"/> is the real one.</summary>
public interface ITrayShell : IDisposable
{
    /// <summary>Raised when the user chose Pause commands or Resume commands. May be raised on any thread.</summary>
    event EventHandler<TrayPauseRequestedEventArgs>? PauseRequested;

    /// <summary>
    /// Raised when the user chose Open UI or double-clicked the icon (T711). The shell does not open anything itself: the
    /// handler decides which address to open and calls <see cref="OpenBrowser"/>. May be raised on any thread.
    /// </summary>
    event EventHandler? OpenUiRequested;

    /// <summary>Shows a new state; callable from any thread.</summary>
    /// <param name="view">What to show.</param>
    void Update(TrayView view);

    /// <summary>Opens a loopback address in the default browser of the user; callable from any thread.</summary>
    /// <param name="address">The address; the caller has checked that it is a loopback http address.</param>
    void OpenBrowser(Uri address);
}

/// <summary>
/// Connects the tray icon with the agent (PLAN.md S11, T310): what the agent reports and whether the pipe is up becomes the
/// view of the icon, and the menu entries Pause commands and Resume commands become <c>set_pause</c> messages. The agent
/// stays the authority: the icon changes when the agent confirms with a new <c>tray_state</c>, not when the user clicks.
/// </summary>
public sealed class TrayCoordinator : IDisposable
{
    private readonly SessionHostClient _client;
    private readonly ITrayShell _shell;
    private readonly ISessionHostLog _log;
    private readonly object _gate = new();
    private readonly bool _isAdministrator;
    private SessionTrayStateMessage? _state;
    private bool _connected;
    private bool _disposed;

    /// <summary>Creates the coordinator and starts listening.</summary>
    /// <param name="client">The connection to the agent.</param>
    /// <param name="shell">The icon.</param>
    /// <param name="log">The diagnostics log.</param>
    /// <param name="isAdministrator">
    /// Whether the user is an administrator. Only then is a login token requested; the agent checks again and does not take this
    /// on trust.
    /// </param>
    public TrayCoordinator(SessionHostClient client, ITrayShell shell, ISessionHostLog? log = null, bool isAdministrator = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(shell);

        _client = client;
        _shell = shell;
        _log = log ?? NullSessionHostLog.Instance;
        _isAdministrator = isAdministrator;

        _client.Connected += OnConnected;
        _client.Disconnected += OnDisconnected;
        _client.TrayStateReceived += OnTrayState;
        _shell.PauseRequested += OnPauseRequested;
        _shell.OpenUiRequested += OnOpenUiRequested;
    }

    /// <summary>How long Open UI waits for the agent to answer the request for a login token before it opens the login page.</summary>
    public static TimeSpan LoginTokenTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The view that is currently shown.</summary>
    public TrayView Current
    {
        get
        {
            lock (_gate)
            {
                return TrayPresenter.Present(_state, _connected);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _client.Connected -= OnConnected;
        _client.Disconnected -= OnDisconnected;
        _client.TrayStateReceived -= OnTrayState;
        _shell.PauseRequested -= OnPauseRequested;
        _shell.OpenUiRequested -= OnOpenUiRequested;
    }

    /// <summary>
    /// Opens the UI in the browser (T711). When the user is an administrator the agent is asked for a single-use login token
    /// and the browser opens with it, so no password is needed; the agent decides and a standard user never gets a token. A
    /// standard user, a refusal, a timeout and a lost connection all end the same way: the browser opens the plain address and
    /// shows the normal login page.
    /// </summary>
    /// <returns>A task that completes when the browser was started or there was nothing to open.</returns>
    public async Task OpenUiAsync()
    {
        TrayView view = Current;
        Uri? ui = view.UiUri;
        if (ui is null || !view.CanOpenUi)
        {
            return;
        }

        Uri target = ui;
        if (_isAdministrator)
        {
            string? token = await _client.RequestLoginTokenAsync(LoginTokenTimeout).ConfigureAwait(false);
            if (token is not null)
            {
                target = TrayPresenter.BuildLoginUri(ui, token);
            }
        }

        _shell.OpenBrowser(target);
    }

    private void OnOpenUiRequested(object? sender, EventArgs e)
    {
        _ = OpenUiSafeAsync();
    }

    private async Task OpenUiSafeAsync()
    {
        try
        {
            await OpenUiAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            _log.Warning("The UI could not be opened: " + exception.Message);
        }
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _connected = true;
            Apply();
        }
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _connected = false;
            Apply();
        }
    }

    private void OnTrayState(object? sender, SessionTrayStateEventArgs e)
    {
        lock (_gate)
        {
            _state = e.State;
            Apply();
        }
    }

    private void OnPauseRequested(object? sender, TrayPauseRequestedEventArgs e)
    {
        _ = RequestAsync(e.Paused);
    }

    private async Task RequestAsync(bool paused)
    {
        if (!await _client.RequestPauseAsync(paused).ConfigureAwait(false))
        {
            _log.Warning("The request to " + (paused ? "pause" : "resume") + " the commands was not sent: no connection to the agent.");
        }
    }

    // Called with the lock held, so the shell sees the states in the order in which they happened.
    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        _shell.Update(TrayPresenter.Present(_state, _connected));
    }
}
