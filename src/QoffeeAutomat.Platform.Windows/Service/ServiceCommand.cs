using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>The exit codes of the <c>service</c> verb; they are the codes of the agent executable (sysexits.h where one fits).</summary>
public static class ServiceExitCodes
{
    /// <summary>The verb did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The operation failed.</summary>
    public const int Failure = 1;

    /// <summary>The arguments are wrong.</summary>
    public const int Usage = 64;

    /// <summary>The service is not installed.</summary>
    public const int NotInstalled = 69;

    /// <summary>The caller is not an administrator.</summary>
    public const int NoPermission = 77;
}

/// <summary>Everything the <c>service</c> verb needs to know about the running executable.</summary>
/// <param name="Arguments">The arguments after the verb <c>service</c>: the action and its options.</param>
/// <param name="DataDirectory">The value of the global <c>--data-dir</c>, or null; an option of the action wins.</param>
/// <param name="ResolveDirectories">Maps the data directory option (null for the default) to the directories of S05.</param>
/// <param name="ExecutablePath">The program that the service starts, normally the agent's apphost; <c>dotnet.exe</c> when the agent runs through the dotnet host.</param>
/// <param name="ExecutableArguments">Arguments that come before the verb <c>run</c>, for example the path of the agent assembly when <paramref name="ExecutablePath"/> is <c>dotnet.exe</c>.</param>
public sealed record ServiceCommandContext(
    IReadOnlyList<string> Arguments,
    string? DataDirectory,
    Func<string?, DataDirectories> ResolveDirectories,
    string ExecutablePath,
    IReadOnlyList<string> ExecutableArguments);

/// <summary>
/// The verb <c>qoffeeautomat service install|uninstall|start|stop|status</c> (PLAN.md S11, T301). Install creates
/// the data directories with their permissions (S05, S09), registers the Event Log source and creates the service
/// (LocalSystem, delayed automatic start, restart on failure); uninstall stops and removes the service and keeps
/// the data.
/// </summary>
public sealed partial class ServiceCommand
{
    /// <summary>The usage text of the verb.</summary>
    public const string Usage = """
        Usage: qoffeeautomat service <install|uninstall|start|stop|status> [options]

          install      Create the data directories with their permissions, register the Event Log
                       source and create the service (LocalSystem, automatic delayed start, restart on
                       failure). Needs an administrator.
          uninstall    Stop and remove the service. The data directories stay. Needs an administrator.
          start        Start the service and wait until it runs.
          stop         Stop the service and wait until it has stopped.
          status       Show the state and the configuration of the service.

        Options:
          --name <name>        Service name (default QoffeeAutomat).
          --data-dir <path>    install: keep all files below this directory.
          --ui-port <port>     install: port of the loopback UI and API listener.
          --link-port <port>   install: port of the QoffeeLink listener.
        """;

    private static readonly Regex ServiceNamePattern = ServiceNameRegex();

    private readonly IScmClient _scm;
    private readonly DataDirectoryProvisioner _provisioner;
    private readonly IEventLogSources _eventLog;
    private readonly Func<bool> _isPrivileged;

    /// <summary>Creates the command.</summary>
    /// <param name="scm">The service control manager.</param>
    /// <param name="provisioner">Creates and protects the data directories.</param>
    /// <param name="eventLog">Registers the Event Log source.</param>
    /// <param name="isPrivileged">Tells whether the caller is an administrator; null asks the operating system.</param>
    public ServiceCommand(IScmClient scm, DataDirectoryProvisioner provisioner, IEventLogSources eventLog, Func<bool>? isPrivileged = null)
    {
        ArgumentNullException.ThrowIfNull(scm);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(eventLog);

        _scm = scm;
        _provisioner = provisioner;
        _eventLog = eventLog;
        _isPrivileged = isPrivileged ?? (() => Environment.IsPrivilegedProcess);
    }

    /// <summary>Quotes one command-line argument the way the C runtime reads it back.</summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The argument, quoted when it needs it.</returns>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(character);
            }

            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>Builds the command line that the service control manager starts.</summary>
    /// <param name="context">The executable and its leading arguments.</param>
    /// <param name="dataDirectory">The data directory option, made absolute by the caller; null for the default.</param>
    /// <param name="uiPort">The UI port, or null for the setting.</param>
    /// <param name="linkPort">The link port, or null for the setting.</param>
    /// <returns>The command line.</returns>
    public static string BuildCommandLine(ServiceCommandContext context, string? dataDirectory, int? uiPort, int? linkPort)
    {
        ArgumentNullException.ThrowIfNull(context);

        var words = new List<string> { context.ExecutablePath };
        words.AddRange(context.ExecutableArguments);
        words.Add("run");
        if (dataDirectory is not null)
        {
            words.Add("--data-dir");
            words.Add(dataDirectory);
        }

        if (uiPort is { } ui)
        {
            words.Add("--ui-port");
            words.Add(ui.ToString(CultureInfo.InvariantCulture));
        }

        if (linkPort is { } link)
        {
            words.Add("--link-port");
            words.Add(link.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(' ', words.Select(Quote));
    }

    /// <summary>Runs the verb.</summary>
    /// <param name="context">The arguments and the executable.</param>
    /// <param name="output">Receives the normal output.</param>
    /// <param name="error">Receives error messages.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A <see cref="ServiceExitCodes"/> value.</returns>
    public async Task<int> RunAsync(ServiceCommandContext context, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(context, out ServiceRequest? request, out string? problem))
        {
            await error.WriteLineAsync(problem + " Try 'service --help'.").ConfigureAwait(false);
            return ServiceExitCodes.Usage;
        }

        if (request.Action == "help")
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return ServiceExitCodes.Success;
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
            await error.WriteLineAsync("Access denied: " + exception.Message + " Run this command from an elevated prompt.").ConfigureAwait(false);
            return ServiceExitCodes.NoPermission;
        }
        catch (Exception exception) when (exception is ServiceControlException or DataDirectorySecurityException or IOException or InvalidOperationException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ServiceExitCodes.Failure;
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRegex();

    private static bool TryParse(
        ServiceCommandContext context,
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
            request = new ServiceRequest("help", ServiceDefaults.Name, null, null, null);
            return true;
        }

        if (action is not ("install" or "uninstall" or "start" or "stop" or "status"))
        {
            problem = $"Unknown service action '{args[0]}'.";
            return false;
        }

        string name = ServiceDefaults.Name;
        string? dataDirectory = context.DataDirectory;
        int? uiPort = null;
        int? linkPort = null;
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

            bool takesValue = option is "--name" or "--data-dir" or "--ui-port" or "--link-port";
            if (!takesValue)
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
                        problem = "--name must start with a letter and may contain letters, digits, '_' and '-'.";
                        return false;
                    }

                    name = value;
                    break;
                case "--data-dir":
                    if (string.IsNullOrWhiteSpace(value) || value.Contains('"', StringComparison.Ordinal))
                    {
                        problem = "--data-dir needs a directory without quotes.";
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

        request = new ServiceRequest(action, name, dataDirectory, uiPort, linkPort);
        return true;
    }

    private async Task<int> InstallAsync(ServiceCommandContext context, ServiceRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!_isPrivileged())
        {
            await error.WriteLineAsync("service install needs an administrator. Run it from an elevated prompt.").ConfigureAwait(false);
            return ServiceExitCodes.NoPermission;
        }

        if (await _scm.QueryAsync(request.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            await error.WriteLineAsync($"The service '{request.Name}' is installed already. Run 'service uninstall' first to install it again.").ConfigureAwait(false);
            return ServiceExitCodes.Failure;
        }

        string? dataDirectory = request.DataDirectory is null ? null : Path.GetFullPath(request.DataDirectory);
        DataDirectories layout = context.ResolveDirectories(dataDirectory);
        _provisioner.Provision(layout);
        _eventLog.Create(request.Name, ServiceDefaults.EventLogName);

        var definition = new ServiceDefinition(
            request.Name,
            ServiceDefaults.DisplayName,
            ServiceDefaults.Description,
            BuildCommandLine(context, dataDirectory, request.UiPort, request.LinkPort));
        await _scm.CreateAsync(definition, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync($"The service '{request.Name}' is installed (LocalSystem, automatic start, delayed). Data: {layout.Root}").ConfigureAwait(false);
        await output.WriteLineAsync("Start it with: qoffeeautomat service start").ConfigureAwait(false);
        return ServiceExitCodes.Success;
    }

    private async Task<int> UninstallAsync(ServiceRequest request, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!_isPrivileged())
        {
            await error.WriteLineAsync("service uninstall needs an administrator. Run it from an elevated prompt.").ConfigureAwait(false);
            return ServiceExitCodes.NoPermission;
        }

        ServiceInfo? service = await _scm.QueryAsync(request.Name, cancellationToken).ConfigureAwait(false);
        if (service is null)
        {
            await output.WriteLineAsync($"The service '{request.Name}' is not installed.").ConfigureAwait(false);
            return ServiceExitCodes.Success;
        }

        if (service.State != ServiceRunState.Stopped)
        {
            await _scm.StopAsync(request.Name, ServiceDefaults.ControlTimeout, cancellationToken).ConfigureAwait(false);
        }

        await _scm.DeleteAsync(request.Name, cancellationToken).ConfigureAwait(false);
        _eventLog.Delete(request.Name);
        await output.WriteLineAsync($"The service '{request.Name}' is removed. The data directories were kept.").ConfigureAwait(false);
        return ServiceExitCodes.Success;
    }

    private async Task<int> ControlAsync(ServiceRequest request, bool start, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (await _scm.QueryAsync(request.Name, cancellationToken).ConfigureAwait(false) is null)
        {
            await error.WriteLineAsync($"The service '{request.Name}' is not installed. Run 'service install' first.").ConfigureAwait(false);
            return ServiceExitCodes.NotInstalled;
        }

        if (start)
        {
            await _scm.StartAsync(request.Name, ServiceDefaults.ControlTimeout, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"The service '{request.Name}' is running.").ConfigureAwait(false);
        }
        else
        {
            await _scm.StopAsync(request.Name, ServiceDefaults.ControlTimeout, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"The service '{request.Name}' is stopped.").ConfigureAwait(false);
        }

        return ServiceExitCodes.Success;
    }

    private async Task<int> StatusAsync(ServiceRequest request, TextWriter output, CancellationToken cancellationToken)
    {
        ServiceInfo? service = await _scm.QueryAsync(request.Name, cancellationToken).ConfigureAwait(false);
        if (service is null)
        {
            await output.WriteLineAsync($"The service '{request.Name}' is not installed.").ConfigureAwait(false);
            return ServiceExitCodes.NotInstalled;
        }

        string startType = !service.StartsAutomatically
            ? "manual or disabled"
            : service.DelayedStart ? "automatic (delayed)" : "automatic";
        await output.WriteLineAsync($"Service:    {service.Name}").ConfigureAwait(false);
        await output.WriteLineAsync($"State:      {service.State}").ConfigureAwait(false);
        await output.WriteLineAsync($"Account:    {service.Account ?? "unknown"}").ConfigureAwait(false);
        await output.WriteLineAsync($"Start type: {startType}").ConfigureAwait(false);
        await output.WriteLineAsync($"Command:    {service.CommandLine ?? "unknown"}").ConfigureAwait(false);
        return ServiceExitCodes.Success;
    }

    private sealed record ServiceRequest(string Action, string Name, string? DataDirectory, int? UiPort, int? LinkPort);
}
