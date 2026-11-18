using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
#if QOFFEE_LINUX
using System.Reflection;
using QoffeeAutomat.Platform.Linux.Service;
using QoffeeAutomat.Platform.Linux.Sessions;
#endif

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// The one place where the agent touches <c>QoffeeAutomat.Platform.Linux</c> (PLAN.md S04, S17, T802), the counterpart of
/// <see cref="WindowsHostIntegration"/>. The agent is built twice: the <c>net10.0</c> build (symbol <c>QOFFEE_LINUX</c>, what Linux and
/// the tests run) has the Linux platform, the <c>service</c> verb for systemd and the systemd hosting; the <c>net10.0-windows</c> build
/// has none of it, and every member here does nothing.
/// </summary>
internal static class LinuxHostIntegration
{
    /// <summary>The environment variable that selects the platform when <c>--platform</c> is not given (<c>QOFFEE_PLATFORM=simulated</c>).</summary>
    public const string PlatformEnvironmentVariable = "QOFFEE_PLATFORM";

    /// <summary>True in the Linux build, which contains the Linux platform.</summary>
#if QOFFEE_LINUX
    public static bool IsAvailable => true;
#else
    public static bool IsAvailable => false;
#endif

    /// <summary>
    /// Tells whether the host runs as a systemd service on the Linux platform: the console then writes the format that journald understands,
    /// and the host lifetime talks to systemd.
    /// </summary>
    /// <param name="platform">The selected platform.</param>
    /// <returns>True when the Linux platform runs as a systemd unit.</returns>
    public static bool IsSystemdService(PlatformSelection platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
#if QOFFEE_LINUX
        return platform.Kind == PlatformKind.Linux && LinuxPlatformServices.IsRunningAsService();
#else
        return false;
#endif
    }

    /// <summary>
    /// Creates the web application builder for a systemd service, whose content root is the directory of the executable (the UI and the
    /// templates lie next to it). Returns null when the agent does not run as a systemd service.
    /// </summary>
    /// <param name="arguments">The arguments that belong to ASP.NET Core.</param>
    /// <param name="platform">The selected platform.</param>
    /// <returns>The builder; null when the caller should create the default one.</returns>
    public static WebApplicationBuilder? CreateBuilder(string[] arguments, PlatformSelection platform)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return IsSystemdService(platform)
            ? WebApplication.CreateBuilder(new WebApplicationOptions { Args = arguments, ContentRootPath = AppContext.BaseDirectory })
            : null;
    }

    /// <summary>Makes sure that the data directories exist and are protected as PLAN.md S05 and S09 demand.</summary>
    /// <param name="options">The command line, for <c>--data-dir</c>.</param>
    /// <param name="platform">The selected platform; nothing happens unless it is Linux.</param>
    /// <exception cref="AgentStartupException">A directory cannot be created or can be accessed by other users.</exception>
    public static void PrepareDirectories(AgentOptions options, PlatformSelection platform)
    {
#if QOFFEE_LINUX
        if (platform.Kind != PlatformKind.Linux)
        {
            return;
        }

        var provisioner = new LinuxDataDirectoryProvisioner(new UnixFileSystem());
        try
        {
            provisioner.EnsureSecure(ToDirectories(AppPaths.ForCurrentProcess(options.DataDir)), requireRootOwner: Environment.IsPrivilegedProcess);
        }
        catch (LinuxDataDirectorySecurityException exception)
        {
            throw new AgentStartupException(exception.Message, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AgentStartupException(
                "The data directories cannot be created: " + exception.Message + " Run the agent as root (the systemd service does), or choose a directory with --data-dir.",
                exception);
        }
#else
        _ = options;
        _ = platform;
#endif
    }

    /// <summary>Replaces the simulated providers by the Linux ones when the Linux platform is selected.</summary>
    /// <param name="services">The service collection, with the simulated platform registered.</param>
    /// <param name="platform">The selected platform.</param>
    public static void ConfigureServices(IServiceCollection services, PlatformSelection platform)
    {
#if QOFFEE_LINUX
        if (platform.Kind == PlatformKind.Linux)
        {
            services.AddLinuxPlatform(provider => Path.Combine(provider.GetRequiredService<IAppPaths>().DataDirectory, AgentHost.AgentIdFileName));
        }
#else
        _ = services;
        _ = platform;
#endif
    }

    /// <summary>
    /// Adds the service that creates the default commands of the Linux actions on the first start (T803). It is registered after the
    /// service that loads the configuration, because it looks at what the configuration holds.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="platform">The selected platform; nothing happens unless it is Linux.</param>
    public static void ConfigureServicesAfterConfigLoad(IServiceCollection services, PlatformSelection platform)
    {
#if QOFFEE_LINUX
        if (platform.Kind == PlatformKind.Linux)
        {
            services.AddLinuxDefaultCommands(provider => provider.GetRequiredService<IAppPaths>().DataDirectory);
        }
#else
        _ = services;
        _ = platform;
#endif
    }

    /// <summary>
    /// Runs <c>qoffeeautomat session-host</c> on Linux (T803): the session helper of the logged-in user, which the systemd user unit
    /// <c>qoffeeautomat-session.service</c> starts. It listens on a Unix domain socket in the runtime directory of the user and serves the
    /// agent until it receives SIGTERM.
    /// </summary>
    /// <param name="output">Receives the normal output.</param>
    /// <param name="error">Receives error messages.</param>
    /// <returns>The exit code; <see cref="ExitCodes.Unavailable"/> when this build or this system is not Linux.</returns>
    public static async Task<int> RunSessionHostAsync(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

#if QOFFEE_LINUX
        if (!OperatingSystem.IsLinux())
        {
            await error.WriteLineAsync("The 'session-host' verb is the session helper of Linux and works on Linux only.").ConfigureAwait(false);
            return ExitCodes.Unavailable;
        }

        _ = output;
        return await SessionHelperHost.RunAsync(AgentVersion.Current, Environment.GetEnvironmentVariable, error, CancellationToken.None).ConfigureAwait(false);
#else
        _ = output;
        await error.WriteLineAsync("The 'session-host' verb is available in the Linux build of the agent only.").ConfigureAwait(false);
        return ExitCodes.Unavailable;
#endif
    }

    /// <summary>Runs <c>qoffeeautomat service ...</c> on Linux: install, uninstall, start, stop and status of the systemd unit.</summary>
    /// <param name="options">The parsed command line; <see cref="AgentOptions.VerbArguments"/> holds the action and its options.</param>
    /// <param name="output">Receives the normal output.</param>
    /// <param name="error">Receives error messages.</param>
    /// <returns>The exit code; <see cref="ExitCodes.Unavailable"/> when this build or this system has no systemd support.</returns>
    public static async Task<int> RunServiceVerbAsync(AgentOptions options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

#if QOFFEE_LINUX
        if (!OperatingSystem.IsLinux())
        {
            await error.WriteLineAsync("The 'service' verb manages a systemd service and works on Linux only.").ConfigureAwait(false);
            return ExitCodes.Unavailable;
        }

        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            await error.WriteLineAsync("The path of the running executable is not known, so the service cannot be installed.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // Started through "dotnet agent.dll", the unit has to start the same way.
        string[] leading = [];
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.GetName().Name is { Length: > 0 } entryName)
        {
            string entryAssembly = Path.Combine(AppContext.BaseDirectory, $"{entryName}.dll");
            if (File.Exists(entryAssembly))
            {
                leading = [entryAssembly];
            }
        }

        var command = new LinuxServiceCommand(new SystemctlClient(new ProcessCommandRunner()), new LinuxDataDirectoryProvisioner(new UnixFileSystem()));
        var context = new LinuxServiceContext(
            options.VerbArguments ?? [],
            options.DataDir,
            dataDirectory => ToDirectories(AppPaths.ForCurrentProcess(dataDirectory)),
            processPath,
            leading);
        return await command.RunAsync(context, output, error).ConfigureAwait(false);
#else
        await error.WriteLineAsync("The 'service' verb for systemd is available in the Linux build of the agent only.").ConfigureAwait(false);
        return ExitCodes.Unavailable;
#endif
    }

#if QOFFEE_LINUX
    /// <summary>
    /// Maps the paths of the agent to the directories that are protected. A layout below one directory (<c>--data-dir</c>) has that
    /// directory as its root; the default layout (<c>/etc/qoffeeautomat</c>, <c>/var/lib/qoffeeautomat</c>, <c>/var/log/qoffeeautomat</c>) has no common parent,
    /// and <c>/etc</c> or <c>/var</c> must never be re-protected.
    /// </summary>
    private static LinuxDataDirectories ToDirectories(AppPaths paths)
    {
        string? configParent = Path.GetDirectoryName(Path.GetFullPath(paths.ConfigDirectory));
        string? dataParent = Path.GetDirectoryName(Path.GetFullPath(paths.DataDirectory));
        string? root = configParent is not null && string.Equals(configParent, dataParent, StringComparison.Ordinal) ? configParent : null;
        return new LinuxDataDirectories(paths.ConfigDirectory, paths.ScriptsDirectory, paths.SecretsDirectory, paths.DataDirectory, paths.LogsDirectory, root);
    }
#endif
}
