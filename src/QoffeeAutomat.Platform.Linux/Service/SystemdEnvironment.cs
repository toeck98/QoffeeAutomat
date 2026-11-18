using System.Globalization;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>
/// What systemd tells a service about itself through the environment (<c>sd_notify(3)</c>, <c>sd_watchdog_enabled(3)</c>,
/// <c>systemd.exec(5)</c>), and how the agent recognises that it was started by systemd (PLAN.md S17, T802). Every member takes the
/// source of the variables and the root directory as parameters, so that tests never depend on the environment of the test runner.
/// </summary>
public static class SystemdEnvironment
{
    /// <summary>The variable that holds the address of the notification socket; systemd sets it for <c>Type=notify</c> services.</summary>
    public const string NotifySocketVariable = "NOTIFY_SOCKET";

    /// <summary>The variable that holds the watchdog interval in microseconds.</summary>
    public const string WatchdogUsecVariable = "WATCHDOG_USEC";

    /// <summary>The variable that names the process that must send the watchdog keep-alive; absent means the main process.</summary>
    public const string WatchdogPidVariable = "WATCHDOG_PID";

    /// <summary>The variable that systemd sets for every unit it starts.</summary>
    public const string InvocationIdVariable = "INVOCATION_ID";

    /// <summary>
    /// Tells whether this process is the service of a systemd unit. A <c>Type=notify</c> unit always carries a notification socket;
    /// otherwise the invocation id together with a parent process 1 identifies a unit. The invocation id alone is not enough: a
    /// terminal that the desktop started as a systemd unit hands it down to every shell, and a developer who runs the agent there
    /// must keep the console output.
    /// </summary>
    /// <param name="getVariable">Reads an environment variable; null when it is not set.</param>
    /// <param name="rootDirectory">The directory that holds <c>proc</c>; null or empty means <c>/</c>.</param>
    /// <returns>True when systemd started this process as a service.</returns>
    public static bool IsSystemdService(Func<string, string?> getVariable, string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        if (!string.IsNullOrEmpty(getVariable(NotifySocketVariable)))
        {
            return true;
        }

        return !string.IsNullOrEmpty(getVariable(InvocationIdVariable)) && ReadParentProcessId(rootDirectory) == 1;
    }

    /// <summary>Tells whether this system was started with systemd (<c>sd_booted(3)</c>): the directory <c>/run/systemd/system</c> exists.</summary>
    /// <param name="rootDirectory">The directory that holds <c>run</c>; null or empty means <c>/</c>.</param>
    /// <returns>True when systemd is the init system.</returns>
    public static bool IsSystemdBooted(string? rootDirectory = null)
    {
        return Directory.Exists(Path.Combine(RootOf(rootDirectory), "run/systemd/system"));
    }

    /// <summary>Reads the watchdog interval that systemd configured with <c>WatchdogSec=</c>.</summary>
    /// <param name="getVariable">Reads an environment variable; null when it is not set.</param>
    /// <param name="processId">The id of this process; <c>WATCHDOG_PID</c> must name it when it is set.</param>
    /// <returns>The interval within which a keep-alive must arrive; null when the watchdog is off or meant for another process.</returns>
    public static TimeSpan? ReadWatchdogInterval(Func<string, string?> getVariable, int processId)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        string? usec = getVariable(WatchdogUsecVariable);
        if (!ulong.TryParse(usec, NumberStyles.None, CultureInfo.InvariantCulture, out ulong microseconds) || microseconds == 0)
        {
            return null;
        }

        string? pid = getVariable(WatchdogPidVariable);
        if (!string.IsNullOrEmpty(pid)
            && (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int target) || target != processId))
        {
            return null;
        }

        // A TimeSpan holds ticks of 100 ns; a value beyond its range is "never" for practical purposes.
        const ulong MaxMicroseconds = long.MaxValue / TimeSpan.TicksPerMicrosecond;
        return TimeSpan.FromTicks((long)Math.Min(microseconds, MaxMicroseconds) * TimeSpan.TicksPerMicrosecond);
    }

    /// <summary>The interval at which keep-alives are sent: half of the watchdog interval, as <c>sd_watchdog_enabled(3)</c> recommends.</summary>
    /// <param name="watchdog">The interval that systemd waits for.</param>
    /// <returns>Half of <paramref name="watchdog"/>, at least one second.</returns>
    public static TimeSpan KeepAliveInterval(TimeSpan watchdog)
    {
        TimeSpan half = watchdog / 2;
        return half < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : half;
    }

    /// <summary>Reads the id of the parent process from <c>/proc/self/stat</c>.</summary>
    /// <param name="rootDirectory">The directory that holds <c>proc</c>; null or empty means <c>/</c>.</param>
    /// <returns>The parent process id; null when it cannot be read.</returns>
    public static int? ReadParentProcessId(string? rootDirectory = null)
    {
        try
        {
            string line = File.ReadAllText(Path.Combine(RootOf(rootDirectory), "proc/self/stat"));
            return ParseParentProcessId(line);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses the id of the parent process from the content of <c>/proc/&lt;pid&gt;/stat</c>.</summary>
    /// <param name="stat">The line <c>pid (comm) state ppid ...</c>.</param>
    /// <returns>The parent process id; null when the line is not in that form.</returns>
    public static int? ParseParentProcessId(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        // The command name is in parentheses and may itself contain spaces and parentheses, so the last ')' ends it.
        int end = stat.LastIndexOf(')');
        if (end < 0)
        {
            return null;
        }

        string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int parent) ? parent : null;
    }

    private static string RootOf(string? rootDirectory) => string.IsNullOrEmpty(rootDirectory) ? "/" : rootDirectory;
}
