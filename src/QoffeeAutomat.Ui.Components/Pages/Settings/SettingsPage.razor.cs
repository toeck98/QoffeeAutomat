using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Settings;

/// <summary>
/// The settings page (PLAN.md T407). It edits a draft of the agent settings and saves it against the revision it read. A change of
/// a port or of the remote UI switch only takes effect after a restart, so the page says so and offers to restart the agent. The
/// password is changed with its own form, which asks for the current password.
/// </summary>
public partial class SettingsPage
{
    private static readonly string[] LogLevels = ["trace", "debug", "information", "warning", "error", "critical"];

    private AgentSettingsDocument? _settings;
    private ApiException? _loadError;
    private ApiException? _actionError;
    private string _actionFailure = string.Empty;
    private string? _notice;
    private bool _loading = true;
    private bool _saving;
    private bool _restartRequired;
    private bool _restarting;

    private string _name = string.Empty;
    private int _uiPort = AgentSettingsDefaults.UiPort;
    private int _linkPort = AgentSettingsDefaults.LinkPort;
    private bool _remoteUi;
    private bool _trayIcon = true;
    private string _logLevel = "information";
    private int? _runHistoryDays;
    private int? _auditLogDays;

    private string? _currentPassword;
    private string? _newPassword;
    private string? _repeatPassword;
    private bool _changingPassword;
    private ApiException? _passwordError;
    private string? _passwordFailure;
    private string? _passwordNotice;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    private async Task ReloadAsync()
    {
        _loading = true;
        await LoadAsync();
        _loading = false;
    }

    private async Task LoadAsync()
    {
        try
        {
            AgentSettingsDocument settings = await Api.GetSettingsAsync();
            _loadError = null;
            Adopt(settings);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _loadError = exception;
        }
    }

    /// <summary>Takes the saved settings as the new baseline and fills the form from them.</summary>
    private void Adopt(AgentSettingsDocument settings)
    {
        _settings = settings;
        _name = settings.Name;
        _uiPort = settings.EffectiveUiPort;
        _linkPort = settings.EffectiveLinkPort;
        _remoteUi = settings.RemoteUi ?? false;
        _trayIcon = settings.EffectiveTrayIcon;
        string level = settings.Logging?.Level?.ToLowerInvariant() ?? "information";
        _logLevel = Array.IndexOf(LogLevels, level) >= 0 ? level : "information";
        _runHistoryDays = settings.Retention?.RunHistoryDays;
        _auditLogDays = settings.Retention?.AuditLogDays;
    }

    private async Task SaveAsync()
    {
        if (_settings is null || _saving)
        {
            return;
        }

        _saving = true;
        _actionError = null;
        _notice = null;
        AgentSettingsDocument previous = _settings;
        AgentSettingsDocument draft = previous with
        {
            Name = _name.Trim(),
            UiPort = _uiPort,
            LinkPort = _linkPort,
            RemoteUi = _remoteUi,
            TrayIcon = _trayIcon,
            Logging = new LoggingSettings(_logLevel),
            Retention = new RetentionSettings(_runHistoryDays, _auditLogDays),
        };

        try
        {
            AgentSettingsDocument saved = await Api.PutSettingsAsync(draft);
            if (IsDisposed)
            {
                return;
            }

            // The listeners are bound when the agent starts, so these three only change at the next start.
            if (saved.EffectiveUiPort != previous.EffectiveUiPort
                || saved.EffectiveLinkPort != previous.EffectiveLinkPort
                || (saved.RemoteUi ?? false) != (previous.RemoteUi ?? false))
            {
                _restartRequired = true;
            }

            Adopt(saved);
            _notice = L["Settings.Saved"];
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _actionError = exception;
            _actionFailure = L["Settings.SaveFailed"];
            if (exception.StatusCode == 412)
            {
                // Somebody saved in the meantime; keep what was typed, but save against the current revision next time.
                await ReloadRevisionAsync(draft);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task ReloadRevisionAsync(AgentSettingsDocument draft)
    {
        try
        {
            AgentSettingsDocument current = await Api.GetSettingsAsync();
            _settings = current;
            _name = draft.Name;
        }
        catch (ApiException)
        {
            // The error above is the one to show; the next save fails the same way and says so.
        }
    }

    private async Task RestartAsync()
    {
        if (_restarting)
        {
            return;
        }

        _restarting = true;
        _actionError = null;
        _notice = null;
        try
        {
            await Api.RestartAgentAsync();
            _restartRequired = false;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            _restarting = false;
            Session.MarkSignedOut();
        }
        catch (ApiException exception) when (exception.IsNetworkError)
        {
            // The agent went down before it finished the answer, which is what a restart looks like from here.
            _restartRequired = false;
        }
        catch (ApiException exception)
        {
            _restarting = false;
            _actionError = exception;
            _actionFailure = L["Settings.RestartFailed"];
        }
    }

    private async Task ChangePasswordAsync()
    {
        if (_changingPassword)
        {
            return;
        }

        _passwordError = null;
        _passwordFailure = null;
        _passwordNotice = null;
        if (string.IsNullOrEmpty(_currentPassword) || string.IsNullOrEmpty(_newPassword))
        {
            _passwordFailure = L["Settings.Password.Missing"];
            return;
        }

        if (!string.Equals(_newPassword, _repeatPassword, StringComparison.Ordinal))
        {
            _passwordFailure = L["Settings.Password.Mismatch"];
            return;
        }

        _changingPassword = true;
        try
        {
            await Api.ChangePasswordAsync(new ChangePasswordRequest(_currentPassword, _newPassword));
            _currentPassword = null;
            _newPassword = null;
            _repeatPassword = null;
            _passwordNotice = L["Settings.Password.Changed"];
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _passwordError = exception;
            _passwordFailure = L["Settings.Password.Failed"];
        }
        finally
        {
            _changingPassword = false;
        }
    }
}
