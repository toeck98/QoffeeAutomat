using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>The exit codes of the <c>service</c> verb; they are the codes of the agent executable (sysexits.h where one fits).</summary>
public static class LinuxServiceExitCodes
{
    /// <summary>The verb did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The operation failed.</summary>
    public const int Failure = 1;

    /// <summary>The arguments are wrong.</summary>
    public const int Usage = 64;

    /// <summary>The service is not installed, or systemd does not run on this computer.</summary>
    public const int NotInstalled = 69;

    /// <summary>The caller is not root.</summary>
    public const int NoPermission = 77;
}

/// <summary>Everything the <c>service</c> verb needs to know about the running executable.</summary>
/// <param name="Arguments">The arguments after the verb <c>service</c>: the action and its options.</param>
/// <param name="DataDirectory">The value of the global <c>--data-dir</c>, or null; an option of the action wins.</param>
/// <param name="ResolveDirectories">Maps the data directory option (null for the default) to the directories of S05.</param>
/// <param name="ExecutablePath">The program that the unit starts, normally the agent's executable; <c>dotnet</c> when the agent runs through the dotnet host.</param>
/// <param name="ExecutableArguments">Arguments that come before the verb <c>run</c>, for example the path of the agent assembly when <paramref name="ExecutablePath"/> is <c>dotnet</c>.</param>
public sealed record LinuxServiceContext(
    IReadOnlyList<string> Arguments,
    string? DataDirectory,
    Func<string?, LinuxDataDirectories> ResolveDirectories,
    string ExecutablePath,
    IReadOnlyList<string> ExecutableArguments);

/// <summary>Where the verb looks and writes; tests point it at a temporary directory.</summary>
public sealed record LinuxServiceCommandOptions
{
    /// <summary>The directory that gets the unit file; <see cref="LinuxServiceDefaults.UnitDirectory"/> by default.</summary>
    public string UnitDirectory { get; init; } = LinuxServiceDefaults.UnitDirectory;

    /// <summary>The directory that holds <c>run</c>, for the check that systemd is the init system; null or empty means <c>/</c>.</summary>
    public string? RootDirectory { get; init; }

    /// <summary>Tells whether the caller is root; null asks the operating system.</summary>
    public Func<bool>? IsPrivileged { get; init; }
}

/// <summary>
/// The verb <c>qoffeeautomat service install|uninstall|start|stop|status</c> on Linux (PLAN.md S17, T802). Install creates the data
/// directories with their permissions (S05, S09), writes the systemd unit, enables it and starts it; uninstall stops, disables and removes
/// the unit and keeps the data. Start, stop and status go to <c>systemctl</c>.
/// </summary>
public sealed partial class LinuxServiceCommand
{
    /// <summary>The usage text of the verb.</summary>
    public const string Usage = """
        Usage: qoffeeautomat service <install|uninstall|start|stop|status> [options]

          install      Create the data directories with their permissions, write the systemd unit,
                       enable it and start it. Needs root.
          uninstall    Stop, disable and remove the unit. The data directories stay. Needs root.
          start        Start the service and wait until it runs.
          stop         Stop the service and wait until it has stopped.
          status       Show the state of the service.

        Options:
          --name <name>        Unit name without ".service" (default qoffeeautomat).
          --data-dir <path>    install: keep all files below this directory.
          --ui-port <port>     install: port of the loopback UI and API listener.
          --link-port <port>   install: port of the QoffeeLink listener.
          --no-start           install: enable the service but do not start it.
        """;

    private static readonly Regex ServiceNamePattern = ServiceNameRegex();

    private readonly ISystemctl _systemctl;
    private readonly LinuxDataDirectoryProvisioner _provisioner;
    private readonly LinuxServiceCommandOptions _options;
    private readonly Func<bool> _isPrivileged;

    /// <summary>Creates the command.</summary>
    /// <param name="systemctl">Controls the unit.</param>
    /// <param name="provisioner">Creates and protects the data directories.</param>
    /// <param name="options">Where to write the unit; null uses the defaults.</param>
    public LinuxServiceCommand(ISystemctl systemctl, LinuxDataDirectoryProvisioner provisioner, LinuxServiceCommandOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(systemctl);
        ArgumentNullException.ThrowIfNull(provisioner);

        _systemctl = systemctl;
        _provisioner = provisioner;
        _options = options ?? new LinuxServiceCommandOptions();
        _isPrivileged = _options.IsPrivileged ?? (() => Environment.IsPrivilegedProcess);
    }

    /// <summary>Runs the verb.</summary>
    /// <param name="context">The arguments and the executable.</param>
    /// <param name="output">Receives the normal output.</param>
    /// <param name="error">Receives error messages.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A <see cref="LinuxServiceExitCodes"/> value.</returns>
    public async Task<int> RunAsync(LinuxServiceContext context, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(context, out ServiceRequest? request, out string? problem))
        {
            await error.WriteLineAsync(problem + " Try 'service --help'.").ConfigureAwait(false);
            return LinuxServiceExitCodes.Usage;
        }

        if (request.Action == "help")
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return LinuxServiceExitCodes.Success;
        }

        try
        {
            return request.Action switch
            {
                "install" => await InstallAsync(context, request, output, error, cancellationToken).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(request, output, error, cancellationToken).ConfigureAwait(false),
                "start" => await ControlAsync(request, start: true, output, error, cancellationToken).ConfigureAwait(false),
                "stop" => await ControlAsync(request, start: false, output, error, cancellationToken).ConfigureAwait(false),
                _ => await StatusAsync(request, output, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (UnauthorizedAccessException exception)
        {
            await error.WriteLineAsync("Access denied: " + exception.Message + " Run this command with sudo.").ConfigureAwait(false);
            return LinuxServiceExitCodes.NoPermission;
        }
        catch (Exception exception) when (exception is SystemctlException or LinuxDataDirectorySecurityException or IOException or ArgumentException or InvalidOperationException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return LinuxServiceExitCodes.Failure;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRegex();

    private static bool TryParse(
        LinuxServiceContext context,
        [NotNullWhen(true)] out ServiceRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        request = null;
        problem = null;
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0)
        {
            problem = "service needs an action: install, uninstall, start, stop or status.";
            return false;
        }

        string action = args[0].ToLowerInvariant();
        if (action is "--help" or "-h" or "help")
        {
            request = new ServiceRequest("help", LinuxServiceDefaults.Name, null, null, null, Start: true);
            return true;
        }

        if (action is not ("install" or "uninstall" or "start" or "stop" or "status"))
        {
            problem = $"Unknown service action '{args[0]}'.";
            return false;
        }

        string name = LinuxServiceDefaults.Name;
        string? dataDirectory = context.DataDirectory;
        int? uiPort = null;
        int? linkPort = null;
        bool start = true;
        for (int index = 1; index < args.Count; index++)
        {
            string option = args[index];
            string? value = null;
            int equals = option.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                value = option[(equals + 1)..];
                option = option[..equals];
            }

            if (option == "--no-start" && value is null)
            {
                start = false;
                continue;
            }

            if (option is not ("--name" or "--data-dir" or "--ui-port" or "--link-port"))
            {
                problem = $"Unknown option '{args[index]}'.";
                return false;
            }

            if (value is null)
            {
                if (index + 1 >= args.Count)
                {
                    problem = $"{option} needs a value.";
                    return false;
                }

                value = args[++index];
            }

            switch (option)
            {
                case "--name":
                    if (!ServiceNamePattern.IsMatch(value))
                    {
                        problem = "--name must start with a lower-case letter and may contain lower-case letters, digits, '_' and '-'.";
                        return false;
                    }

                    name = value;
                    break;
                case "--data-dir":
                    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                    {
                        problem = "--data-dir needs a directory without control characters.";
                        return false;
                    }

                    dataDirectory = value;
                    break;
                default:
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port > 65535)
                    {
                        problem = $"{option} must be a number between 0 and 65535.";
                        return false;
                    }

                    if (option == "--ui-port")
                    {
                        uiPort = port;
                    }
                    else
                    {
                        linkPort = port;
                    }

                    break;
            }
        }

        request = new ServiceRequest(action, name, dataDirectory, uiPort, linkPort, start);
        return true;
    }

    private string UnitPath(string name) => Path.Combine(_options.UnitDirectory, LinuxServiceDefaults.UnitName(name));

    private async Task<int> InstallAsync(LinuxServiceContext context, ServiceRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!_isPrivileged())
        {
            await error.WriteLineAsync("service install needs root. Run it with sudo.").ConfigureAwait(false);
            return LinuxServiceExitCodes.NoPermission;
        }

        if (!SystemdEnvironment.IsSystemdBooted(_options.RootDirectory))
        {
            await error.WriteLineAsync("systemd is not the init system of this computer, so the service cannot be installed. Run 'qoffeeautomat run' instead.").ConfigureAwait(false);
            return LinuxServiceExitCodes.NotInstalled;
        }

        string unit = LinuxServiceDefaults.UnitName(request.Name);
        string? dataDirectory = request.DataDirectory is null ? null : Path.GetFullPath(request.DataDirectory);
        LinuxDataDirectories layout = context.ResolveDirectories(dataDirectory);
        _provisioner.Provision(layout, makeOwnedByRoot: true);

        string text = SystemdUnitFile.Render(new SystemdUnitOptions(
            context.ExecutablePath,
            context.ExecutableArguments,
            dataDirectory,
            request.UiPort,
            request.LinkPort));
        string path = UnitPath(request.Name);
        await WriteUnitAsync(path, text, cancellationToken).ConfigureAwait(false);

        bool wasActive = (await _systemctl.QueryAsync(unit, cancellationToken).ConfigureAwait(false)).IsActive;
        await _systemctl.DaemonReloadAsync(cancellationToken).ConfigureAwait(false);
        await _systemctl.EnableAsync(unit, cancellationToken).ConfigureAwait(false);

        string data = layout.Root ?? layout.Config;
        await output.WriteLineAsync($"The service '{unit}' is installed ({path}, runs as root, starts at boot). Data: {data}").ConfigureAwait(false);
        if (!request.Start)
        {
            await output.WriteLineAsync("Start it with: sudo qoffeeautomat service start").ConfigureAwait(false);
            return LinuxServiceExitCodes.Success;
        }

        if (wasActive)
        {
            await output.WriteLineAsync($"The service is running already; restart it to use the new unit: sudo systemctl restart {unit}").ConfigureAwait(false);
            return LinuxServiceExitCodes.Success;
        }

        await _systemctl.StartAsync(unit, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"The service '{unit}' is running.").ConfigureAwait(false);
        return LinuxServiceExitCodes.Success;
    }

    private async Task<int> UninstallAsync(ServiceRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!_isPrivileged())
        {
            await error.WriteLineAsync("service uninstall needs root. Run it with sudo.").ConfigureAwait(false);
            return LinuxServiceExitCodes.NoPermission;
        }

        string unit = LinuxServiceDefaults.UnitName(request.Name);
        string path = UnitPath(request.Name);
        UnitStatus status = await _systemctl.QueryAsync(unit, cancellationToken).ConfigureAwait(false);
        if (!status.IsInstalled && !File.Exists(path))
        {
            await output.WriteLineAsync($"The service '{unit}' is not installed.").ConfigureAwait(false);
            return LinuxServiceExitCodes.Success;
        }

        if (status.IsInstalled)
        {
            if (status.ActiveState is "active" or "activating" or "deactivating" or "failed")
            {
                await _systemctl.StopAsync(unit, cancellationToken).ConfigureAwait(false);
            }

            await _systemctl.DisableAsync(unit, cancellationToken).ConfigureAwait(false);
        }

        File.Delete(path);
        await _systemctl.DaemonReloadAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"The service '{unit}' is removed. The data directories were kept.").ConfigureAwait(false);
        return LinuxServiceExitCodes.Success;
    }

    private async Task<int> ControlAsync(ServiceRequest request, bool start, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        string unit = LinuxServiceDefaults.UnitName(request.Name);
        if (!(await _systemctl.QueryAsync(unit, cancellationToken).ConfigureAwait(false)).IsInstalled)
        {
            await error.WriteLineAsync($"The service '{unit}' is not installed. Run 'service install' first.").ConfigureAwait(false);
            return LinuxServiceExitCodes.NotInstalled;
        }

        if (start)
        {
            await _systemctl.StartAsync(unit, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"The service '{unit}' is running.").ConfigureAwait(false);
        }
        else
        {
            await _systemctl.StopAsync(unit, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"The service '{unit}' is stopped.").ConfigureAwait(false);
        }

        return LinuxServiceExitCodes.Success;
    }

    private async Task<int> StatusAsync(ServiceRequest request, TextWriter output, CancellationToken cancellationToken)
    {
        string unit = LinuxServiceDefaults.UnitName(request.Name);
        UnitStatus status = await _systemctl.QueryAsync(unit, cancellationToken).ConfigureAwait(false);
        if (!status.IsInstalled)
        {
            await output.WriteLineAsync($"The service '{unit}' is not installed.").ConfigureAwait(false);
            return LinuxServiceExitCodes.NotInstalled;
        }

        string startType = status.UnitFileState.Length == 0 ? "unknown" : status.UnitFileState;
        await output.WriteLineAsync($"Service:    {status.Name}").ConfigureAwait(false);
        await output.WriteLineAsync($"State:      {status.ActiveState} ({status.SubState})").ConfigureAwait(false);
        await output.WriteLineAsync($"Start type: {startType}").ConfigureAwait(false);
        await output.WriteLineAsync($"Main PID:   {(status.MainPid > 0 ? status.MainPid.ToString(CultureInfo.InvariantCulture) : "none")}").ConfigureAwait(false);
        await output.WriteLineAsync($"Unit file:  {(status.FragmentPath.Length == 0 ? "unknown" : status.FragmentPath)}").ConfigureAwait(false);
        return LinuxServiceExitCodes.Success;
    }

    private static async Task WriteUnitAsync(string path, string text, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The file appears completely or not at all, and is readable for everybody like the unit files of packages.
        string temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private sealed record ServiceRequest(string Action, string Name, string? DataDirectory, int? UiPort, int? LinkPort, bool Start);
}
