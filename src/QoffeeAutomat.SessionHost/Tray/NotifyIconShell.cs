using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;

namespace QoffeeAutomat.SessionHost.Tray;

/// <summary>
/// The tray icon on the desktop of the user (PLAN.md S11, T310): a Windows Forms <see cref="NotifyIcon"/> with the menu Open UI,
/// Pause commands, Resume commands and About, running on a thread of its own with a message loop, because the SessionHost
/// itself is asynchronous code without one. The icon is removed when the shell is disposed, so it disappears when the
/// SessionHost ends.
/// </summary>
public sealed class NotifyIconShell : ITrayShell
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly string _version;
    private readonly ISessionHostLog _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private ApplicationContext? _context;
    private Control? _marshal;
    private NotifyIcon? _icon;
    private TrayIconSet? _icons;
    private ToolStripMenuItem? _openUi;
    private ToolStripMenuItem? _pause;
    private ToolStripMenuItem? _resume;
    private Exception? _startFailure;
    private TrayView _view = TrayView.Hidden;
    private bool _aboutOpen;
    private int _disposed;

    /// <summary>Creates the shell and waits until the icon exists.</summary>
    /// <param name="version">The product version shown in the About box.</param>
    /// <param name="log">The diagnostics log.</param>
    /// <exception cref="InvalidOperationException">The icon could not be created (no desktop, missing resource).</exception>
    public NotifyIconShell(string version, ISessionHostLog? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        _version = version;
        _log = log ?? NullSessionHostLog.Instance;
        _thread = new Thread(Run) { Name = "QoffeeAutomat tray", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Task.Wait(StartTimeout) || _startFailure is not null)
        {
            Dispose();
            throw new InvalidOperationException("The tray icon could not be created.", _startFailure);
        }
    }

    /// <inheritdoc />
    public event EventHandler<TrayPauseRequestedEventArgs>? PauseRequested;

    /// <inheritdoc />
    public event EventHandler? OpenUiRequested;

    /// <inheritdoc />
    public void Update(TrayView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        Control? marshal = _marshal;
        if (marshal is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (marshal.IsHandleCreated)
            {
                marshal.BeginInvoke(() => Apply(view));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The message loop is ending; the icon goes away anyway.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Control? marshal = _marshal;
        try
        {
            if (marshal is { IsHandleCreated: true })
            {
                marshal.BeginInvoke(() => _context?.ExitThread());
            }
            else
            {
                _context?.ExitThread();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The loop has ended already.
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread && !_thread.Join(StopTimeout))
        {
            _log.Warning("The tray icon thread did not end in time.");
        }

        if (!_thread.IsAlive)
        {
            // The thread cleaned up after itself; this only makes sure that nothing is left when it ended early.
            Cleanup();
        }
    }

    private void Run()
    {
        // An exception in a menu handler must not take the SessionHost down: the icon is a convenience.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        ThreadExceptionEventHandler onThreadException = (_, e) => _log.Warning("The tray icon failed: " + e.Exception.Message);
        Application.ThreadException += onThreadException;
        try
        {
            RunLoop();
        }
        finally
        {
            Application.ThreadException -= onThreadException;
        }
    }

    private void RunLoop()
    {
        try
        {
            _icons = new TrayIconSet();
            _context = new ApplicationContext();
            _marshal = new Control();
            _ = _marshal.Handle;

            var menu = new ContextMenuStrip();
            _openUi = new ToolStripMenuItem("Open UI", null, (_, _) => OpenUi()) { Enabled = false };
            _pause = new ToolStripMenuItem("Pause commands", null, (_, _) => RequestPause(true)) { Enabled = false };
            _resume = new ToolStripMenuItem("Resume commands", null, (_, _) => RequestPause(false)) { Enabled = false };
            var about = new ToolStripMenuItem("About", null, (_, _) => ShowAbout());
            menu.Items.AddRange(new ToolStripItem[] { _openUi, new ToolStripSeparator(), _pause, _resume, new ToolStripSeparator(), about });

            _icon = new NotifyIcon
            {
                Icon = _icons.Get(TrayIconState.Disconnected),
                Text = "QoffeeAutomat",
                ContextMenuStrip = menu,
                Visible = false,
            };
            _icon.DoubleClick += (_, _) => OpenUi();
        }
#pragma warning disable CA1031 // Whatever goes wrong while the icon is made, the SessionHost works on without it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _startFailure = exception;
            _ready.TrySetResult();
            Cleanup();
            return;
        }

        _ready.TrySetResult();
        try
        {
            Application.Run(_context);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Apply(TrayView view)
    {
        _view = view;
        if (_icon is null || _icons is null)
        {
            return;
        }

        _icon.Icon = _icons.Get(view.Icon);
        _icon.Text = view.Tooltip.Length > 0 ? view.Tooltip : "QoffeeAutomat";
        _openUi!.Enabled = view.CanOpenUi;
        _pause!.Enabled = view.CanPause;
        _resume!.Enabled = view.CanResume;

        // Visible last: the icon appears with its final look.
        _icon.Visible = view.Visible;
    }

    /// <inheritdoc />
    public void OpenBrowser(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Only a loopback http address is ever opened, whoever asks (the address of a login link carries a token).
        if (address.Scheme != Uri.UriSchemeHttp || !address.IsLoopback || address.UserInfo.Length > 0)
        {
            _log.Warning("Refused to open an address that is not a loopback http address.");
            return;
        }

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The message does not contain the address: it may hold a login token.
            _log.Warning("The UI could not be opened in the browser: " + exception.Message);
        }
    }

    private void OpenUi()
    {
        if (_view.UiUri is null || !_view.CanOpenUi)
        {
            return;
        }

        OpenUiRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RequestPause(bool paused)
    {
        PauseRequested?.Invoke(this, new TrayPauseRequestedEventArgs(paused));
    }

    private void ShowAbout()
    {
        if (_aboutOpen)
        {
            return;
        }

        _aboutOpen = true;
        try
        {
            string agent = _view.AgentName.Length > 0 ? _view.AgentName : "(not connected)";
            string address = _view.UiUri?.AbsoluteUri ?? "(unknown)";
            string text = string.Create(
                CultureInfo.InvariantCulture,
                $"QoffeeAutomat {_version}\n\nConnects this PC with Home Assistant.\n\nAgent: {agent}\nUI: {address}");
            MessageBox.Show(text, "About QoffeeAutomat", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _aboutOpen = false;
        }
    }

    private void Cleanup()
    {
        if (_icon is not null)
        {
            // Hidden first, otherwise the icon stays in the notification area until the mouse moves over it.
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _icon = null;
        }

        _marshal?.Dispose();
        _marshal = null;
        _icons?.Dispose();
        _icons = null;
        _context?.Dispose();
        _context = null;
    }
}
