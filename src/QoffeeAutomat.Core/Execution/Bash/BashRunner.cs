using System.Globalization;
using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Execution.Bash;

/// <summary>Settings of <see cref="BashRunner"/>.</summary>
public sealed class BashOptions
{
    /// <summary>The default for <see cref="StaleAfter"/>: longer than the longest command timeout (24 hours).</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(48);

    /// <summary>The scripts directory that <c>source: file</c> refers to; null makes file scripts fail with a clear error.</summary>
    public string? ScriptsDirectory { get; init; }

    /// <summary>
    /// The directory below which every run with inline code gets a private directory (mode 0700, the script inside 0600); null uses the
    /// system temp directory. The agent passes a directory below its own data directory, which only root can read.
    /// </summary>
    public string? TempRoot { get; init; }

    /// <summary>File operations used to write the temporary files; null uses the real file system. A seam for fault injection in tests.</summary>
    public IFileOperations? FileOperations { get; init; }

    /// <summary>Directories of runs that crashed (and so were not cleaned up) are removed after this age, once per runner.</summary>
    public TimeSpan StaleAfter { get; init; } = DefaultStaleAfter;

    /// <summary>
    /// Checks script files before they run with more rights than the user's (PLAN.md S09 item 5); null runs every script. A refused
    /// script ends the run with <see cref="ErrorCodes.Internal"/>.
    /// </summary>
    public IScriptAccessGuard? ScriptGuard { get; init; }

    /// <summary>
    /// Runs the invocations of <c>runAs: user</c> in the session of the interactive user (PLAN.md S11); null runs them through the
    /// launcher like every other invocation. The session helper has none: there the runner already is in the session.
    /// </summary>
    public IUserContextBash? UserSession { get; init; }
}

/// <summary>Everything the runner needs to execute one bash script.</summary>
/// <param name="Source"><see cref="BashSources.Inline"/> or <see cref="BashSources.File"/>.</param>
/// <param name="Code">The script when the source is inline.</param>
/// <param name="Script">The file name in the scripts directory when the source is a file.</param>
/// <param name="Arguments">
/// The positional arguments <c>$1</c>, <c>$2</c> and so on, one entry per argument. They are separate argv entries of the shell, never
/// joined into a string and never part of the script text.
/// </param>
/// <param name="Environment">The environment variables of the process (<c>QA_*</c>, <c>QA_VAR_*</c>).</param>
/// <param name="Timeout">How long the shell may run before its process tree is killed.</param>
/// <param name="RunAs">One of <see cref="RunAsValues"/>.</param>
/// <param name="CaptureOutput">Whether standard output and error are captured.</param>
public sealed record BashInvocation(
    string Source,
    string? Code,
    string? Script,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    string RunAs = RunAsValues.System,
    bool CaptureOutput = true);

/// <summary>
/// Runs bash the way PLAN.md D-012 asks for every script language (T803): out of process, with the variable values only in the
/// environment (<c>QA_VAR_&lt;NAME&gt;</c>) and as positional arguments (<c>$1</c>, <c>$2</c> and so on), and never in the text of the script or
/// in a shell command line. The shell is started as <c>bash --noprofile --norc &lt;script&gt; &lt;argument&gt;...</c> through the
/// <see cref="IProcessLauncher"/>, without an intermediate shell; every argument arrives as one argv entry whatever characters it holds.
/// Inline code is written into a private per-run directory and deleted when the shell ended, however it ended.
/// </summary>
public sealed class BashRunner
{
    /// <summary>The name prefix of the per-run directories.</summary>
    public const string WorkspacePrefix = "qa-sh-";

    /// <summary>The name of the file that holds inline code inside the per-run directory.</summary>
    public const string InlineFileName = "inline.sh";

    /// <summary>The largest script that is run, inline or from a file; a script is read into memory.</summary>
    public const int MaxScriptBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IBashLocator _locator;
    private readonly BashOptions _options;
    private readonly TimeProvider _time;
    private readonly IFileOperations _files;
    private int _swept;

    /// <summary>Creates the runner.</summary>
    /// <param name="locator">Finds bash; null searches this computer.</param>
    /// <param name="options">Directories and tuning; null uses the defaults.</param>
    /// <param name="time">The clock, used to age out directories of crashed runs; null uses the system clock.</param>
    public BashRunner(IBashLocator? locator = null, BashOptions? options = null, TimeProvider? time = null)
    {
        _locator = locator ?? new BashLocator();
        _options = options ?? new BashOptions();
        _time = time ?? TimeProvider.System;
        _files = _options.FileOperations ?? FileOperations.Instance;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.StaleAfter, TimeSpan.Zero, nameof(options));
    }

    /// <summary>Looks for bash without running anything; the UI uses it to tell the user what is installed.</summary>
    /// <returns>The path or the reason it is missing.</returns>
    public BashResolution ResolveShell() => _locator.Resolve();

    /// <summary>
    /// Removes per-run directories under <paramref name="root"/> that were last written to before <paramref name="now"/> minus
    /// <paramref name="olderThan"/>. A directory that cannot be removed (it belongs to another account, or is in use) is skipped.
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

    /// <summary>Tells whether a text may be passed to a program as an argument or an environment variable: Unix cannot carry a NUL character.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True when the text has no NUL character.</returns>
    public static bool IsPassable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !text.Contains('\0', StringComparison.Ordinal);
    }

    /// <summary>Runs the script and waits for the shell to end.</summary>
    /// <param name="invocation">What to run.</param>
    /// <param name="launcher">Starts the shell in the run context of the command.</param>
    /// <param name="cancellationToken">Cancelling it kills the shell's process tree.</param>
    /// <returns>
    /// The outcome of the shell process: the exit code of the script, or <c>timeout</c>/<c>cancelled</c>. A missing shell, a missing script,
    /// a value that cannot be passed or an unusable temporary directory is a failure with error <see cref="ErrorCodes.Internal"/> or
    /// <see cref="ErrorCodes.InvalidVars"/> and a message that never contains variable values.
    /// </returns>
    public async ValueTask<ExecutionResult> RunAsync(
        BashInvocation invocation,
        IProcessLauncher launcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(invocation.Arguments);
        ArgumentNullException.ThrowIfNull(invocation.Environment);

        ExecutionResult? unusable = CheckPassable(invocation);
        if (unusable is not null)
        {
            return unusable;
        }

        ExecutionResult? invalid = ValidateSource(invocation, out string? scriptPath);
        if (invalid is not null)
        {
            return invalid;
        }

        if (invocation.RunAs == RunAsValues.User && _options.UserSession is { } userSession)
        {
            // The scripts directory belongs to the service account; the user's helper could not read it. The script is read here and
            // travels as inline code, which the helper writes into its own private directory.
            BashInvocation inline = invocation;
            if (scriptPath is not null)
            {
                string? text = ReadScript(scriptPath, out ExecutionResult? readFailure);
                if (text is null)
                {
                    return readFailure!;
                }

                inline = invocation with { Source = BashSources.Inline, Code = text, Script = null };
            }

            return await userSession.RunAsync(inline, cancellationToken);
        }

        BashResolution resolution = _locator.Resolve();
        if (resolution.Path is not { } shell)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, resolution.Reason ?? "bash is not available.");
        }

        BashWorkspace? workspace = null;
        try
        {
            ProcessLaunchRequest request;
            try
            {
                string target;
                if (scriptPath is not null)
                {
                    target = scriptPath;
                }
                else
                {
                    SweepOnce();
                    workspace = BashWorkspace.Create(ResolveRoot());

                    // Windows line endings of an editor would end every command in a carriage return, which bash does not skip.
                    string code = invocation.Code!.Replace("\r\n", "\n", StringComparison.Ordinal);
                    target = await workspace.WriteAsync(_files, InlineFileName, Utf8.GetBytes(code), cancellationToken);
                }

                request = BuildRequest(shell, target, invocation);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return ExecutionResult.Failure(
                    ErrorCodes.Internal,
                    "The temporary file for the bash run could not be written: " + exception.Message);
            }

            return await launcher.LaunchAsync(invocation.RunAs, request, cancellationToken);
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    /// <summary>
    /// The command line of the shell: <c>--noprofile --norc &lt;script&gt;</c> followed by the positional arguments. The script is a path
    /// that the runner created or validated; the arguments are the values, each one argv entry.
    /// </summary>
    /// <param name="scriptPath">The full path of the script file.</param>
    /// <param name="arguments">The positional arguments.</param>
    /// <returns>The argv entries after the shell's own name.</returns>
    public static IReadOnlyList<string> BuildArguments(string scriptPath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(arguments);

        return ["--noprofile", "--norc", scriptPath, .. arguments];
    }

    private static ProcessLaunchRequest BuildRequest(string shell, string target, BashInvocation invocation)
    {
        // BASH_ENV names a file that a non-interactive bash sources before the script; --norc does not stop it. The service never sets it,
        // so an empty value only closes the way for an inherited one. A command that really wants it can pass its own.
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BASH_ENV"] = string.Empty };
        foreach ((string name, string value) in invocation.Environment)
        {
            environment[name] = value;
        }

        return new ProcessLaunchRequest(
            shell,
            BuildArguments(target, invocation.Arguments),
            WorkingDirectory: null,
            environment,
            invocation.Timeout,
            WaitForExit: true,
            invocation.CaptureOutput);
    }

    private static ExecutionResult? CheckPassable(BashInvocation invocation)
    {
        foreach (string argument in invocation.Arguments)
        {
            if (argument is null || !IsPassable(argument))
            {
                return ExecutionResult.Failure(ErrorCodes.InvalidVars, "A value contains a NUL character, which a bash script cannot receive.");
            }
        }

        foreach ((string name, string value) in invocation.Environment)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal) || !IsPassable(name))
            {
                return ExecutionResult.Failure(ErrorCodes.Internal, "An environment variable has an invalid name.");
            }

            if (value is null || !IsPassable(value))
            {
                return ExecutionResult.Failure(ErrorCodes.InvalidVars, "A value contains a NUL character, which a bash script cannot receive.");
            }
        }

        return null;
    }

    private ExecutionResult? ValidateSource(BashInvocation invocation, out string? scriptPath)
    {
        scriptPath = null;
        switch (invocation.Source)
        {
            case BashSources.Inline:
                if (string.IsNullOrWhiteSpace(invocation.Code))
                {
                    return ExecutionResult.Failure(ErrorCodes.Internal, "The bash action has no script.");
                }

                return Utf8.GetByteCount(invocation.Code) > MaxScriptBytes
                    ? ExecutionResult.Failure(ErrorCodes.Internal, "The bash script is larger than 1 MiB.")
                    : null;
            case BashSources.File:
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
                return ExecutionResult.Failure(ErrorCodes.Internal, $"Unknown bash source '{invocation.Source}'.");
        }
    }

    private static string? ReadScript(string path, out ExecutionResult? failure)
    {
        failure = null;
        try
        {
            if (new FileInfo(path).Length > MaxScriptBytes)
            {
                failure = ExecutionResult.Failure(ErrorCodes.Internal, "The bash script is larger than 1 MiB.");
                return null;
            }

            return Utf8.GetString(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = ExecutionResult.Failure(ErrorCodes.Internal, "The script file could not be read: " + exception.Message);
            return null;
        }
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
/// A private directory for the script of one run. On Unix it is created with mode 0700 and the file inside with 0600 (see
/// <see cref="IFileOperations.WriteAllBytesAsync"/>), so no other account can read the script, which may hold a value that an
/// automation put there. <see cref="Dispose"/> deletes it with everything inside.
/// </summary>
internal sealed class BashWorkspace : IDisposable
{
    private BashWorkspace(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static BashWorkspace Create(string root)
    {
        string path = System.IO.Path.Combine(
            root,
            BashRunner.WorkspacePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(root);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new BashWorkspace(path);
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
/// Executes <c>bash</c> actions (PLAN.md S06, S17, D-012, T803) through the <see cref="BashRunner"/>. Every value reaches the script twice
/// and in no other way: as the environment variable <c>QA_VAR_&lt;NAME&gt;</c> (which the engine puts into the context) and as a positional
/// argument. The positional arguments follow the order of the command's <c>parameters</c>: <c>$1</c> is the first declared parameter,
/// <c>$2</c> the second. A declared parameter without value (optional, no default) is the empty string, so that the position of the others
/// does not move. Booleans are <c>true</c> and <c>false</c>, numbers use the invariant culture.
/// </summary>
public sealed class BashActionHandler : IActionHandler
{
    private readonly BashRunner _runner;

    /// <summary>Creates the handler.</summary>
    /// <param name="runner">Runs the shell.</param>
    public BashActionHandler(BashRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
    }

    /// <inheritdoc />
    public string ActionType => ActionTypes.Bash;

    /// <summary>The positional arguments of a run: one per declared parameter, in declaration order.</summary>
    /// <param name="parameters">The parameters of the command; null for none.</param>
    /// <param name="variables">The validated variables.</param>
    /// <returns>The arguments.</returns>
    public static IReadOnlyList<string> PositionalArguments(
        IReadOnlyList<ParameterDefinition>? parameters,
        IReadOnlyDictionary<string, object> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        if (parameters is null)
        {
            return [];
        }

        var arguments = new List<string>(parameters.Count);
        foreach (ParameterDefinition parameter in parameters)
        {
            arguments.Add(variables.TryGetValue(parameter.Name, out object? value) ? ActionContext.FormatValue(value) : string.Empty);
        }

        return arguments;
    }

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Command.Action is not BashAction action)
        {
            return ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.Internal, "The command does not have a bash action."));
        }

        var invocation = new BashInvocation(
            action.Source,
            action.Code,
            action.Script,
            PositionalArguments(context.Command.Parameters, context.Variables),
            context.Environment,
            context.Timeout,
            context.Command.RunAs,
            context.Command.CaptureOutput);
        return _runner.RunAsync(invocation, context.Launcher, cancellationToken);
    }
}
