using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if QOFFEE_WINDOWS
using System.Reflection;
using QoffeeAutomat.Platform.Windows.Security;
using QoffeeAutomat.Platform.Windows.Service;
#endif

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// The one place where the agent touches <c>QoffeeAutomat.Platform.Windows</c> (PLAN.md S04, T301). The Windows platform
/// project targets <c>net10.0-windows</c>, which a <c>net10.0</c> project cannot reference, so the agent is built
/// for both frameworks: the <c>net10.0</c> build (tests, Linux, development) has no Windows code and every member
/// here does nothing; the <c>net10.0-windows</c> build (symbol <c>QOFFEE_WINDOWS</c>, what the installer ships) has
/// the Windows platform, the <c>service</c> verb and the service hosting.
/// </summary>
internal static class WindowsHostIntegration
{
    /// <summary>True in the Windows build, which contains the Windows platform.</summary>
#if QOFFEE_WINDOWS
    public static bool IsAvailable => true;
#else
    public static bool IsAvailable => false;
#endif

    /// <summary>
    /// Creates the web application builder. A service starts with the system directory as its working directory, so
    /// the content root is the directory of the executable.
    /// </summary>
    /// <param name="arguments">The arguments that belong to ASP.NET Core.</param>
    /// <returns>The builder.</returns>
    public static WebApplicationBuilder CreateBuilder(string[] arguments)
    {
#if QOFFEE_WINDOWS
        if (WindowsPlatformServices.IsRunningAsService())
        {
            return WebApplication.CreateBuilder(new WebApplicationOptions { Args = arguments, ContentRootPath = AppContext.BaseDirectory });
        }
#endif
        return WebApplication.CreateBuilder(arguments);
    }

    /// <summary>Makes sure that the data directories exist and are protected as PLAN.md S05 and S09 demand.</summary>
    /// <param name="options">The command line, for <c>--data-dir</c>.</param>
    /// <param name="platform">The selected platform; nothing happens unless it is Windows.</param>
    /// <exception cref="AgentStartupException">A directory is missing or can be changed by standard users.</exception>
    public static void PrepareDirectories(AgentOptions options, PlatformSelection platform)
    {
#if QOFFEE_WINDOWS
        if (platform.Kind != PlatformKind.Windows)
        {
            return;
        }

        var provisioner = new DataDirectoryProvisioner(new WindowsFileSystemAcl());
        try
        {
            provisioner.EnsureSecure(ToDirectories(AppPaths.ForCurrentProcess(options.DataDir)), mayProvision: Environment.IsPrivilegedProcess);
        }
        catch (DataDirectorySecurityException exception)
        {
            throw new AgentStartupException(exception.Message, exception);
        }
#else
        _ = options;
        _ = platform;
#endif
    }

    /// <summary>Adds the Event Log provider when the Windows platform runs as a service.</summary>
    /// <param name="logging">The logging builder.</param>
    /// <param name="platform">The selected platform.</param>
    public static void ConfigureLogging(ILoggingBuilder logging, PlatformSelection platform)
    {
#if QOFFEE_WINDOWS
        if (platform.Kind == PlatformKind.Windows)
        {
            logging.AddWindowsEventLog();
        }
#else
        _ = logging;
        _ = platform;
#endif
    }

    /// <summary>Replaces the simulated providers by the Windows ones when the Windows platform is selected.</summary>
    /// <param name="services">The service collection, with the simulated platform registered.</param>
    /// <param name="platform">The selected platform.</param>
    public static void ConfigureServices(IServiceCollection services, PlatformSelection platform)
    {
#if QOFFEE_WINDOWS
        if (platform.Kind == PlatformKind.Windows)
        {
            services.AddWindowsPlatform(provider => ToDirectories(provider.GetRequiredService<IAppPaths>()), AgentHost.AgentIdFileName);
        }
#else
        _ = services;
        _ = platform;
#endif
    }

    /// <summary>
    /// Adds the services that need the loaded configuration. Call it directly after <c>ConfigLoadService</c> is registered:
    /// hosted services start in the order of registration, and the default commands (T306) are created only when the
    /// configuration is known to be empty.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="platform">The selected platform.</param>
    public static void ConfigureServicesAfterConfigLoad(IServiceCollection services, PlatformSelection platform)
    {
#if QOFFEE_WINDOWS
        if (platform.Kind == PlatformKind.Windows)
        {
            services.AddWindowsDefaultCommands(provider => ToDirectories(provider.GetRequiredService<IAppPaths>()));
        }
#else
        _ = services;
        _ = platform;
#endif
    }

    /// <summary>Runs <c>qoffeeautomat service ...</c>.</summary>
    /// <param name="options">The parsed command line; <see cref="AgentOptions.VerbArguments"/> holds the action and its options.</param>
    /// <param name="output">Receives the normal output.</param>
    /// <param name="error">Receives error messages.</param>
    /// <returns>The exit code; <see cref="ExitCodes.Unavailable"/> when this build or this system has no service support.</returns>
    public static async Task<int> RunServiceVerbAsync(AgentOptions options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

#if QOFFEE_WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync("The 'service' verb manages a Windows service and works on Windows only.").ConfigureAwait(false);
            return ExitCodes.Unavailable;
        }

        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            await error.WriteLineAsync("The path of the running executable is not known, so the service cannot be installed.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        // Started through "dotnet agent.dll", the service has to start the same way.
        string[] leading = [];
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entryAssembly)
        {
            leading = [entryAssembly];
        }

        var command = new ServiceCommand(new ScmClient(), new DataDirectoryProvisioner(new WindowsFileSystemAcl()), new WindowsEventLogSources());
        var context = new ServiceCommandContext(
            options.VerbArguments ?? [],
            options.DataDir,
            dataDirectory => ToDirectories(AppPaths.ForCurrentProcess(dataDirectory)),
            processPath,
            leading);
        return await command.RunAsync(context, output, error).ConfigureAwait(false);
#else
        await error.WriteLineAsync("The 'service' verb is available in the Windows build of the agent only.").ConfigureAwait(false);
        return ExitCodes.Unavailable;
#endif
    }

#if QOFFEE_WINDOWS
    private static DataDirectories ToDirectories(IAppPaths paths)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(paths.ConfigDirectory)) ?? paths.ConfigDirectory;
        return new DataDirectories(root, paths.ConfigDirectory, paths.ScriptsDirectory, paths.SecretsDirectory, paths.DataDirectory, paths.LogsDirectory);
    }
#endif
}
