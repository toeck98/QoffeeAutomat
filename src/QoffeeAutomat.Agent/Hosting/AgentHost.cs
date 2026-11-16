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
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Agent.Security;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;
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

    private const string SecretKeyFileName = "secrets.key";
    private const string SecretsFileName = "secrets.json";
    private const string EventsDatabaseFileName = "events.db";
    private const string RunsDatabaseFileName = "runs.db";

    /// <summary>Builds the host without starting it.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="configureServices">Hook for tests and later tasks to add or replace services; runs after the agent's own registrations.</param>
    /// <returns>The application, ready for <c>RunAsync</c>.</returns>
    /// <exception cref="AgentStartupException">The requested platform cannot be used.</exception>
    public static WebApplication Build(AgentOptions options, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        PlatformSelection platform = PlatformSelector.Select(options.Platform, PlatformSelector.CurrentOs());
        if (!platform.IsValid)
        {
            throw new AgentStartupException(platform.Error!);
        }

        // Windows (T301): the data directories must be protected before anything is written to them, including the
        // log files that the first logger creates.
        WindowsHostIntegration.PrepareDirectories(options, platform);

        // Options the agent does not know (--environment, --contentRoot and so on) belong to ASP.NET Core.
        string[] hostArguments = options.PassThrough is null ? [] : [.. options.PassThrough];
        WebApplicationBuilder builder = WindowsHostIntegration.CreateBuilder(hostArguments);
        ConfigureLogging(builder.Logging);
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
        app.UseMiddleware<RemoteAccessGate>();
        app.UseAuthentication();
        app.UseAuthorization();
        ManagementApi.MapEndpoints(app);
        PairingEndpoints.MapLink(app);

        // QoffeeLink (T210): the WebSocket endpoint authenticates the bearer token itself, before the upgrade.
        app.UseWebSockets(LinkEndpoint.CreateWebSocketOptions(app.Services.GetRequiredService<LinkOptions>()));
        LinkEndpoint.Map(app);
        return app;
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
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
        AddEngines(services);

        // The management API (T213, T214): the uptime for /status and the published OpenAPI document.
        services.AddSingleton<AgentUptime>();
        services.AddHostedService(provider => provider.GetRequiredService<AgentUptime>());
        services.AddSingleton<OpenApiDocumentProvider>();

        services.AddHostedService<ConfigLoadService>();
        WindowsHostIntegration.ConfigureServicesAfterConfigLoad(services, platform);
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

                // Registered by the Windows platform (T301): refuses system scripts that standard users can change.
                ScriptGuard = provider.GetService<IScriptAccessGuard>(),

                // Registered by the Windows platform (T302/T303): PowerShell of runAs user runs inside the user's session.
                UserSession = provider.GetService<IUserContextPowerShell>(),
            },
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IActionHandler>(provider => new PowerShellActionHandler(provider.GetRequiredService<PowerShellRunner>()));
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
                },
                provider.GetRequiredService<IAgentPause>());
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
        services.AddSingleton(provider => new CustomSensorProvider(
            provider.GetRequiredService<IConfigStore>(),
            provider.GetRequiredService<PowerShellRunner>(),
            provider.GetRequiredService<IProcessLauncher>(),
            provider.GetRequiredService<TimeProvider>(),
            handlers: provider.GetServices<ISensorSourceHandler>()));
        services.AddSingleton(provider =>
        {
            var engine = new MetricEngine(
                provider.GetRequiredService<IConfigStore>(),
                provider.GetServices<IMetricProvider>(),
                provider.GetRequiredService<TimeProvider>(),
                session: provider.GetRequiredService<ISessionBridge>(),
                customSensors: provider.GetRequiredService<CustomSensorProvider>());
            ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("QoffeeAutomat.Metrics");
            engine.ProviderFailed += (_, failure) => AgentLog.MetricProviderFailed(logger, failure.MetricIds.Count, failure.Exception.Message, failure.RetryIn.TotalSeconds);
            return engine;
        });
        services.AddSingleton(provider => new ManifestService(
            provider.GetRequiredService<CommandRegistry>(),
            provider.GetRequiredService<MetricEngine>(),
            provider.GetRequiredService<AgentIdentity>().Current,
            features: null,
            config: provider.GetRequiredService<IConfigStore>()));
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

        // The file key serves the simulated platform and Linux; the Windows platform replaces it by DPAPI (T301).
        services.AddSingleton<ISecretProtector>(provider =>
            new FileKeyProtector(Path.Combine(provider.GetRequiredService<IAppPaths>().SecretsDirectory, SecretKeyFileName)));
        services.AddSingleton<ISecretStore>(provider =>
            new SecretStore(
                Path.Combine(provider.GetRequiredService<IAppPaths>().SecretsDirectory, SecretsFileName),
                provider.GetRequiredService<ISecretProtector>()));
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
