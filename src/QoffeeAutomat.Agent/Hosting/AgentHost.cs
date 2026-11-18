using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Api.Prometheus;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Agent.Security;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Agent.Updates;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Secrets.Named;
using QoffeeAutomat.Core.Templates;
using QoffeeAutomat.Core.Updates;
using QoffeeAutomat.Platform.Simulated;
using LinkOptions = QoffeeAutomat.Agent.Link.LinkOptions;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// Builds the agent host on the ASP.NET Core minimal host (PLAN.md S04, T201): data directories,
/// logging to console and rolling files, the two Kestrel listeners of D-018, <c>/healthz</c>, graceful
/// shutdown, the config and secret stores and the platform implementation. Everything that depends on
/// the data directory is resolved lazily from <see cref="IAppPaths"/>, so a test host can replace the
/// paths through the service collection.
/// </summary>
public static class AgentHost
{
    /// <summary>The unauthenticated liveness endpoint (PLAN.md S08).</summary>
    public const string HealthPath = "/healthz";

    /// <summary>The time the host waits for work to finish after a stop request.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The name of the file in the data directory that keeps the agent id.</summary>
    internal const string AgentIdFileName = "agent-id.txt";

    /// <summary>The environment variable that points the template gallery at another directory.</summary>
    internal const string TemplatesDirEnvironmentVariable = "QOFFEEAUTOMAT_TEMPLATES_DIR";

    private const string SecretKeyFileName = "secrets.key";
    private const string SecretsFileName = "secrets.json";
    private const string EventsDatabaseFileName = "events.db";
    private const string RunsDatabaseFileName = "runs.db";
    private const string StateValuesFileName = "state-values.json";

    /// <summary>Builds the host without starting it.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="configureServices">Hook for tests and later tasks to add or replace services; runs after the agent's own registrations.</param>
    /// <returns>The application, ready for <c>RunAsync</c>.</returns>
    /// <exception cref="AgentStartupException">The requested platform cannot be used.</exception>
    public static WebApplication Build(AgentOptions options, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // --platform wins over QOFFEE_PLATFORM, which keeps the simulated platform selectable for tests and the end-to-end stack (D-021).
        PlatformSelection platform = PlatformSelector.Select(
            options.Platform ?? Environment.GetEnvironmentVariable(LinuxHostIntegration.PlatformEnvironmentVariable),
            PlatformSelector.CurrentOs());
        if (!platform.IsValid)
        {
            throw new AgentStartupException(platform.Error!);
        }

        // Windows (T301): the data directories must be protected before anything is written to them, including the
        // log files that the first logger creates.
        WindowsHostIntegration.PrepareDirectories(options, platform);

        // Linux (T802): the same for /etc/qoffeeautomat, /var/lib/qoffeeautomat and /var/log/qoffeeautomat, with modes 0700 and 0600.
        LinuxHostIntegration.PrepareDirectories(options, platform);

        // Options the agent does not know (--environment, --contentRoot and so on) belong to ASP.NET Core.
        string[] hostArguments = options.PassThrough is null ? [] : [.. options.PassThrough];
        WebApplicationBuilder builder = LinuxHostIntegration.CreateBuilder(hostArguments, platform) ?? WindowsHostIntegration.CreateBuilder(hostArguments);
        ConfigureLogging(builder.Logging, journald: LinuxHostIntegration.IsSystemdService(platform));
        WindowsHostIntegration.ConfigureLogging(builder.Logging, platform);
        ConfigureServices(builder.Services, options, platform);
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.MapGet(HealthPath, () => Results.Text(
            $"{{\"status\":\"ok\",\"version\":\"{AgentVersion.Current}\"}}",
            "application/json"));
        LocalChannelEndpoints.Map(app);

        // Security (T211, T212): the management API stays on this PC unless remote UI is on, every route
        // below /api/v1 needs a session except the documented ones, and the pairing endpoint of QoffeeLink
        // needs only a one-time code.
        //
        // The Prometheus export (T709) answers GET /metrics on the HTTPS listener with its own read-only token. It sits in front
        // of the gate because the export is meant for another PC; it passes on everything else, and the UI page /metrics too.
        app.UseMiddleware<PrometheusMiddleware>();
        app.UseMiddleware<RemoteAccessGate>();

        // The web UI (T401): static files behind the gate, so other PCs get them only while remote UI is on.
        UiHosting.UseFiles(app);
        app.UseAuthentication();
        app.UseAuthorization();
        ManagementApi.MapEndpoints(app);
        PairingEndpoints.MapLink(app);

        // QoffeeLink (T210): the WebSocket endpoint authenticates the bearer token itself, before the upgrade.
        app.UseWebSockets(LinkEndpoint.CreateWebSocketOptions(app.Services.GetRequiredService<LinkOptions>()));
        LinkEndpoint.Map(app);

        // Client routes of the single-page UI answer with index.html; mapped last, it has the lowest order and never
        // shadows /api, /link or /healthz.
        UiHosting.MapFallback(app);
        return app;
    }

    private static void ConfigureLogging(ILoggingBuilder logging, bool journald)
    {
        logging.ClearProviders();
        if (journald)
        {
            // Under systemd the journal adds the time and the unit, and reads the priority from the prefix "<N>" of a line (T802).
            logging.AddSystemdConsole(console =>
            {
                console.IncludeScopes = false;
                console.TimestampFormat = null;
            });
            return;
        }

        logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
    }

    private static void ConfigureServices(IServiceCollection services, AgentOptions options, PlatformSelection platform)
    {
        services.AddSingleton(options);
        services.AddSingleton(platform);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAppPaths>(_ => AppPaths.ForCurrentProcess(options.DataDir));

        services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);
        services.AddSingleton<IConfigureOptions<KestrelServerOptions>, KestrelListenerSetup>();
        services.AddSingleton<IConfigureOptions<LoggerFilterOptions>, SettingsLogLevelSetup>();
        services.AddSingleton<ILoggerProvider>(provider => new RollingFileLoggerProvider(
            provider.GetRequiredService<IAppPaths>().LogsDirectory,
            provider.GetRequiredService<TimeProvider>()));

        AddStores(services);
        services.AddAgentSecurity();
        AddSimulatedPlatform(services);
        WindowsHostIntegration.ConfigureServices(services, platform);
        LinuxHostIntegration.ConfigureServices(services, platform);

        // The update check and the built-in action agent.update (T704). After the platform: the action wraps its built-in actions.
        services.AddAgentUpdates(platform);
        AddEngines(services);

        // The management API (T213, T214): the uptime for /status and the published OpenAPI document.
        services.AddSingleton<AgentUptime>();
        services.AddHostedService(provider => provider.GetRequiredService<AgentUptime>());
        services.AddSingleton<OpenApiDocumentProvider>();
        services.TryAddSingleton<IAgentRestarter, HostAgentRestarter>();

        services.AddHostedService<ConfigLoadService>();
        WindowsHostIntegration.ConfigureServicesAfterConfigLoad(services, platform);
        LinuxHostIntegration.ConfigureServicesAfterConfigLoad(services, platform);
        services.AddHostedService<EngineStartService>();
        services.AddHostedService<LocalChannelService>();
        services.AddHostedService<AgentLifecycleService>();

        // QoffeeLink and the zeroconf advertisement (T210, T216). They stop first, before the web server (see
        // LinkLifecycleService), and the advertisement is withdrawn before the clients are told to leave.
        services.AddQoffeeLink();
        services.AddZeroconf();
    }

    private static void AddEngines(IServiceCollection services)
    {
        // Events (T217): the bus stores catalogued events in data/events.db.
        services.AddSingleton(provider => new SqliteEventStore(
            Path.Combine(provider.GetRequiredService<IAppPaths>().DataDirectory, EventsDatabaseFileName),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IEventStore>(provider => provider.GetRequiredService<SqliteEventStore>());
        services.AddSingleton(provider =>
        {
            var bus = new EventBus(provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IEventStore>());
            ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("QoffeeAutomat.Events");
            bus.StoreFailed += (_, failure) => AgentLog.EventStoreFailed(logger, failure.Exception.Message);
            return bus;
        });
        services.AddSingleton<IEventBus>(provider => provider.GetRequiredService<EventBus>());
        services.AddSingleton(provider => new PlatformEventPublisher(
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<ISessionBridge>(),
            provider.GetRequiredService<IPowerEvents>()));
        services.AddSingleton(_ => new LocalChannel());

        // Commands (T207): registry, run history in data/runs.db and the engine.
        services.AddSingleton(provider =>
        {
            IAppPaths paths = provider.GetRequiredService<IAppPaths>();
            int? days = AgentSettingsLoader.LoadOrDefault(paths, out _).Retention?.RunHistoryDays;
            return new SqliteRunHistory(
                Path.Combine(paths.DataDirectory, RunsDatabaseFileName),
                provider.GetRequiredService<TimeProvider>(),
                days is { } retained ? new RunHistoryOptions { MaxAge = TimeSpan.FromDays(retained) } : null);
        });
        services.AddSingleton<IRunHistory>(provider => provider.GetRequiredService<SqliteRunHistory>());
        services.AddSingleton(provider => new CommandRegistry(provider.GetRequiredService<IConfigStore>()));
        services.AddSingleton<AgentPause>();
        services.AddSingleton<IAgentPause>(provider => provider.GetRequiredService<AgentPause>());

        // What the tray icon of the SessionHost shows (T310); only the Windows platform sends it anywhere.
        services.AddSingleton<ITrayStatusSource, AgentTrayStatusSource>();

        // The status of the global hotkeys (T708); the Windows platform replaces this with the real one.
        services.TryAddSingleton<IHotkeyStatusSource>(NoHotkeyStatusSource.Instance);

        // The status of the optional hardware sensors (T707); the Windows platform replaces this with the real one.
        services.TryAddSingleton<IHardwareSensorStatusSource>(NoHardwareSensorStatusSource.Instance);

        // The live output of the runs that have not finished (T214, GET /runs/{id}/stream).
        services.AddSingleton(new RunOutputHub());
        services.AddSingleton<IProcessRunner>(provider => new ProcessRunner(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IProcessLauncher>(provider => new RoutingProcessLauncher(
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<ISessionBridge>(),
            provider.GetRequiredService<IElevatedLauncher>()));
        services.AddSingleton<IActionHandler>(new ProcessActionHandler());
        services.AddSingleton<IPowerShellEngineLocator, PowerShellEngineLocator>();
        services.AddSingleton(provider => new PowerShellRunner(
            provider.GetRequiredService<IPowerShellEngineLocator>(),
            new PowerShellOptions
            {
                ScriptsDirectory = provider.GetRequiredService<IAppPaths>().ScriptsDirectory,

                // The per-run files (wrapper, inline code, parameter file with the variable values) go below the data
                // directory, which only SYSTEM and Administrators can read (S05, S09), not into the temp directory of
                // LocalSystem (security review T606).
                TempRoot = Path.Combine(provider.GetRequiredService<IAppPaths>().DataDirectory, "tmp"),

                // Registered by the Windows platform (T301): refuses system scripts that standard users can change.
                ScriptGuard = provider.GetService<IScriptAccessGuard>(),

                // Registered by the Windows platform (T302/T303): PowerShell of runAs user runs inside the user's session.
                UserSession = provider.GetService<IUserContextPowerShell>(),
            },
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IActionHandler>(provider => new PowerShellActionHandler(provider.GetRequiredService<PowerShellRunner>()));

        // The action type bash (T803, PLAN.md S17): values only as environment variables and positional arguments, never in the script.
        // The handler is registered everywhere; CommandPlatformSupport flags the platforms that cannot run it (Windows).
        services.AddSingleton<IBashLocator, BashLocator>();
        services.AddSingleton(provider => new BashRunner(
            provider.GetRequiredService<IBashLocator>(),
            new BashOptions
            {
                ScriptsDirectory = provider.GetRequiredService<IAppPaths>().ScriptsDirectory,
                TempRoot = Path.Combine(provider.GetRequiredService<IAppPaths>().DataDirectory, "tmp"),

                // Registered by the Linux platform: refuses system scripts that other users can change (S09 item 5).
                ScriptGuard = provider.GetService<IScriptAccessGuard>(),

                // Registered by the Linux platform: bash of runAs user runs inside the session helper of the user.
                UserSession = provider.GetService<IUserContextBash>(),
            },
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IActionHandler>(provider => new BashActionHandler(provider.GetRequiredService<BashRunner>()));

        // What the platform can run (T803); the Linux platform registers its own before this line.
        services.TryAddSingleton(CommandPlatformSupport.Unrestricted);
        services.AddSingleton<IActionHandler>(provider => new BuiltinActionHandler(
            provider.GetRequiredService<IBuiltinActionProvider>(),
            provider.GetRequiredService<ISessionBridge>()));
        services.AddSingleton(provider =>
        {
            IConfigStore config = provider.GetRequiredService<IConfigStore>();
            var engine = new CommandEngine(
                provider.GetRequiredService<CommandRegistry>(),
                provider.GetServices<IActionHandler>(),
                provider.GetRequiredService<IProcessLauncher>(),
                provider.GetRequiredService<IRunHistory>(),
                provider.GetRequiredService<IEventBus>(),
                provider.GetRequiredService<TimeProvider>(),
                new CommandEngineOptions
                {
                    AgentName = () => config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.Name ?? Environment.MachineName,
                    LocalChannel = provider.GetRequiredService<LocalChannel>(),
                    RunOutput = provider.GetRequiredService<RunOutputHub>(),
                    PlatformSupport = provider.GetRequiredService<CommandPlatformSupport>(),
                },
                provider.GetRequiredService<IAgentPause>(),
                provider.GetRequiredService<ISessionBridge>(),
                provider.GetRequiredService<INamedSecretStore>());
            ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("QoffeeAutomat.Commands");
            engine.Faulted += (_, fault) => AgentLog.CommandEngineFault(logger, fault.RunId, fault.Stage, fault.Exception.Message);
            return engine;
        });

        // Metrics, custom sensors and the manifest (T205, T215, T209). The engine samples only what a
        // subscriber asks for (D-007), so starting it costs nothing until the first client subscribes.
        services.AddSingleton(provider => new AgentIdentity(
            provider.GetRequiredService<IPlatformInfo>(),
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<AgentOptions>()));
        // The values of the sensors of type state (T714) are kept in data/state-values.json when the sensor asks for persist.
        services.AddSingleton(provider => new StateSensorStore(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<TimeProvider>(),
            Path.Combine(provider.GetRequiredService<IAppPaths>().DataDirectory, StateValuesFileName)));
        services.AddSingleton(provider => new CustomSensorProvider(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<PowerShellRunner>(),
            provider.GetRequiredService<IProcessLauncher>(),
            provider.GetRequiredService<TimeProvider>(),
            handlers: provider.GetServices<ISensorSourceHandler>(),
            states: provider.GetRequiredService<StateSensorStore>()));
        // The maintenance mode (the pause flag) as the metric agent.paused (T705); the switch is the system command agent_maintenance.
        // The installed and the newest version as the metrics agent.version and agent.latest_version (T704), which the update entity follows.
        services.AddSingleton<IMetricProvider>(provider => new AgentMetricProvider(
            provider.GetRequiredService<IAgentPause>(),
            provider.GetRequiredService<IUpdateStatusSource>()));
        services.AddSingleton(provider =>
        {
            var engine = new MetricEngine(
                provider.GetRequiredService<IConfigStore>(),
                provider.GetServices<IMetricProvider>(),
                provider.GetRequiredService<TimeProvider>(),
                session: provider.GetRequiredService<ISessionBridge>(),
                customSensors: provider.GetRequiredService<CustomSensorProvider>());
            provider.GetRequiredService<AgentPause>().Changed += (_, _) => engine.RequestSample(AgentMetricIds.Paused);

            // A release that the check finds is published at once (T704): the metrics are event-driven.
            provider.GetRequiredService<IUpdateStatusSource>().Changed += (_, _) =>
            {
                foreach (string metricId in AgentMetricIds.Updates)
                {
                    engine.RequestSample(metricId);
                }
            };

            // A value that a script, the UI or the API sets is published at once: the sensor is event-driven (T714).
            provider.GetRequiredService<StateSensorStore>().Changed += (_, change) => engine.RequestSample(change.MetricId);
            ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("QoffeeAutomat.Metrics");
            engine.ProviderFailed += (_, failure) => AgentLog.MetricProviderFailed(logger, failure.MetricIds.Count, failure.Exception.Message, failure.RetryIn.TotalSeconds);
            return engine;
        });
        // The optional Prometheus export (T709): a scrape is a subscriber of the metric engine with a keep-alive window, and the
        // read-only token that it needs is kept as a hash in the secret store.
        services.AddSingleton(provider => new PrometheusTokenStore(provider.GetRequiredService<ISecretStore>()));
        services.AddSingleton(provider => new PrometheusExporter(
            provider.GetRequiredService<MetricEngine>(),
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<AgentIdentity>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<PrometheusOptions>()));
        services.AddSingleton(provider => new ManifestService(
            provider.GetRequiredService<CommandRegistry>(),
            provider.GetRequiredService<MetricEngine>(),
            provider.GetRequiredService<AgentIdentity>().Current,
            // The features follow the built-in actions of the platform: notify.toast announces notify (T702), media.control
            // announces media (T716); the simulated platform has neither, so it announces nothing.
            features: () => AgentFeatures.FromActions(provider.GetRequiredService<IBuiltinActionProvider>()),
            config: provider.GetRequiredService<IConfigStore>(),
            includeSystemCommands: true,
            platformSupport: provider.GetRequiredService<CommandPlatformSupport>()));
        services.AddSingleton<IManifestSource>(provider => provider.GetRequiredService<ManifestService>());
    }

    private static void AddStores(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            IAppPaths paths = provider.GetRequiredService<IAppPaths>();
            ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("QoffeeAutomat.Config");
            return new ConfigStore(
                new ConfigStoreOptions
                {
                    ConfigDirectory = paths.ConfigDirectory,
                    ScriptsDirectory = paths.ScriptsDirectory,
                    ProblemReporter = problem => AgentLog.ConfigProblem(logger, problem.Path, problem.Code, problem.Message),
                },
                provider.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<IConfigStore>(provider => provider.GetRequiredService<ConfigStore>());

        // The template gallery (T701): the templates ship next to the executable and are read once, on first use.
        services.AddSingleton(_ => new TemplateCatalogSource(ResolveTemplatesDirectory()));
        services.AddSingleton(provider => new TemplateInstantiator(
            provider.GetRequiredService<TemplateCatalogSource>(),
            provider.GetRequiredService<IConfigStore>()));

        // The file key serves the simulated platform and Linux; the Windows platform replaces it by DPAPI (T301).
        services.AddSingleton<ISecretProtector>(provider =>
            new FileKeyProtector(Path.Combine(provider.GetRequiredService<IAppPaths>().SecretsDirectory, SecretKeyFileName)));
        services.AddSingleton<ISecretStore>(provider =>
            new SecretStore(
                Path.Combine(provider.GetRequiredService<IAppPaths>().SecretsDirectory, SecretsFileName),
                provider.GetRequiredService<ISecretProtector>()));

        // The named secrets of scripts (T706) live in the same file, under script.<name>.
        services.AddSingleton<INamedSecretStore>(provider => new NamedSecretStore(provider.GetRequiredService<ISecretStore>()));
    }

    /// <summary>
    /// The directory of the shipped templates: <c>QOFFEEAUTOMAT_TEMPLATES_DIR</c> when set, otherwise the folder
    /// <c>templates</c> next to the executable (the build and the publish copy <c>templates/</c> of the repository there).
    /// </summary>
    internal static string ResolveTemplatesDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(TemplatesDirEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "templates")
            : Path.GetFullPath(configured);
    }

    private static void AddSimulatedPlatform(IServiceCollection services)
    {
        services.AddSingleton(provider => new SimulatedPlatform(
            provider.GetRequiredService<TimeProvider>(),
            new SimulatedPlatformOptions(AgentIdFile: Path.Combine(provider.GetRequiredService<IAppPaths>().DataDirectory, AgentIdFileName))));
        services.AddSingleton<IMetricProvider>(provider => provider.GetRequiredService<SimulatedPlatform>().Metrics);
        services.AddSingleton<IBuiltinActionProvider>(provider => provider.GetRequiredService<SimulatedPlatform>().Actions);
        services.AddSingleton<ISessionBridge>(provider => provider.GetRequiredService<SimulatedPlatform>().Session);
        services.AddSingleton<IPowerEvents>(provider => provider.GetRequiredService<SimulatedPlatform>().Power);
        services.AddSingleton<IServiceAdvertiser>(provider => provider.GetRequiredService<SimulatedPlatform>().Advertiser);
        services.AddSingleton<IElevatedLauncher>(provider => provider.GetRequiredService<SimulatedPlatform>().ElevatedLauncher);
        services.AddSingleton<IPlatformInfo>(provider => provider.GetRequiredService<SimulatedPlatform>().Info);
    }
}
