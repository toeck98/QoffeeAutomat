using System.Diagnostics;
using System.Globalization;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Tests.Execution;

/// <summary>A test that needs a POSIX shell and coreutils; it is reported as skipped on Windows.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs a POSIX shell.";
        }
    }
}

/// <summary>A test that needs Windows tools such as cmd.exe; it is reported as skipped elsewhere.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows.";
        }
    }
}

/// <summary>Builders and helpers shared by the process runner tests.</summary>
internal static class ProcessTestSupport
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static readonly Dictionary<string, string> NoEnvironment = new();

    public static ProcessLaunchRequest Request(
        string file,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool waitForExit = true,
        bool captureOutput = true)
    {
        return new ProcessLaunchRequest(
            file,
            arguments ?? [],
            workingDirectory,
            environment ?? NoEnvironment,
            timeout ?? TimeSpan.FromSeconds(30),
            waitForExit,
            captureOutput);
    }

    /// <summary>Runs a script with <c>/bin/sh -c</c>; the arguments become <c>$1</c>, <c>$2</c> and so on.</summary>
    public static ProcessLaunchRequest Sh(
        string script,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool waitForExit = true,
        bool captureOutput = true)
    {
        return Request(
            "/bin/sh",
            ["-c", script, "sh", .. arguments ?? []],
            timeout,
            workingDirectory,
            environment,
            waitForExit,
            captureOutput);
    }

    /// <summary>Waits until a file holds a complete line with a process id, written by the script under test.</summary>
    public static async Task<int> ReadPidAsync(string path)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                string text = await File.ReadAllTextAsync(path, timeout.Token);
                if (text.EndsWith('\n') && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                {
                    return pid;
                }
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Whether a process is running; a zombie that only waits to be reaped counts as gone.</summary>
    public static bool IsAlive(int pid)
    {
        string stat = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat";
        if (File.Exists(stat))
        {
            try
            {
                string text = File.ReadAllText(stat);
                int close = text.LastIndexOf(')');
                char state = text[close + 2];
                return state is not ('Z' or 'X');
            }
            catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Waits until a process is gone; the kill signal is delivered asynchronously.</summary>
    public static async Task AssertGoneAsync(int pid)
    {
        using var timeout = new CancellationTokenSource(Patience);
        while (IsAlive(pid))
        {
            Assert.False(timeout.IsCancellationRequested, $"Process {pid.ToString(CultureInfo.InvariantCulture)} is still running.");
            await Task.Delay(20);
        }
    }

    public static void KillQuietly(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>A containment that records what the runner does with it and kills by walking the tree.</summary>
internal sealed class RecordingContainment : IProcessContainment
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    /// <summary>When set, <see cref="Prepare"/> returns this instead of the command it was given.</summary>
    public Func<ProcessCommand, ProcessCommand>? Rewrite { get; init; }

    /// <summary>When true, <see cref="Attach"/> throws, like a job object that cannot be created.</summary>
    public bool FailToAttach { get; init; }

    public string Name => "recording";

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public ProcessCommand Prepare(ProcessCommand command)
    {
        Record("prepare:" + command.FileName);
        return Rewrite?.Invoke(command) ?? command;
    }

    public IProcessTree Attach(Process process)
    {
        if (FailToAttach)
        {
            Record("attach-failed");
            throw new InvalidOperationException("Cannot attach.");
        }

        Record("attach");
        return new Tree(this, process);
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    private sealed class Tree : IProcessTree
    {
        private readonly RecordingContainment _owner;
        private readonly Process _process;

        public Tree(RecordingContainment owner, Process process)
        {
            _owner = owner;
            _process = process;
        }

        public void Kill()
        {
            _owner.Record("kill");
            _process.Kill(entireProcessTree: true);
        }

        public void Dispose()
        {
            _owner.Record("dispose");
        }
    }
}
