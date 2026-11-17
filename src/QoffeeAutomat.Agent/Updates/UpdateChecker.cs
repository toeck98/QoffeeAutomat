using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>What the user chose about the update check (the settings document, <see cref="UpdateSettings"/>).</summary>
public interface IUpdateSettingsSource
{
    /// <summary>Whether the agent may ask GitHub for a newer release.</summary>
    bool CheckEnabled { get; }

    /// <summary>The time between two checks.</summary>
    TimeSpan Interval { get; }
}

/// <summary>Reads the update settings from the effective configuration, so that a change takes effect without a restart.</summary>
public sealed class ConfigUpdateSettingsSource : IUpdateSettingsSource
{
    private readonly IConfigStore _config;

    /// <summary>Creates the source.</summary>
    /// <param name="config">The configuration.</param>
    public ConfigUpdateSettingsSource(IConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
    }

    /// <inheritdoc />
    public bool CheckEnabled => Settings?.EffectiveUpdateCheck ?? true;

    /// <inheritdoc />
    public TimeSpan Interval => TimeSpan.FromHours(Settings?.EffectiveUpdateIntervalHours ?? AgentSettingsDefaults.UpdateIntervalHours);

    private AgentSettingsDocument? Settings => _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
}

/// <summary>
/// Asks GitHub for the newest release once a day and publishes what it learns as the <see cref="UpdateStatus"/> (PLAN.md S09 item 13,
/// S13, S16, T704). The request is the only one that the agent makes to the internet on its own. It is made only while
/// <see cref="UpdateOptions.Supported"/> and the setting <c>updates.check</c> are on; with the check off the service does nothing but
/// look at the settings now and then, and no <see cref="IReleaseSource"/> member is called. A failure (no network, a bad answer) is
/// logged and leaves the last known status in place; the next attempt comes at the next interval.
/// </summary>
public sealed class UpdateChecker : BackgroundService, IUpdateStatusSource
{
    private readonly IReleaseSource _releases;
    private readonly IUpdateSettingsSource _settings;
    private readonly UpdateOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateChecker> _logger;
    private readonly Lock _gate = new();

    private UpdateStatus _status;
    private ReleaseInfo? _latest;
    private DateTimeOffset? _lastAttempt;
    private bool _loggedDisabled;

    /// <summary>Creates the checker.</summary>
    /// <param name="releases">Where the releases come from.</param>
    /// <param name="settings">The user's choices.</param>
    /// <param name="options">The fixed parameters.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public UpdateChecker(
        IReleaseSource releases,
        IUpdateSettingsSource settings,
        UpdateOptions options,
        TimeProvider time,
        ILogger<UpdateChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _releases = releases;
        _settings = settings;
        _options = options;
        _time = time;
        _logger = logger;
        _status = new UpdateStatus(options.InstalledVersion);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public UpdateStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The newest release that the last successful check found, with its installer and checksum file; null while none is known.</summary>
    public ReleaseInfo? LatestRelease
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>
    /// Checks when a check is due: the self-update is supported, the check is switched on and the interval since the last attempt has
    /// passed. Otherwise nothing happens and, above all, no request is made.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when a request to GitHub was made (whatever its outcome).</returns>
    public async Task<bool> CheckIfDueAsync(CancellationToken cancellationToken)
    {
        if (!_options.Supported)
        {
            return false;
        }

        if (!_settings.CheckEnabled)
        {
            if (!_loggedDisabled)
            {
                _loggedDisabled = true;
                UpdateLog.CheckDisabled(_logger);
            }

            return false;
        }

        _loggedDisabled = false;
        DateTimeOffset now = _time.GetUtcNow();
        if (_lastAttempt is { } last && now - last < _settings.Interval)
        {
            return false;
        }

        _lastAttempt = now;
        await CheckNowAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Asks GitHub now, whatever the interval says. It is for the callers that act on a person's request; the periodic check goes through
    /// <see cref="CheckIfDueAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the answer is processed or the failure is logged.</returns>
    public async Task CheckNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            ReleaseInfo? release = await _releases.GetLatestAsync(cancellationToken).ConfigureAwait(false);
            Apply(release);
        }
        catch (Exception exception) when (IsExpectedFailure(exception, cancellationToken))
        {
            UpdateLog.CheckFailed(_logger, exception.Message);
        }
    }

    /// <summary>Publishes a release that another code path found (the updater asks GitHub itself and shares the result).</summary>
    /// <param name="release">The newest release; null when there is none.</param>
    public void Apply(ReleaseInfo? release)
    {
        UpdateStatus next;
        if (release is null)
        {
            UpdateLog.NoRelease(_logger);
            next = new UpdateStatus(_options.InstalledVersion, CheckedAt: _time.GetUtcNow());
        }
        else
        {
            string latest = release.Version.ToString();
            UpdateLog.CheckSucceeded(_logger, latest, _options.InstalledVersion);
            next = new UpdateStatus(_options.InstalledVersion, latest, release.PageUrl, release.Notes, _time.GetUtcNow());
        }

        bool changed;
        lock (_gate)
        {
            changed = _status.LatestVersion != next.LatestVersion
                || _status.ReleaseUrl != next.ReleaseUrl
                || _status.ReleaseNotes != next.ReleaseNotes;
            _status = next;
            _latest = release;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Supported)
        {
            return;
        }

        try
        {
            await Task.Delay(_options.InitialDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckIfDueAsync(stoppingToken).ConfigureAwait(false);

                // Waiting costs nothing; looking at the settings again after a few minutes makes a change take effect without a restart.
                TimeSpan wait = _settings.CheckEnabled && _lastAttempt is { } last
                    ? Min(_options.SettingsPollInterval, last + _settings.Interval - _time.GetUtcNow())
                    : _options.SettingsPollInterval;
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The agent stops.
        }
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static bool IsExpectedFailure(Exception exception, CancellationToken cancellationToken)
    {
        // A cancellation that the caller did not ask for is the time limit of the request.
        return exception is HttpRequestException or UpdateException or JsonException or IOException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
    }
}
