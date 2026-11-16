using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>Loads and validates all configuration documents when the host starts (PLAN.md T202).</summary>
public sealed class ConfigLoadService : IHostedService
{
    private readonly IConfigStore _store;
    private readonly ILogger<ConfigLoadService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The config store.</param>
    /// <param name="logger">The logger.</param>
    public ConfigLoadService(IConfigStore store, ILogger<ConfigLoadService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ConfigLoadReport report = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        AgentLog.ConfigLoaded(_logger, report.Loaded, report.Problems.Count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>Logs the start-up facts (version, platform, listeners) and the stop of the agent.</summary>
public sealed class AgentLifecycleService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly IAppPaths _paths;
    private readonly PlatformSelection _platform;
    private readonly ILogger<AgentLifecycleService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="server">The web server, for its addresses.</param>
    /// <param name="paths">The data directories.</param>
    /// <param name="platform">The selected platform.</param>
    /// <param name="logger">The logger.</param>
    public AgentLifecycleService(
        IHostApplicationLifetime lifetime,
        IServer server,
        IAppPaths paths,
        PlatformSelection platform,
        ILogger<AgentLifecycleService> logger)
    {
        _lifetime = lifetime;
        _server = server;
        _paths = paths;
        _platform = platform;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_platform.Warning is { } warning)
        {
            AgentLog.PlatformFallback(_logger, warning);
        }

        _lifetime.ApplicationStarted.Register(OnStarted);
        _lifetime.ApplicationStopping.Register(() => AgentLog.Stopping(_logger));
        _lifetime.ApplicationStopped.Register(() => AgentLog.Stopped(_logger));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void OnStarted()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var platformName = _platform.Kind.ToString().ToLowerInvariant();
            AgentLog.Started(_logger, AgentVersion.Current, platformName, _paths.ConfigDirectory);
        }

        IServerAddressesFeature? addresses = _server.Features.Get<IServerAddressesFeature>();
        if (addresses is { Addresses.Count: > 0 } && _logger.IsEnabled(LogLevel.Information))
        {
            var joinedAddresses = string.Join(", ", addresses.Addresses);
            AgentLog.Listening(_logger, joinedAddresses);
        }
    }
}

/// <summary>Applies the log level of <c>settings.json</c> (<c>logging.level</c>) to the logger filters.</summary>
public sealed class SettingsLogLevelSetup : IConfigureOptions<LoggerFilterOptions>
{
    private readonly IAppPaths _paths;

    /// <summary>Creates the setup.</summary>
    /// <param name="paths">Where settings.json is.</param>
    public SettingsLogLevelSetup(IAppPaths paths)
    {
        _paths = paths;
    }

    /// <inheritdoc />
    public void Configure(LoggerFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = AgentSettingsLoader.LoadOrDefault(_paths, out _);
        if (settings.Logging?.Level is { } level && Enum.TryParse(level, ignoreCase: true, out LogLevel parsed))
        {
            options.MinLevel = parsed;
        }
    }
}
