using System.ComponentModel;
using System.Diagnostics;

namespace QoffeeAutomat.Core.Metrics.Custom;

/// <summary>Lists the names of the running processes; the source of <c>process_running</c> sensors.</summary>
public interface IProcessNameSource
{
    /// <summary>Reads the process list once.</summary>
    /// <returns>
    /// The names of all running processes without a directory and, on Windows, usually without <c>.exe</c>. The
    /// set compares names the way the operating system does: ignoring case on Windows, exactly elsewhere.
    /// </returns>
    IReadOnlySet<string> GetRunningProcessNames();
}

/// <summary>The default <see cref="IProcessNameSource"/>, based on <see cref="Process.GetProcesses()"/>.</summary>
public sealed class SystemProcessNameSource : IProcessNameSource
{
    /// <inheritdoc />
    public IReadOnlySet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The process ended while the list was being read, or its name is not readable.
                }
            }
        }

        return names;
    }
}

/// <summary>Matching of the process name of a <c>process_running</c> sensor against the running processes.</summary>
public static class ProcessNames
{
    /// <summary>Tells whether a process with the requested name is running.</summary>
    /// <param name="running">The names of the running processes, see <see cref="IProcessNameSource"/>.</param>
    /// <param name="requested">The name from the sensor document. A trailing <c>.exe</c> is optional: <c>notepad</c> and <c>notepad.exe</c> are the same process.</param>
    /// <returns>True when a process with that name is running.</returns>
    public static bool IsRunning(IReadOnlySet<string> running, string requested)
    {
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(requested);

        string name = requested.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        if (running.Contains(name))
        {
            return true;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && running.Contains(name[..^4]);
    }
}
