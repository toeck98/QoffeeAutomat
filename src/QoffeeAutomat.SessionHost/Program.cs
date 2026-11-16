using System.Diagnostics;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.SessionHost.Actions;
using QoffeeAutomat.SessionHost.Execution;
using QoffeeAutomat.SessionHost.Ipc;
using QoffeeAutomat.SessionHost.Metrics;
using QoffeeAutomat.SessionHost.Tray;

namespace QoffeeAutomat.SessionHost;

/// <summary>
/// Entry point of the SessionHost (PLAN.md D-009, S11): a windowless program that the agent starts in every interactive
/// session with the token of the user. It connects to the agent over the named pipe, runs user-context commands and ends
/// when the agent says so. Diagnostics go to <c>%LOCALAPPDATA%\QoffeeAutomat\logs\sessionhost.log</c>.
/// </summary>
public static class Program
{
    /// <summary>Runs the SessionHost.</summary>
    /// <param name="args">The command line, see <see cref="SessionHostOptions"/>.</param>
    /// <returns>A <see cref="SessionHostExitCodes"/> value.</returns>
    public static async Task<int> Main(string[] args)
    {
        var log = new FileSessionHostLog(FileSessionHostLog.DefaultPath());

        int currentSession;
        using (Process current = Process.GetCurrentProcess())
        {
            currentSession = current.SessionId;
        }

        SessionHostOptions? options = SessionHostOptions.Parse(args, currentSession, AppContext.BaseDirectory, out string? error);
        if (options is null)
        {
            log.Warning("Wrong command line: " + error);
            return SessionHostExitCodes.Usage;
        }

        // One instance per session: the name is in the namespace of the session. Only its existence matters, so it is never owned.
        using var instance = new Mutex(initiallyOwned: false, @"Local\QoffeeAutomat.SessionHost", out bool isFirst);
        if (!isFirst)
        {
            log.Info("Another session helper already runs in this session.");
            return SessionHostExitCodes.AlreadyRunning;
        }

        var clientOptions = new SessionHostClientOptions
        {
            SessionId = options.SessionId,
            PipeName = options.PipeName,
            UserName = UserTokenInfo.CurrentUserName(),
            IsAdministrator = UserTokenInfo.IsAdministrator(),
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        };
        var client = new SessionHostClient(
            clientOptions,
            new NativePipeServerVerifier(options.AgentImagePath),
            new ProcessRunner(),
            UserBuiltinActions.CreateNative(),
            locator: null,
            time: null,
            log,
            new WindowsUserMetricProbe());

        log.Info($"Started in session {options.SessionId}.");

        // The tray icon (T310). It stays hidden until the agent says that it is wanted (settings: trayIcon), and a failure to
        // create it never stops the SessionHost: commands of the user's session matter more than the icon.
        NotifyIconShell? shell = null;
        TrayCoordinator? tray = null;
        try
        {
            shell = new NotifyIconShell(clientOptions.Version, log);
            tray = new TrayCoordinator(client, shell, log);
        }
        catch (InvalidOperationException exception)
        {
            log.Warning("Running without a tray icon: " + exception.Message);
        }

        try
        {
            using var cancellation = new CancellationTokenSource();
            int exitCode = await client.RunAsync(cancellation.Token).ConfigureAwait(false);
            log.Info($"Ended with exit code {exitCode}.");
            return exitCode;
        }
        finally
        {
            // The icon disappears with the SessionHost.
            tray?.Dispose();
            shell?.Dispose();
        }
    }
}
