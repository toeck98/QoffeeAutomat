using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace QoffeeAutomat.Core.Execution;

/// <summary>An executable and its arguments, one entry per argv element.</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">The arguments; never split or joined.</param>
public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>The handle with which a started process and all its descendants can be killed.</summary>
public interface IProcessTree : IDisposable
{
    /// <summary>Kills the process and everything it started; does nothing when they are gone already.</summary>
    void Kill();
}

/// <summary>
/// How the process runner keeps hold of everything a process starts (PLAN.md T206): a process
/// group on Unix, a job object on Windows. The default (<see cref="ProcessContainments.CreateDefault"/>)
/// lives in Core; the Windows platform replaces it with a job object.
/// </summary>
public interface IProcessContainment
{
    /// <summary>A short name for logs, for example <c>process-group</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Called before the process is started; may change how it is started, for example so that it
    /// becomes the leader of a new process group. The semantics of the command must stay the same.
    /// </summary>
    /// <param name="command">What the caller wants to start.</param>
    /// <returns>What to start instead (or the same command).</returns>
    ProcessCommand Prepare(ProcessCommand command);

    /// <summary>Called right after the process was started.</summary>
    /// <param name="process">The running process.</param>
    /// <returns>The handle used to kill the tree; disposed when the run is over.</returns>
    IProcessTree Attach(Process process);
}

/// <summary>The containments that Core ships.</summary>
public static class ProcessContainments
{
    /// <summary>
    /// Picks the best containment that works without platform-specific code: on Unix a new process
    /// group (when <c>setsid</c> and <c>kill</c> are installed), otherwise a walk over the process
    /// tree. On Windows this is the tree walk; the Windows platform supplies a job object.
    /// </summary>
    /// <returns>A containment.</returns>
    public static IProcessContainment CreateDefault()
    {
        if (!OperatingSystem.IsWindows()
            && ExecutableLocator.Find("setsid") is { } setsid
            && ExecutableLocator.Find("kill") is { } kill)
        {
            return new UnixProcessGroupContainment(setsid, kill);
        }

        return new ProcessTreeContainment();
    }
}

/// <summary>
/// Kills a process together with its descendants by walking the process tree
/// (<see cref="Process.Kill(bool)"/>). Misses descendants whose parent already exited.
/// </summary>
public sealed class ProcessTreeContainment : IProcessContainment
{
    /// <inheritdoc />
    public string Name => "process-tree";

    /// <inheritdoc />
    public ProcessCommand Prepare(ProcessCommand command)
    {
        return command;
    }

    /// <inheritdoc />
    public IProcessTree Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new TreeWalk(process);
    }

    internal static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // The process is gone already, or part of the tree could not be killed; nothing more to do.
        }
    }

    private sealed class TreeWalk : IProcessTree
    {
        private readonly Process _process;

        public TreeWalk(Process process)
        {
            _process = process;
        }

        public void Kill()
        {
            KillTree(_process);
        }

        public void Dispose()
        {
            // Nothing is held.
        }
    }
}

/// <summary>
/// Unix: the process becomes the leader of a new process group (it is started through <c>setsid</c>,
/// which replaces itself with the real program, so the process id stays the same), and the whole
/// group is killed with <c>kill -9 -- -PGID</c>. That reaches descendants that lost their parent as
/// well. The tree walk follows as a safety net for processes that left the group.
/// </summary>
internal sealed class UnixProcessGroupContainment : IProcessContainment
{
    private readonly string _setsid;
    private readonly string _kill;

    public UnixProcessGroupContainment(string setsid, string kill)
    {
        _setsid = setsid;
        _kill = kill;
    }

    public string Name => "process-group";

    public ProcessCommand Prepare(ProcessCommand command)
    {
        // setsid reports a missing program in its own words and exit code; look it up first so a
        // missing executable fails the same way as without containment.
        string target = ExecutableLocator.Find(command.FileName)
            ?? throw new FileNotFoundException($"The executable '{command.FileName}' was not found.", command.FileName);
        return new ProcessCommand(_setsid, [target, .. command.Arguments]);
    }

    public IProcessTree Attach(Process process)
    {
        return new Group(process, _kill);
    }

    private sealed class Group : IProcessTree
    {
        private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

        private readonly Process _process;
        private readonly string _kill;
        private readonly int _groupId;

        public Group(Process process, string kill)
        {
            _process = process;
            _kill = kill;
            _groupId = process.Id;
        }

        public void Kill()
        {
            KillGroup();
            ProcessTreeContainment.KillTree(_process);
        }

        public void Dispose()
        {
            // Nothing is held.
        }

        private void KillGroup()
        {
            var info = new ProcessStartInfo(_kill)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-9");
            info.ArgumentList.Add("--");
            info.ArgumentList.Add("-" + _groupId.ToString(CultureInfo.InvariantCulture));

            try
            {
                using Process killer = Process.Start(info)!;
                killer.StandardInput.Close();
                killer.WaitForExit(KillWait);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                // The tree walk that follows is the fallback.
            }
        }
    }
}

/// <summary>Finds executables the way a shell would, without starting one.</summary>
internal static class ExecutableLocator
{
    /// <summary>Resolves a file name; a name with a directory part is taken relative to the current directory.</summary>
    /// <param name="name">A file name or path.</param>
    /// <returns>The full path, or null when no executable file matches.</returns>
    public static string? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0)
        {
            string full = Path.GetFullPath(name);
            return IsExecutable(full) ? full : null;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try
        {
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
