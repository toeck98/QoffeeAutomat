using System.Globalization;
using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution.PowerShell;

/// <summary>Settings of <see cref="PowerShellRunner"/>.</summary>
public sealed class PowerShellOptions
{
    /// <summary>The default for <see cref="StaleAfter"/>: longer than the longest command timeout (24 hours).</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(48);

    /// <summary>The scripts directory that <c>source: file</c> refers to; null makes file scripts fail with a clear error.</summary>
    public string? ScriptsDirectory { get; init; }

    /// <summary>
    /// The directory below which every run gets a private directory for the wrapper, the parameter file and
    /// inline code; null uses the system temp directory. On Unix the per-run directory has mode 0700 and its
    /// files 0600. On Windows the directory inherits the access rules of this root, so the Windows platform
    /// supplies a root that only the executing account (and, for <c>runAs: user</c>, that user) can read.
    /// </summary>
    public string? TempRoot { get; init; }

    /// <summary>File operations used to write the temporary files; null uses the real file system. A seam for fault injection in tests.</summary>
    public IFileOperations? FileOperations { get; init; }

    /// <summary>Directories of runs that crashed (and so were not cleaned up) are removed after this age, once per runner.</summary>
    public TimeSpan StaleAfter { get; init; } = DefaultStaleAfter;

    /// <summary>
    /// Checks script files before they run with more rights than the user's (PLAN.md S09 item 5); null runs
    /// every script. A refused script ends the run with <see cref="ErrorCodes.Internal"/>.
    /// </summary>
    public IScriptAccessGuard? ScriptGuard { get; init; }

    /// <summary>
    /// Runs the invocations of <c>runAs: user</c> in the session of the interactive user (PLAN.md S11); null runs them
    /// through the launcher like every other invocation. The SessionHost has none: there the runner already is in the
    /// session.
    /// </summary>
    public IUserContextPowerShell? UserSession { get; init; }
}

/// <summary>Everything the runner needs to execute one piece of PowerShell: an action of a command or the script of a sensor.</summary>
/// <param name="Source"><see cref="PowerShellSources.Inline"/> or <see cref="PowerShellSources.File"/>.</param>
/// <param name="Code">The code when the source is inline.</param>
/// <param name="Script">The file name in the scripts directory when the source is a file.</param>
/// <param name="Engine">One of <see cref="PowerShellEngines"/>.</param>
/// <param name="Variables">The validated variables (string, long, double, bool); passed as typed named parameters.</param>
/// <param name="Environment">The environment variables of the process (<c>QA_*</c>, <c>QA_VAR_*</c>).</param>
/// <param name="Timeout">How long the engine may run before its process tree is killed.</param>
/// <param name="RunAs">One of <see cref="RunAsValues"/>.</param>
/// <param name="CaptureOutput">Whether standard output and error are captured.</param>
public sealed record PowerShellInvocation(
    string Source,
    string? Code,
    string? Script,
    string Engine,
    IReadOnlyDictionary<string, object> Variables,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    string RunAs = RunAsValues.System,
    bool CaptureOutput = true);

/// <summary>
/// Runs PowerShell the way PLAN.md D-012 describes: out of process, through the generated wrapper, with the
/// variables in a JSON parameter file. The runner finds the engine, writes the wrapper, the parameter file and
/// (for inline code, or a script that Windows PowerShell 5.1 would misread) a script file into a private
/// per-run directory, starts the engine through the <see cref="IProcessLauncher"/> and deletes the directory
/// when the process has ended, however it ended. Both the <c>powershell</c> action and the <c>powershell</c>
/// custom sensors use it.
/// </summary>
public sealed class PowerShellRunner
{
    /// <summary>The name prefix of the per-run directories.</summary>
    public const string WorkspacePrefix = "qa-ps-";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly IPowerShellEngineLocator _locator;
    private readonly PowerShellOptions _options;
    private readonly TimeProvider _time;
    private readonly IFileOperations _files;
    private int _swept;

    /// <summary>Creates the runner.</summary>
    /// <param name="locator">Finds the engine; null searches this computer.</param>
    /// <param name="options">Directories and tuning; null uses the defaults.</param>
    /// <param name="time">The clock, used to age out directories of crashed runs; null uses the system clock.</param>
    public PowerShellRunner(IPowerShellEngineLocator? locator = null, PowerShellOptions? options = null, TimeProvider? time = null)
    {
        _locator = locator ?? new PowerShellEngineLocator();
        _options = options ?? new PowerShellOptions();
        _time = time ?? TimeProvider.System;
        _files = _options.FileOperations ?? FileOperations.Instance;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.StaleAfter, TimeSpan.Zero, nameof(options));
    }

    /// <summary>Looks for an engine without running anything; the UI uses it to tell the user what is installed.</summary>
    /// <param name="engine">One of <see cref="PowerShellEngines"/>.</param>
    /// <returns>The engine or the reason it is missing.</returns>
    public PowerShellEngineResolution ResolveEngine(string engine)
    {
        return _locator.Resolve(engine);
    }

    /// <summary>
    /// Removes per-run directories under <paramref name="root"/> that were last written to before
    /// <paramref name="now"/> minus <paramref name="olderThan"/>. A directory that cannot be removed (it belongs to
    /// another account, or is in use) is skipped.
    /// </summary>
    /// <param name="root">The directory that holds the per-run directories.</param>
    /// <param name="olderThan">The minimum age.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The number of directories removed.</returns>
    public static int SweepStale(string root, TimeSpan olderThan, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return 0;
        }

        int removed = 0;
        DateTime cutoff = (now - olderThan).UtcDateTime;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root, WorkspacePrefix + "*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                        removed++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // In use, or not ours: leave it.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The root cannot be listed; there is nothing to sweep.
        }

        return removed;
    }

    /// <summary>Runs the PowerShell code or script and waits for the engine to end.</summary>
    /// <param name="invocation">What to run.</param>
    /// <param name="launcher">Starts the engine in the run context of the command.</param>
    /// <param name="cancellationToken">Cancelling it kills the engine's process tree.</param>
    /// <returns>
    /// The outcome of the engine process: the exit code of the script, or <c>timeout</c>/<c>cancelled</c>. A
    /// missing engine, a missing script or an unusable temporary directory is a failure with error
    /// <see cref="ErrorCodes.Internal"/> and a message that never contains variable values.
    /// </returns>
    public async ValueTask<ExecutionResult> RunAsync(
        PowerShellInvocation invocation,
        IProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(invocation.Variables);
        ArgumentNullException.ThrowIfNull(invocation.Environment);

        if (invocation.RunAs == RunAsValues.User && _options.UserSession is { } userSession)
        {
            // The session helper finds the engine, writes the wrapper and runs it with the user's rights.
            return await userSession.RunAsync(invocation, cancellationToken);
        }

        PowerShellEngineResolution resolution = _locator.Resolve(invocation.Engine);
        if (resolution.Engine is not { } engine)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, resolution.Reason ?? "No PowerShell engine is available.");
        }

        ExecutionResult? invalid = ValidateSource(invocation, out string? scriptPath);
        if (invalid is not null)
        {
            return invalid;
        }

        byte[] parameters;
        try
        {
            parameters = PowerShellParameterFile.Serialize(invocation.Variables);
        }
        catch (ArgumentException exception)
        {
            return ExecutionResult.Failure(ErrorCodes.InvalidVars, exception.Message);
        }

        PowerShellWorkspace? workspace = null;
        try
        {
            ProcessLaunchRequest request;
            try
            {
                SweepOnce();
                workspace = PowerShellWorkspace.Create(ResolveRoot());
                string wrapperPath = await workspace.WriteAsync(_files, "wrapper.ps1", WithBom(PowerShellWrapper.Script), cancellationToken);
                string parametersPath = await workspace.WriteAsync(_files, "parameters.json", parameters, cancellationToken);
                string target = await PrepareTargetAsync(workspace, invocation, engine, scriptPath, cancellationToken);

                var environment = new Dictionary<string, string>(invocation.Environment, StringComparer.Ordinal)
                {
                    [PowerShellWrapper.ParametersFileVariable] = parametersPath,
                    [PowerShellWrapper.TargetScriptVariable] = target,
                };

                request = new ProcessLaunchRequest(
                    engine.Path,
                    PowerShellWrapper.BuildArguments(wrapperPath),
                    WorkingDirectory: null,
                    environment,
                    invocation.Timeout,
                    WaitForExit: true,
                    invocation.CaptureOutput);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ExecutionResult.Failure(
                    ErrorCodes.Internal,
                    "The temporary files for the PowerShell run could not be written: " + exception.Message);
            }

            return await launcher.LaunchAsync(invocation.RunAs, request, cancellationToken);
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    private static byte[] WithBom(string text)
    {
        return [.. Utf8WithBom.GetPreamble(), .. Utf8WithBom.GetBytes(text)];
    }

    private ExecutionResult? ValidateSource(PowerShellInvocation invocation, out string? scriptPath)
    {
        scriptPath = null;
        switch (invocation.Source)
        {
            case PowerShellSources.Inline:
                return string.IsNullOrWhiteSpace(invocation.Code)
                    ? ExecutionResult.Failure(ErrorCodes.Internal, "The PowerShell action has no code.")
                    : null;
            case PowerShellSources.File:
                if (string.IsNullOrEmpty(_options.ScriptsDirectory))
                {
                    return ExecutionResult.Failure(ErrorCodes.Internal, "No scripts directory is configured, so script files cannot run.");
                }

                if (!ScriptNames.IsValid(invocation.Script))
                {
                    return ExecutionResult.Failure(ErrorCodes.Internal, "The script name is not valid.");
                }

                string root = Path.GetFullPath(_options.ScriptsDirectory);
                string full = Path.GetFullPath(Path.Combine(root, invocation.Script!));
                string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(full))
                {
                    return ExecutionResult.Failure(ErrorCodes.Internal, $"The script '{invocation.Script}' does not exist in the scripts directory.");
                }

                if (_options.ScriptGuard is { } guard && invocation.RunAs != RunAsValues.User)
                {
                    ScriptAccessDecision decision = guard.Check(full, invocation.RunAs);
                    if (!decision.IsAllowed)
                    {
                        return ExecutionResult.Failure(
                            ErrorCodes.Internal,
                            decision.Reason ?? $"The script '{invocation.Script}' may not run with these rights.");
                    }
                }

                scriptPath = full;
                return null;
            default:
                return ExecutionResult.Failure(ErrorCodes.Internal, $"Unknown PowerShell source '{invocation.Source}'.");
        }
    }

    /// <summary>Returns the path of the script that the wrapper invokes, writing a file into the workspace when needed.</summary>
    private async Task<string> PrepareTargetAsync(
        PowerShellWorkspace workspace,
        PowerShellInvocation invocation,
        PowerShellEngine engine,
        string? scriptPath,
        CancellationToken cancellationToken)
    {
        if (scriptPath is null)
        {
            // Inline code: a UTF-8 file with byte order mark, which both engines read as UTF-8.
            return await workspace.WriteAsync(_files, "inline.ps1", WithBom(invocation.Code!), cancellationToken);
        }

        if (engine.IsWindowsPowerShell)
        {
            byte[] content = await File.ReadAllBytesAsync(scriptPath, cancellationToken);
            if (NeedsByteOrderMark(content))
            {
                // Windows PowerShell 5.1 reads a script without byte order mark in the ANSI code page, which
                // would corrupt every non-ASCII character. Run a copy that says it is UTF-8.
                byte[] withBom = [.. Utf8WithBom.GetPreamble(), .. content];
                return await workspace.WriteAsync(_files, Path.GetFileName(scriptPath), withBom, cancellationToken);
            }
        }

        return scriptPath;
    }

    private static bool NeedsByteOrderMark(byte[] content)
    {
        ReadOnlySpan<byte> bom = Utf8WithBom.GetPreamble();
        return !content.AsSpan().StartsWith(bom) && content.Any(value => value >= 0x80);
    }

    private string ResolveRoot()
    {
        return Path.GetFullPath(_options.TempRoot ?? Path.GetTempPath());
    }

    private void SweepOnce()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            SweepStale(ResolveRoot(), _options.StaleAfter, _time.GetUtcNow());
        }
    }
}

/// <summary>
/// A private directory for the files of one run. On Unix it is created with mode 0700 (the files inside with
/// 0600, see <see cref="IFileOperations.WriteAllBytesAsync"/>), so no other account can read the parameter
/// file, which may hold secret values. <see cref="Dispose"/> deletes it with everything inside.
/// </summary>
internal sealed class PowerShellWorkspace : IDisposable
{
    private PowerShellWorkspace(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static PowerShellWorkspace Create(string root)
    {
        string path = System.IO.Path.Combine(
            root,
            PowerShellRunner.WorkspacePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        // The root may not exist yet; its own permissions are those of its creator, only the per-run directory is private.
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new PowerShellWorkspace(path);
    }

    public async Task<string> WriteAsync(IFileOperations files, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        string file = System.IO.Path.Combine(Path, name);
        await files.WriteAllBytesAsync(file, content, ownerOnly: true, cancellationToken);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind (in use, or a crash of the file system): the sweep of a later run removes it.
        }
    }
}

/// <summary>
/// Executes <c>powershell</c> actions (PLAN.md S06, D-011, D-012) through the <see cref="PowerShellRunner"/>.
/// The target script declares a <c>param()</c> block that matches the command's parameters; a variable that the
/// script does not declare makes PowerShell fail with a parameter binding error, which ends the run with a
/// non-zero exit code and the message on standard error.
/// </summary>
public sealed class PowerShellActionHandler : IActionHandler
{
    private readonly PowerShellRunner _runner;

    /// <summary>Creates the handler.</summary>
    /// <param name="runner">Runs the engine.</param>
    public PowerShellActionHandler(PowerShellRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
    }

    /// <inheritdoc />
    public string ActionType => ActionTypes.PowerShell;

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Command.Action is not PowerShellAction action)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.Internal, "The command does not have a PowerShell action."));
        }

        var invocation = new PowerShellInvocation(
            action.Source,
            action.Code,
            action.Script,
            action.Engine,
            context.Variables,
            context.Environment,
            context.Timeout,
            context.Command.RunAs,
            context.Command.CaptureOutput);
        return _runner.RunAsync(invocation, context.Launcher, cancellationToken);
    }
}
