using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Platform.Linux.Actions;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>
/// The composition root of the session helper (PLAN.md S17, T803): what <c>qoffeeautomat session-host</c> runs. It is started by the systemd
/// user unit <c>qoffeeautomat-session.service</c> inside the session of the user, listens on the socket in the runtime directory of the
/// user, and serves the agent until it is stopped with SIGTERM or SIGINT. It has no configuration of its own: the agent sends everything
/// it needs with every run.
/// </summary>
public static class SessionHelperHost
{
    /// <summary>The category of the log messages of the helper.</summary>
    public const string LogCategory = "QoffeeAutomat.SessionHelper";

    /// <summary>Runs the helper until it is stopped.</summary>
    /// <param name="version">The product version, for the <c>hello</c>.</param>
    /// <param name="environment">Reads an environment variable; null when it is not set.</param>
    /// <param name="error">Receives a message when the helper cannot start.</param>
    /// <param name="cancellationToken">Stops the helper (a test; the real process is stopped by a signal).</param>
    /// <returns>A <see cref="SessionHostExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(string version, Func<string, string?> environment, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(error);

        string? socketPath = SessionSocketPaths.ForHelper(environment);
        if (socketPath is null)
        {
            await error.WriteLineAsync(
                $"The session helper needs {SessionSocketPaths.RuntimeDirectoryVariable} (the runtime directory of the user session). Start it with the systemd user unit: systemctl --user start qoffeeautomat-session.").ConfigureAwait(false);
            return SessionHostExitCodes.Usage;
        }

        using ILoggerFactory loggers = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddSystemdConsole(console =>
            {
                console.IncludeScopes = false;
                console.TimestampFormat = null;
            });
        });
        ILogger logger = loggers.CreateLogger(LogCategory);

        var commands = new ProcessCommandRunner();
        var userActions = new LinuxUserActions(commands);

        // The scripts of the helper live in the runtime directory of the user, below the socket directory (mode 0700).
        string tempRoot = Path.Combine(Path.GetDirectoryName(socketPath)!, "tmp");
        var bash = new BashRunner(new BashLocator(), new BashOptions { TempRoot = tempRoot });
        var executor = new LinuxUserCommandExecutor(new ProcessRunner(), bash, userActions);

        await using var server = new SessionHelperServer(
            socketPath,
            executor,
            new LinuxDesktopMetricSource(commands),
            async (title, message, token) => _ = await userActions.ShowNotificationAsync(title, message, token).ConfigureAwait(false),
            new SessionHelperIdentity(Environment.UserName, version),
            logger);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });

        try
        {
            return await server.RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            await error.WriteLineAsync("The session helper could not listen on " + socketPath + ": " + exception.Message).ConfigureAwait(false);
            return SessionHostExitCodes.Failure;
        }
    }
}
