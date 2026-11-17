using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Updates;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Actions.Power;
using QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;
using QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;
using QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;
using QoffeeAutomat.Platform.Windows.Notifications;
using QoffeeAutomat.Platform.Windows.Security;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.Platform.Windows.Updates;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>
/// Registers the Windows platform (PLAN.md S04, S11) in the dependency injection container of the agent and sets up
/// the service hosting: the service lifetime, the Event Log provider, DPAPI secrets, the power and session sources
/// and the check of system scripts.
/// </summary>
public static class WindowsPlatformServices
{
    /// <summary>The category of the lifecycle messages (started, stopped) that also go to the Event Log at information level.</summary>
    public const string LifecycleLogCategory = "QoffeeAutomat.Agent.Hosting.AgentLifecycleService";

    /// <summary>The category of security warnings.</summary>
    public const string SecurityLogCategory = "QoffeeAutomat.Security";

    /// <summary>The category of the messages about the built-in actions and their default commands.</summary>
    public const string ActionsLogCategory = "QoffeeAutomat.Actions";

    /// <summary>The category of the messages about the SessionHosts and the session pipe.</summary>
    public const string SessionsLogCategory = "QoffeeAutomat.Sessions";

    /// <summary>The category of the messages about the optional hardware sensors.</summary>
    public const string HardwareLogCategory = "QoffeeAutomat.HardwareSensors";

    /// <summary>The path of the SessionHost executable: it is installed next to the agent executable (PLAN.md S09 item 7).</summary>
    /// <returns>The full path.</returns>
    public static string InstalledSessionHostPath() => Path.Combine(AppContext.BaseDirectory, SessionIpc.SessionHostFileName);

    /// <summary>Tells whether this process was started by the service control manager.</summary>
    /// <returns>True when running as a Windows service.</returns>
    public static bool IsRunningAsService() => WindowsServiceHelpers.IsWindowsService();

    /// <summary>
    /// Adds the Event Log provider when the process runs as a service: warnings and errors, and the lifecycle messages
    /// at information level. The source is registered by <c>service install</c>.
    /// </summary>
    /// <param name="logging">The logging builder.</param>
    /// <param name="serviceName">The service name, which is also the Event Log source.</param>
    /// <returns>The same builder.</returns>
    public static ILoggingBuilder AddWindowsEventLog(this ILoggingBuilder logging, string serviceName = ServiceDefaults.Name)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        if (!IsRunningAsService())
        {
            return logging;
        }

        logging.AddEventLog(settings =>
        {
            settings.SourceName = serviceName;
            settings.LogName = ServiceDefaults.EventLogName;
        });
        logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
        logging.AddFilter<EventLogLoggerProvider>(LifecycleLogCategory, LogLevel.Information);
        return logging;
    }

    /// <summary>
    /// Replaces the simulated providers that the Windows platform implements and, when running as a service, the host
    /// lifetime. The system metrics (T304), the session metrics (T305), the custom sensor types (T307), the built-in
    /// actions (T306) and the elevated launcher (T311) are real.
    /// </summary>
    /// <param name="services">The service collection of the agent.</param>
    /// <param name="directories">Resolves the data directories of PLAN.md S05.</param>
    /// <param name="agentIdFileName">The name of the agent id file in the data directory.</param>
    /// <param name="serviceName">The service name that the control manager knows the process by.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddWindowsPlatform(
        this IServiceCollection services,
        Func<IServiceProvider, DataDirectories> directories,
        string agentIdFileName,
        string serviceName = ServiceDefaults.Name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentIdFileName);

        services.TryAddSingleton<IFileSystemAcl, WindowsFileSystemAcl>();
        services.TryAddSingleton<ISessionUserLookup, WtsSessionUserLookup>();

        services.RemoveAll<ISecretProtector>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();

        services.AddSingleton(provider => new WindowsPowerEvents(provider.GetService<TimeProvider>()));
        services.RemoveAll<IPowerEvents>();
        services.AddSingleton<IPowerEvents>(provider => provider.GetRequiredService<WindowsPowerEvents>());

        services.AddSingleton(provider => new WindowsSessionMonitor(provider.GetRequiredService<ISessionUserLookup>()));

        // The SessionHost per interactive session (T302): the manager starts and supervises them, the pipe server
        // accepts them, and the bridge turns their connection into the session state and the user-context runs.
        services.TryAddSingleton<ISessionNativeApi>(provider => new WtsSessionNativeApi(provider.GetRequiredService<ISessionUserLookup>()));
        services.TryAddSingleton<IPipePeerInspector, NativePipePeerInspector>();
        services.AddSingleton(provider => new SessionPipeServer(
            new SessionPipeServerOptions
            {
                SessionHostPath = InstalledSessionHostPath(),
                ScriptsDirectory = directories(provider).Scripts,
                Version = typeof(WindowsPlatformServices).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
            provider.GetRequiredService<IPipePeerInspector>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(SessionsLogCategory)));
        services.AddSingleton(provider => new WindowsSessionManager(
            provider.GetRequiredService<ISessionNativeApi>(),
            provider.GetRequiredService<SessionPipeServer>(),
            new SessionManagerOptions { SessionHostPath = InstalledSessionHostPath() },
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(SessionsLogCategory)));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<WindowsSessionManager>());
        services.AddSingleton(provider => new WindowsSessionBridge(
            provider.GetRequiredService<WindowsSessionMonitor>(),
            provider.GetRequiredService<WindowsSessionManager>(),
            provider.GetRequiredService<ISessionUserLookup>(),
            provider.GetService<TimeProvider>()));
        services.RemoveAll<ISessionBridge>();
        services.AddSingleton<ISessionBridge>(provider => provider.GetRequiredService<WindowsSessionBridge>());
        services.AddSingleton<IUserContextPowerShell>(provider => provider.GetRequiredService<WindowsSessionBridge>());

        // The tray icon of the SessionHost (T310): tells it what to show and applies Pause commands and Resume commands to the
        // global pause flag. The agent host registers AgentPause and ITrayStatusSource.
        services.AddSingleton(provider => new TrayStateService(
            provider.GetRequiredService<WindowsSessionManager>(),
            provider.GetRequiredService<WindowsSessionBridge>(),
            provider.GetRequiredService<AgentPause>(),
            provider.GetRequiredService<ITrayStatusSource>(),
            provider.GetService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(SessionsLogCategory)));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<TrayStateService>());

        // One-click UI login (T711): answers the tray's request for a single-use login token, for administrators only. The agent
        // host registers ILoginTokenIssuer.
        services.AddSingleton(provider =>
        {
            var bridge = provider.GetRequiredService<WindowsSessionBridge>();
            return new LoginTokenService(
                provider.GetRequiredService<WindowsSessionManager>(),
                () => bridge.ActiveConnection,
                provider.GetRequiredService<IElevatedNativeApi>(),
                provider.GetRequiredService<ILoginTokenIssuer>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(SessionsLogCategory));
        });
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LoginTokenService>());

        // Global hotkeys (T708): the hotkeys of the settings go to the session helpers, presses come back as the event
        // hotkey_pressed, and the outcome of each registration is what the UI shows. The agent host registers IConfigStore and IEventBus.
        services.AddSingleton(provider =>
        {
            var bridge = provider.GetRequiredService<WindowsSessionBridge>();
            return new HotkeyService(
                provider.GetRequiredService<WindowsSessionManager>(),
                () => bridge.ActiveConnection,
                provider.GetRequiredService<IConfigStore>(),
                provider.GetRequiredService<IEventBus>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(SessionsLogCategory));
        });
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<HotkeyService>());
        services.RemoveAll<IHotkeyStatusSource>();
        services.AddSingleton<IHotkeyStatusSource>(provider => provider.GetRequiredService<HotkeyService>());

        // Button presses of notifications (T702) come back from the session helpers as the event notification_action.
        services.AddSingleton(provider => new NotificationActionRelay(
            provider.GetRequiredService<WindowsSessionManager>(),
            provider.GetRequiredService<IEventBus>()));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<NotificationActionRelay>());

        // When the computer started: the source of the metric system.last_boot and of the boot id in hello (T304).
        services.AddSingleton(provider => new WindowsBootTime(provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.RemoveAll<IPlatformInfo>();
        services.AddSingleton<IPlatformInfo>(provider => new WindowsPlatformInfo(
            Path.Combine(directories(provider).Data, agentIdFileName),
            provider.GetService<TimeProvider>(),
            bootTime: provider.GetRequiredService<WindowsBootTime>()));

        // The system metrics (T304); the user metrics come through the session bridge (T305).
        services.TryAddSingleton<ISystemSources>(provider => new WindowsSystemSources(provider.GetService<TimeProvider>()));
        services.AddSingleton(provider => new WindowsSystemMetricProvider(
            provider.GetRequiredService<ISystemSources>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<WindowsBootTime>()));
        services.RemoveAll<IMetricProvider>();
        services.AddSingleton<IMetricProvider>(provider => provider.GetRequiredService<WindowsSystemMetricProvider>());

        // The optional hardware sensors (T707, ADR 0002): temperatures, fans and power draw through LibreHardwareMonitorLib and the
        // PawnIO kernel driver. The provider follows the setting hardwareSensors.enabled, which is off by default; while it is off,
        // nothing here calls IHardwareSourceFactory.Open, so neither the library nor a driver handle is loaded. Creating the
        // factory does not load the library either (see LibreHardwareSourceFactory). The agent host registers IConfigStore.
        services.TryAddSingleton<IHardwareSourceFactory, LibreHardwareSourceFactory>();
        services.TryAddSingleton<IHardwareDriverProbe, PawnIoDriverProbe>();
        services.AddSingleton(provider => new HardwareMetricProvider(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<IHardwareSourceFactory>(),
            provider.GetRequiredService<IHardwareDriverProbe>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(HardwareLogCategory)));
        services.AddSingleton<IMetricProvider>(provider => provider.GetRequiredService<HardwareMetricProvider>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<HardwareMetricProvider>());
        services.RemoveAll<IHardwareSensorStatusSource>();
        services.AddSingleton<IHardwareSensorStatusSource>(provider => provider.GetRequiredService<HardwareMetricProvider>());

        // The custom sensor types that need Windows (T307): the custom sensor provider takes them and the sensor endpoints
        // validate with them, while on other platforms they are reported as unsupported.
        services.TryAddSingleton<IPerformanceCounterReader>(provider => new PerformanceCounterReader(provider.GetService<TimeProvider>()));
        services.TryAddSingleton<ICimQueryRunner, WmiCimQueryRunner>();
        services.TryAddSingleton<IServiceStateReader, ServiceControllerStateReader>();
        services.RemoveAll<ISensorSourceHandler>();
        services.AddSingleton<ISensorSourceHandler>(provider => new PerfCounterSensorHandler(provider.GetRequiredService<IPerformanceCounterReader>()));
        services.AddSingleton<ISensorSourceHandler>(provider => new CimQuerySensorHandler(provider.GetRequiredService<ICimQueryRunner>()));
        services.AddSingleton<ISensorSourceHandler>(provider => new ServiceStateSensorHandler(provider.GetRequiredService<IServiceStateReader>()));

        // The built-in actions (T306): the power actions run here through IPowerNativeApi, every action of the desktop is
        // handed to the SessionHost by the decorator, which has its own provider (UserBuiltinActions).
        services.TryAddSingleton<IPowerNativeApi, WindowsPowerNativeApi>();

        // Keep awake (T715): one power request that the agent owns. It is given back when the agent stops and is never restored after a
        // restart; the metric power.keep_awake reports the real state and the relay publishes every change at once.
        services.TryAddSingleton<IPowerRequestApi, WindowsPowerRequestApi>();
        services.AddSingleton(provider => new KeepAwakeService(
            provider.GetRequiredService<IPowerRequestApi>(),
            provider.GetService<TimeProvider>()));
        services.AddSingleton<IMetricProvider>(provider => new KeepAwakeMetricProvider(provider.GetRequiredService<KeepAwakeService>()));
        services.AddSingleton<IHostedService>(provider => new KeepAwakeMetricRelay(
            provider.GetRequiredService<KeepAwakeService>(),
            provider.GetService<MetricEngine>() is { } engine ? engine.RequestSample : null));

        // The one pending delayed power action (T715): the countdown dialog is shown by the SessionHost of the logged-in user, the
        // event power_action_cancelled goes to the event bus that the agent host registers.
        services.TryAddSingleton<ICountdownPresenter>(provider => new SessionHostCountdownPresenter(provider.GetRequiredService<WindowsSessionBridge>()));
        services.AddSingleton(provider => new PowerActionScheduler(
            provider.GetService<TimeProvider>(),
            provider.GetRequiredService<ICountdownPresenter>(),
            provider.GetService<IEventBus>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(ActionsLogCategory)));
        services.AddSingleton(provider => new WindowsBuiltinActions(
            provider.GetRequiredService<IPowerNativeApi>(),
            provider.GetRequiredService<PowerActionScheduler>(),
            provider.GetRequiredService<KeepAwakeService>()));
        // notify.toast (T702) has its picture fetched by the agent; the fetcher is replaceable for tests.
        services.TryAddSingleton<INotificationImageFetcher>(_ => new NotificationImageFetcher());
        services.RemoveAll<IBuiltinActionProvider>();
        services.AddSingleton<IBuiltinActionProvider>(provider => new UserContextBuiltinActions(
            provider.GetRequiredService<WindowsBuiltinActions>(),
            provider.GetRequiredService<WindowsSessionBridge>(),
            provider.GetRequiredService<INotificationImageFetcher>(),
            provider.GetRequiredService<PowerActionScheduler>()));

        // Commands with runAs: user_elevated (T311, D-036).
        services.TryAddSingleton<IElevatedNativeApi, WindowsElevatedNativeApi>();
        services.RemoveAll<IElevatedLauncher>();
        services.AddSingleton<IElevatedLauncher>(provider => new ElevatedLauncher(
            provider.GetRequiredService<IElevatedNativeApi>(),
            provider.GetRequiredService<IFileSystemAcl>(),
            provider.GetService<TimeProvider>(),
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(SecurityLogCategory)));

        // The Authenticode signature of the installer that agent.update downloads (T704).
        services.RemoveAll<IAuthenticodeVerifier>();
        services.AddSingleton<IAuthenticodeVerifier, WinTrustAuthenticodeVerifier>();

        services.RemoveAll<IScriptAccessGuard>();
        services.AddSingleton<IScriptAccessGuard>(provider => new SystemScriptAclGuard(
            provider.GetRequiredService<IFileSystemAcl>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(SecurityLogCategory)));

        if (IsRunningAsService())
        {
            services.Configure<QoffeeServiceOptions>(options => options.ServiceName = serviceName);
            services.RemoveAll<IHostLifetime>();
            services.AddSingleton<IHostLifetime, QoffeeServiceLifetime>();
        }

        return services;
    }

    /// <summary>
    /// Adds the service that creates the default commands of the power and session actions on the first start (T306). It must
    /// be registered after the service that loads the configuration, because it looks at what the configuration holds: a
    /// hosted service starts in the order of registration.
    /// </summary>
    /// <param name="services">The service collection of the agent.</param>
    /// <param name="directories">Resolves the data directories of PLAN.md S05; the marker file lives in the data directory.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddWindowsDefaultCommands(
        this IServiceCollection services,
        Func<IServiceProvider, DataDirectories> directories)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directories);

        services.AddSingleton(provider => new DefaultCommandSeeder(
            provider.GetRequiredService<IConfigStore>(),
            Path.Combine(directories(provider).Data, DefaultCommandSeeder.MarkerFileName),
            provider.GetService<TimeProvider>()));
        services.AddSingleton<IHostedService>(provider => new DefaultCommandSeedService(
            provider.GetRequiredService<DefaultCommandSeeder>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(ActionsLogCategory)));
        return services;
    }
}
