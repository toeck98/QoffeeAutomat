using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Actions;
using QoffeeAutomat.Platform.Linux.Metrics.Session;
using QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;
using QoffeeAutomat.Platform.Linux.Sessions;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>
/// Registers the Linux platform (PLAN.md S04, S17, T802) in the dependency injection container of the agent and sets up the systemd
/// hosting: the notify lifetime, the system metrics from <c>/proc</c> and <c>/sys</c>, the session from logind, the power events through
/// the delay lock of logind, the facts about the computer and the agent id. Zeroconf needs nothing here: the agent's own multicast DNS
/// responder (ADR 0001) runs on Linux as it does elsewhere and shares port 5353 with <c>avahi-daemon</c> and <c>systemd-resolved</c>.
/// </summary>
public static class LinuxPlatformServices
{
    /// <summary>The category of the messages about logind: the delay lock, the power signals and the session.</summary>
    public const string LogindLogCategory = "QoffeeAutomat.Logind";

    /// <summary>The category of the messages about systemd: ready, stopping and the watchdog.</summary>
    public const string SystemdLogCategory = "QoffeeAutomat.Systemd";

    /// <summary>The category of the messages about the session helper and its socket.</summary>
    public const string SessionsLogCategory = "QoffeeAutomat.Sessions";

    /// <summary>The category of the messages about the built-in actions and their default commands.</summary>
    public const string ActionsLogCategory = "QoffeeAutomat.Actions";

    /// <summary>The category of security warnings.</summary>
    public const string SecurityLogCategory = "QoffeeAutomat.Security";

    /// <summary>Tells whether this process was started by systemd as a service.</summary>
    /// <returns>True when running as a systemd service.</returns>
    public static bool IsRunningAsService() => OperatingSystem.IsLinux() && SystemdEnvironment.IsSystemdService(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Replaces the simulated providers that the Linux platform implements: the system metrics (T801), the session (T801, T802), the power
    /// events, the platform info and the built-in actions, and, when the process runs as a systemd service, the host lifetime. The simulated
    /// platform stays selectable with <c>--platform simulated</c>, which never calls this method.
    /// </summary>
    /// <param name="services">The service collection of the agent.</param>
    /// <param name="agentIdFile">Resolves the file that keeps the agent id.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddLinuxPlatform(this IServiceCollection services, Func<IServiceProvider, string> agentIdFile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(agentIdFile);

        services.TryAddSingleton<ILinuxSystemSources>(_ => new LinuxSystemSources());
        services.AddSingleton(provider =>
        {
            ILinuxSystemSources sources = provider.GetRequiredService<ILinuxSystemSources>();
            TimeProvider time = provider.GetService<TimeProvider>() ?? TimeProvider.System;
            return new LinuxBootTime(time, sources.ReadUptime, sources.ReadKernelBootId());
        });

        // The system metrics (T801); the session metrics come through the session bridge below.
        services.AddSingleton(provider => new LinuxSystemMetricProvider(
            provider.GetRequiredService<ILinuxSystemSources>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<LinuxBootTime>()));
        services.RemoveAll<IMetricProvider>();
        services.AddSingleton<IMetricProvider>(provider => provider.GetRequiredService<LinuxSystemMetricProvider>());

        services.RemoveAll<IPlatformInfo>();
        services.AddSingleton<IPlatformInfo>(provider => new LinuxPlatformInfo(
            agentIdFile(provider),
            provider.GetRequiredService<ILinuxSystemSources>(),
            provider.GetRequiredService<LinuxBootTime>()));

        // The session as logind knows it, and the session helper of the user (T803): the bridge connects to the Unix domain socket of the
        // helper, runs the commands of runAs: user there (processes, bash scripts, desktop actions) and takes the desktop metrics from it.
        services.TryAddSingleton<ILogindSessionSource>(_ => LogindSessionSources.CreateDefault());
        services.AddSingleton(provider => new LinuxSessionMetricProvider(provider.GetRequiredService<ILogindSessionSource>()));
        services.TryAddSingleton<ICommandRunner>(_ => new ProcessCommandRunner());
        services.TryAddSingleton<ISessionHelperTransport>(provider => new UnixSocketHelperTransport(
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(SessionsLogCategory)));
        services.AddSingleton(provider => new LinuxSessionBridge(
            provider.GetRequiredService<LinuxSessionMetricProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogindLogCategory),
            provider.GetService<TimeProvider>(),
            new LinuxSessionHelperOptions(
                provider.GetRequiredService<ISessionHelperTransport>(),
                typeof(LinuxPlatformServices).Assembly.GetName().Version?.ToString() ?? "0.0.0")));
        services.RemoveAll<ISessionBridge>();
        services.AddSingleton<ISessionBridge>(provider => provider.GetRequiredService<LinuxSessionBridge>());
        services.AddSingleton<ILinuxUserSession>(provider => provider.GetRequiredService<LinuxSessionBridge>());
        services.AddSingleton<IUserContextBash>(provider => provider.GetRequiredService<LinuxSessionBridge>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LinuxSessionBridge>());

        // The built-in actions (T803): power and session through logind and systemctl, the desktop through the helper. A delayed power
        // action tells the user with a notification, and power.cancel stops it.
        services.AddSingleton(provider => new LinuxPowerScheduler(
            provider.GetService<TimeProvider>(),
            (title, message, token) => provider.GetRequiredService<ILinuxUserSession>().NotifyAsync(title, message, token),
            provider.GetService<IEventBus>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(ActionsLogCategory)));
        services.RemoveAll<IBuiltinActionProvider>();
        services.AddSingleton<IBuiltinActionProvider>(provider => new LinuxBuiltinActions(
            provider.GetRequiredService<ICommandRunner>(),
            provider.GetRequiredService<LinuxPowerScheduler>(),
            provider.GetRequiredService<ILinuxUserSession>()));

        // Windows-only action types and built-in actions are flagged as unsupported, and the command documents are kept (S17).
        services.RemoveAll<CommandPlatformSupport>();
        services.AddSingleton(provider => new CommandPlatformSupport(
            PlatformActionSupport.LinuxOs,
            () => provider.GetRequiredService<IBuiltinActionProvider>()));

        // Scripts that run with the rights of the service must not be changeable by other users (S09 item 5).
        services.RemoveAll<IScriptAccessGuard>();
        services.AddSingleton<IScriptAccessGuard>(provider => new LinuxScriptAccessGuard(
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(SecurityLogCategory)));

        services.RemoveAll<IElevatedLauncher>();
        services.AddSingleton<IElevatedLauncher, LinuxElevatedLauncher>();

        // Suspend and shutdown: bye is sent while the delay lock of logind is held (T802).
        services.TryAddSingleton<ILogindSignalSource>(_ => new BusctlLogindSignalSource());
        services.TryAddSingleton<ILogindInhibitor>(_ => new SystemdInhibitor());
        services.AddSingleton(provider => new LinuxPowerEvents(
            provider.GetRequiredService<ILogindSignalSource>(),
            provider.GetRequiredService<ILogindInhibitor>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogindLogCategory),
            provider.GetService<TimeProvider>()));
        services.RemoveAll<IPowerEvents>();
        services.AddSingleton<IPowerEvents>(provider => provider.GetRequiredService<LinuxPowerEvents>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LinuxPowerEvents>());

        if (IsRunningAsService())
        {
            services.TryAddSingleton<ISystemdNotifier>(_ => SystemdNotifier.FromEnvironment(Environment.GetEnvironmentVariable));
            services.RemoveAll<IHostLifetime>();
            services.AddSingleton<IHostLifetime>(provider => new SystemdHostLifetime(
                provider.GetRequiredService<ISystemdNotifier>(),
                provider.GetRequiredService<IHostApplicationLifetime>(),
                SystemdEnvironment.ReadWatchdogInterval(Environment.GetEnvironmentVariable, Environment.ProcessId),
                provider.GetService<TimeProvider>()));
        }

        return services;
    }

    /// <summary>
    /// Adds the service that creates the default commands of the Linux actions on the first start (T803). It must be registered after the
    /// service that loads the configuration, because it looks at what the configuration holds: a hosted service starts in the order of
    /// registration.
    /// </summary>
    /// <param name="services">The service collection of the agent.</param>
    /// <param name="dataDirectory">Resolves the data directory; the marker file lives there.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddLinuxDefaultCommands(this IServiceCollection services, Func<IServiceProvider, string> dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataDirectory);

        services.AddSingleton(provider => new LinuxDefaultCommandSeeder(
            provider.GetRequiredService<IConfigStore>(),
            Path.Combine(dataDirectory(provider), LinuxDefaultCommandSeeder.MarkerFileName),
            provider.GetService<TimeProvider>()));
        services.AddSingleton<IHostedService>(provider => new LinuxDefaultCommandSeedService(
            provider.GetRequiredService<LinuxDefaultCommandSeeder>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(ActionsLogCategory)));
        return services;
    }
}
