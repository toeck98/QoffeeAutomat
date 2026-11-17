using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace QoffeeAutomat.SessionHost.Notifications;

/// <summary>
/// The <see cref="IToastPresenter"/> of a Windows session (PLAN.md S11, T702): toast notifications through the Windows Runtime
/// (<c>ToastNotificationManager</c>). The SessionHost is not a packaged app, so the application id under which Windows shows the
/// notifications is registered for the current user in <c>HKCU\Software\Classes\AppUserModelId</c> (a display name, nothing
/// outside the profile of the user). This is the only class of the notifications that touches the Windows Runtime.
/// </summary>
/// <remarks>
/// A button press arrives through the <c>Activated</c> event of the notification object, so it works as long as the SessionHost
/// runs: for a notification that is still on the screen and for one in the action center. Windows forgets the handlers when the
/// process ends, so a button of a notification from before a restart of the SessionHost does nothing; the notifications expire
/// with the next start of Windows (<c>ExpiresOnReboot</c>) and after a day.
/// </remarks>
public sealed class WindowsToastPresenter : IToastPresenter, IDisposable
{
    /// <summary>The application id of the notifications.</summary>
    public const string AppUserModelId = "QoffeeAutomat.SessionHost";

    private const string DisplayName = "QoffeeAutomat";
    private const string Group = "qoffeeautomat";
    private const int MaxKept = 50;

    private readonly object _gate = new();
    private readonly ISessionHostLog _log;
    // The notifications are kept so that Windows can still call the handler of one that is in the action center.
    private readonly List<ToastNotification> _kept = [];
    private ToastNotifier? _notifier;

    /// <summary>Creates the presenter.</summary>
    /// <param name="log">The diagnostics log.</param>
    public WindowsToastPresenter(ISessionHostLog? log = null)
    {
        _log = log ?? NullSessionHostLog.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<ToastActionEventArgs>? ActionInvoked;

    /// <inheritdoc />
    public string? Show(ToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ToastNotifier notifier = GetNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                return "Windows does not show notifications from QoffeeAutomat (" + notifier.Setting + "); check the notification settings and focus assist.";
            }

            var xml = new XmlDocument();
            xml.LoadXml(ToastXml.Build(request));
            var toast = new ToastNotification(xml)
            {
                Group = Group,
                ExpiresOnReboot = true,
                ExpirationTime = DateTimeOffset.Now.AddDays(1),
            };
            if (request.Tag is { Length: > 0 } tag)
            {
                toast.Tag = tag;
            }

            toast.Activated += OnActivated;
            Keep(toast);
            notifier.Show(toast);
            return null;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException)
        {
            _log.Warning("A notification could not be shown: " + exception.Message);
            return "The notification could not be shown: " + exception.Message;
        }
    }

    /// <summary>Forgets the notifications; their buttons do nothing afterwards.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (ToastNotification toast in _kept)
            {
                toast.Activated -= OnActivated;
            }

            _kept.Clear();
        }
    }

    private ToastNotifier GetNotifier()
    {
        lock (_gate)
        {
            if (_notifier is null)
            {
                RegisterApplicationId();
                _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            }

            return _notifier;
        }
    }

    private static void RegisterApplicationId()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId);
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
    }

    private void Keep(ToastNotification toast)
    {
        lock (_gate)
        {
            _kept.Add(toast);
            while (_kept.Count > MaxKept)
            {
                _kept[0].Activated -= OnActivated;
                _kept.RemoveAt(0);
            }
        }
    }

    private void OnActivated(ToastNotification sender, object args)
    {
        if (args is ToastActivatedEventArgs activated
            && ToastArguments.TryParse(activated.Arguments, out string action, out string? tag))
        {
            ActionInvoked?.Invoke(this, new ToastActionEventArgs(action, tag));
        }
    }
}
