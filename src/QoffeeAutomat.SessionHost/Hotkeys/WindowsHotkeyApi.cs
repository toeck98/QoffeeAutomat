using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.SessionHost.Hotkeys;

/// <summary>
/// <see cref="IHotkeyApi"/> on a real desktop (PLAN.md S16, T708): <c>RegisterHotKey</c> and <c>UnregisterHotKey</c> bound to a
/// hidden window on a thread of its own with a message loop, because the SessionHost itself is asynchronous code without one and
/// Windows delivers <c>WM_HOTKEY</c> to the thread that registered the key. Calls from other threads are marshalled to that
/// thread. The registrations disappear with the window when the SessionHost ends. Registrations carry <c>MOD_NOREPEAT</c>, so
/// holding the keys down raises one press. Verified by hand on a desktop (human_verify).
/// </summary>
public sealed class WindowsHotkeyApi : IHotkeyApi
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private ApplicationContext? _context;
    private HotkeyWindow? _window;
    private Exception? _startFailure;
    private int _disposed;

    /// <summary>Creates the hidden window and waits until it exists.</summary>
    /// <exception cref="InvalidOperationException">The window could not be created (no desktop).</exception>
    public WindowsHotkeyApi()
    {
        _thread = new Thread(Run) { Name = "QoffeeAutomat hotkeys", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Task.Wait(StartTimeout) || _startFailure is not null)
        {
            Dispose();
            throw new InvalidOperationException("The window for the hotkeys could not be created.", _startFailure);
        }
    }

    /// <inheritdoc />
    public event EventHandler<HotkeyIdEventArgs>? Pressed;

    /// <inheritdoc />
    public HotkeyRegistrationResult Register(int id, HotkeyModifiers modifiers, int virtualKey, out string? detail)
    {
        HotkeyWindow? window = _window;
        if (window is null || Volatile.Read(ref _disposed) != 0)
        {
            detail = "The hotkey support has ended.";
            return HotkeyRegistrationResult.Invalid;
        }

        try
        {
            (HotkeyRegistrationResult result, string? message) = ((HotkeyRegistrationResult, string?))window.Invoke(
                new Func<(HotkeyRegistrationResult, string?)>(() => RegisterOnThread(window, id, modifiers, virtualKey)));
            detail = message;
            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            detail = "The hotkey support has ended.";
            return HotkeyRegistrationResult.Invalid;
        }
    }

    /// <inheritdoc />
    public void Unregister(int id)
    {
        HotkeyWindow? window = _window;
        if (window is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            window.Invoke(new Action(() => _ = NativeMethods.UnregisterHotKey(window.Handle, id)));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // The window is gone, and with it the registration.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        HotkeyWindow? window = _window;
        try
        {
            if (window is { IsHandleCreated: true })
            {
                window.BeginInvoke(new Action(() => _context?.ExitThread()));
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

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _ = _thread.Join(StopTimeout);
        }
    }

    private static (HotkeyRegistrationResult, string?) RegisterOnThread(HotkeyWindow window, int id, HotkeyModifiers modifiers, int virtualKey)
    {
        if (NativeMethods.RegisterHotKey(window.Handle, id, (uint)modifiers | ModNoRepeat, (uint)virtualKey))
        {
            return (HotkeyRegistrationResult.Registered, null);
        }

        int error = Marshal.GetLastPInvokeError();
        return error == ErrorHotkeyAlreadyRegistered
            ? (HotkeyRegistrationResult.Conflict, "Another program already uses this key combination.")
            : (HotkeyRegistrationResult.Invalid, "Windows refused the key combination: " + new Win32Exception(error).Message);
    }

    private void Run()
    {
        try
        {
            _context = new ApplicationContext();
            _window = new HotkeyWindow(id => Pressed?.Invoke(this, new HotkeyIdEventArgs(id)));
            _ = _window.Handle;
        }
#pragma warning disable CA1031 // Whatever goes wrong while the window is made, the SessionHost works on without hotkeys.
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

    private void Cleanup()
    {
        // Destroying the window removes every registration that belongs to it.
        _window?.Dispose();
        _window = null;
        _context?.Dispose();
        _context = null;
    }

    /// <summary>A window that is never shown; it only receives <c>WM_HOTKEY</c>.</summary>
    private sealed class HotkeyWindow : Control
    {
        private readonly Action<int> _onPressed;

        public HotkeyWindow(Action<int> onPressed)
        {
            _onPressed = onPressed;
        }

        protected override void SetVisibleCore(bool value)
        {
            // Never visible, whatever the framework asks.
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                _onPressed((int)m.WParam);
                return;
            }

            base.WndProc(ref m);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
