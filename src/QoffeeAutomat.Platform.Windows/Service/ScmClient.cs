using System.Diagnostics;
using System.ServiceProcess;
using System.Text;

namespace QoffeeAutomat.Platform.Windows.Service;

/// <summary>The service control manager; the seam between the CLI logic and Windows.</summary>
public interface IScmClient
{
    /// <summary>Looks a service up.</summary>
    /// <param name="name">The service name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The service, or null when it is not installed.</returns>
    Task<ServiceInfo?> QueryAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the service: own process, LocalSystem, delayed automatic start, a description, and restart on
    /// failure (also when the process exits with an error code).
    /// </summary>
    /// <param name="definition">What to create.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the service is configured.</returns>
    Task CreateAsync(ServiceDefinition definition, CancellationToken cancellationToken);

    /// <summary>Marks the service for deletion.</summary>
    /// <param name="name">The service name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the service is deleted.</returns>
    Task DeleteAsync(string name, CancellationToken cancellationToken);

    /// <summary>Starts the service and waits until it runs.</summary>
    /// <param name="name">The service name.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the service runs.</returns>
    Task StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Stops the service and waits until it has stopped.</summary>
    /// <param name="name">The service name.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the service has stopped.</returns>
    Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>The outcome of a run of <c>sc.exe</c>.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">What it printed.</param>
public sealed record ScResult(int ExitCode, string Output);

/// <summary>Runs <c>sc.exe</c>; the seam that lets the arguments be tested without Windows.</summary>
public interface IScRunner
{
    /// <summary>Runs <c>sc.exe</c> with the given arguments.</summary>
    /// <param name="arguments">Separate argument entries, never spliced into one string by the caller.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Runs the real <c>sc.exe</c> from the system directory.</summary>
public sealed class ProcessScRunner : IScRunner
{
    /// <inheritdoc />
    public async Task<ScResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("sc.exe could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string text = (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false));
        return new ScResult(process.ExitCode, text.Trim());
    }
}

/// <summary>The failure of a service control operation; the message is meant for the user.</summary>
public sealed class ServiceControlException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public ServiceControlException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public ServiceControlException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ServiceControlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The <see cref="IScmClient"/> of a Windows computer. Querying, starting and stopping use
/// <see cref="ServiceController"/>; creating and deleting use <c>sc.exe</c> with one argument entry per word, so
/// that no value is ever spliced into a command line (PLAN.md D-012). <c>sc.exe</c> is the one tool that sets the
/// delayed start and the failure actions without native interop.
/// </summary>
public sealed class ScmClient : IScmClient
{
    private readonly IScRunner _sc;

    /// <summary>Creates the client.</summary>
    /// <param name="sc">Runs <c>sc.exe</c>; null runs the real one.</param>
    public ScmClient(IScRunner? sc = null)
    {
        _sc = sc ?? new ProcessScRunner();
    }

    /// <summary>The arguments of <c>sc.exe</c> that create the service, in the order they run.</summary>
    /// <param name="definition">What to create.</param>
    /// <returns>The argument lists, one per run.</returns>
    public static IReadOnlyList<IReadOnlyList<string>> CreateCommands(ServiceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // "binPath=" and its value are separate entries on purpose: sc.exe wants the space after the equals sign.
        return
        [
            [
                "create", definition.Name,
                "binPath=", definition.CommandLine,
                "type=", "own",
                "start=", "delayed-auto",
                "obj=", ServiceDefaults.Account,
                "DisplayName=", definition.DisplayName,
            ],
            ["description", definition.Name, definition.Description],
            [
                "failure", definition.Name,
                "reset=", ServiceDefaults.FailureResetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "actions=", FailureActions(),
            ],

            // Restart also when the process ends with an exit code other than zero, not only when it crashes.
            ["failureflag", definition.Name, "1"],
        ];
    }

    /// <summary>Reads the settings from the output of <c>sc qc</c>.</summary>
    /// <param name="name">The service name.</param>
    /// <param name="state">The state from the control manager.</param>
    /// <param name="output">The output of <c>sc qc &lt;name&gt;</c>.</param>
    /// <returns>The service.</returns>
    public static ServiceInfo ParseConfiguration(string name, ServiceRunState state, string output)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(output);

        string? account = null;
        string? commandLine = null;
        bool automatic = false;
        bool delayed = false;
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "START_TYPE":
                    automatic = value.Contains("AUTO_START", StringComparison.Ordinal);
                    delayed = automatic && value.Contains("DELAYED", StringComparison.Ordinal);
                    break;
                case "SERVICE_START_NAME":
                    account = value;
                    break;
                case "BINARY_PATH_NAME":
                    commandLine = value;
                    break;
            }
        }

        return new ServiceInfo(name, state, account, automatic, delayed, commandLine);
    }

    /// <inheritdoc />
    public async Task<ServiceInfo?> QueryAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ServiceRunState? state = await Task.Run(() => ReadState(name), cancellationToken).ConfigureAwait(false);
        if (state is not { } known)
        {
            return null;
        }

        ScResult configuration = await _sc.RunAsync(["qc", name], cancellationToken).ConfigureAwait(false);
        return configuration.ExitCode == 0
            ? ParseConfiguration(name, known, configuration.Output)
            : new ServiceInfo(name, known, null, StartsAutomatically: false, DelayedStart: false, null);
    }

    /// <inheritdoc />
    public async Task CreateAsync(ServiceDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        foreach (IReadOnlyList<string> command in CreateCommands(definition))
        {
            await RunCheckedAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunCheckedAsync(["delete", name], cancellationToken);
    }

    /// <inheritdoc />
    public Task StartAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Task.Run(
            () =>
            {
                using var controller = new ServiceController(name);
                try
                {
                    if (controller.Status == ServiceControllerStatus.Running)
                    {
                        return;
                    }

                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
                {
                    throw new ServiceControlException($"The service '{name}' did not start: {Describe(exception)}", exception);
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Task.Run(
            () =>
            {
                using var controller = new ServiceController(name);
                try
                {
                    if (controller.Status == ServiceControllerStatus.Stopped)
                    {
                        return;
                    }

                    if (controller.Status != ServiceControllerStatus.StopPending)
                    {
                        controller.Stop();
                    }

                    controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
                {
                    throw new ServiceControlException($"The service '{name}' did not stop: {Describe(exception)}", exception);
                }
            },
            cancellationToken);
    }

    private static string FailureActions()
    {
        int shortDelay = (int)ServiceDefaults.RestartDelay.TotalMilliseconds;
        int longDelay = (int)ServiceDefaults.LongRestartDelay.TotalMilliseconds;
        var text = new StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"restart/{shortDelay}/restart/{shortDelay}/restart/{longDelay}");
        return text.ToString();
    }

    private static string Describe(Exception exception)
    {
        return exception.InnerException?.Message ?? exception.Message;
    }

    private static ServiceRunState? ReadState(string name)
    {
        using var controller = new ServiceController(name);
        try
        {
            return controller.Status switch
            {
                ServiceControllerStatus.Stopped => ServiceRunState.Stopped,
                ServiceControllerStatus.StartPending => ServiceRunState.StartPending,
                ServiceControllerStatus.StopPending => ServiceRunState.StopPending,
                ServiceControllerStatus.Running => ServiceRunState.Running,
                _ => ServiceRunState.Other,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController reports "service does not exist" this way.
            return null;
        }
    }

    private async Task RunCheckedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ScResult result = await _sc.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ServiceControlException($"sc.exe {arguments[0]} failed with exit code {result.ExitCode}: {result.Output}");
        }
    }
}
