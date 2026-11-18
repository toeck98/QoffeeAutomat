using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution.Bash;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Execution.PowerShell;

namespace QoffeeAutomat.Core.Tests.Execution.Bash;

/// <summary>A test that needs a real bash; it is reported as skipped on Windows and where bash is not installed.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BashFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    public BashFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs a Unix system with bash.";
        }
        else if (!BashTestSupport.RealBash.Value.IsFound)
        {
            Skip = "bash is not installed on this machine.";
        }
    }
}

/// <summary>A theory that needs a real bash; it is reported as skipped on Windows and where bash is not installed.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BashTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates the attribute.</summary>
    public BashTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs a Unix system with bash.";
        }
        else if (!BashTestSupport.RealBash.Value.IsFound)
        {
            Skip = "bash is not installed on this machine.";
        }
    }
}

/// <summary>Builders and helpers shared by the bash tests.</summary>
internal static class BashTestSupport
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Quotes, semicolons, dollar signs, backticks, subshells, redirections, globs and newlines: the characters of the T208 injection test, for bash.</summary>
    public const string Hostile = "it's \"quoted\"; $HOME `whoami` $(id) & | > < \n second line \\ é✓ * ~ ${IFS}";

    public static readonly Lazy<BashResolution> RealBash = new(() => new BashLocator().Resolve());

    public static CommandDocument Command(
        CommandAction action,
        string runAs = RunAsValues.System,
        bool captureOutput = true,
        int timeoutSeconds = 60,
        params ParameterDefinition[] parameters) => new(
            DocumentSchema.CurrentVersion,
            "bash_test",
            "bash_test",
            true,
            false,
            runAs,
            action,
            Parameters: parameters.Length == 0 ? null : parameters,
            TimeoutSeconds: timeoutSeconds,
            CaptureOutput: captureOutput);

    public static ActionContext Context(
        CommandDocument command,
        IProcessLauncher launcher,
        IReadOnlyDictionary<string, object>? variables = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        IReadOnlyDictionary<string, object> values = variables ?? new Dictionary<string, object>();

        // What the command engine puts into the environment of every run: the run context and QA_VAR_<NAME> for every value.
        var env = new Dictionary<string, string>(environment ?? new Dictionary<string, string> { ["QA_RUN_ID"] = "run1" }, StringComparer.Ordinal);
        foreach ((string name, object value) in values)
        {
            env["QA_VAR_" + name.ToUpperInvariant()] = ActionContext.FormatValue(value);
        }

        return new ActionContext(
            command,
            values,
            "run1",
            RunSources.Ui,
            "Test-PC",
            timeout ?? TimeSpan.FromSeconds(30),
            env,
            launcher);
    }

    public static Dictionary<string, object> Vars(params (string Name, object Value)[] values)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((string name, object value) in values)
        {
            result[name] = value;
        }

        return result;
    }

    public static ParameterDefinition Text(string name, bool required = false) => new(name, ParameterTypes.Text, Required: required);

    public static ParameterDefinition Whole(string name) => new(name, ParameterTypes.WholeNumber);

    public static ParameterDefinition Real(string name) => new(name, ParameterTypes.Real);

    public static ParameterDefinition Flag(string name) => new(name, ParameterTypes.Flag);
}

/// <summary>A locator that always answers with the same bash (or the same reason for its absence).</summary>
internal sealed class FakeBashLocator : IBashLocator
{
    private readonly BashResolution _resolution;

    public FakeBashLocator(string path)
    {
        _resolution = BashResolution.Found(path);
    }

    private FakeBashLocator(BashResolution resolution)
    {
        _resolution = resolution;
    }

    public int Calls { get; private set; }

    public static FakeBashLocator Missing(string reason) => new(BashResolution.Missing(reason));

    public BashResolution Resolve()
    {
        Calls++;
        return _resolution;
    }
}

/// <summary>A user session that records what it was asked to run and answers with a configurable result.</summary>
internal sealed class RecordingUserBash : IUserContextBash
{
    public List<BashInvocation> Invocations { get; } = [];

    public ValueTask<ExecutionResult> RunAsync(BashInvocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Add(invocation);
        return ValueTask.FromResult(new ExecutionResult(0, "in the session", string.Empty));
    }
}

/// <summary>Everything a test can see of the run directory while the shell would be running.</summary>
internal sealed record BashLaunchSnapshot(
    string ScriptPath,
    string ScriptText,
    byte[] ScriptBytes,
    string[] FilesInRunDirectory,
    string RunDirectory,
    UnixFileMode? FileMode,
    UnixFileMode? DirectoryMode);

/// <summary>A runner on real files with a recording launcher: the handler and the runner without a shell.</summary>
internal sealed class BashHarness : IDisposable
{
    public const string FakeShell = "/fake/bin/bash";

    public BashHarness(IBashLocator? locator = null, Func<string, BashOptions>? options = null)
    {
        Temp = new TempDirectory();
        ScriptsDirectory = Temp.Combine("scripts");
        TempRoot = Temp.Combine("run");
        Directory.CreateDirectory(ScriptsDirectory);
        Locator = locator ?? new FakeBashLocator(FakeShell);
        Runner = new BashRunner(
            Locator,
            options?.Invoke(Temp.FullPath) ?? new BashOptions { ScriptsDirectory = ScriptsDirectory, TempRoot = TempRoot });
        Handler = new BashActionHandler(Runner);
        Launcher = new RecordingLauncher();
    }

    public TempDirectory Temp { get; }

    public string ScriptsDirectory { get; }

    public string TempRoot { get; }

    public IBashLocator Locator { get; }

    public BashRunner Runner { get; }

    public BashActionHandler Handler { get; }

    public RecordingLauncher Launcher { get; }

    /// <summary>The run directories that are currently below the temp root.</summary>
    public string[] RunDirectories => Directory.Exists(TempRoot) ? Directory.GetDirectories(TempRoot) : [];

    public string WriteScript(string name, string text)
    {
        string path = Path.Combine(ScriptsDirectory, name);
        File.WriteAllText(path, text);
        return path;
    }

    public BashInvocation Invocation(
        string source = BashSources.Inline,
        string? code = "echo x",
        string? script = null,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string runAs = RunAsValues.System) => new(
            source,
            code,
            script,
            arguments ?? [],
            environment ?? new Dictionary<string, string> { ["QA_RUN_ID"] = "run1" },
            TimeSpan.FromSeconds(30),
            runAs);

    /// <summary>Makes the launcher record a <see cref="BashLaunchSnapshot"/> of the files at the moment of the launch.</summary>
    public List<BashLaunchSnapshot> CaptureSnapshots()
    {
        var snapshots = new List<BashLaunchSnapshot>();
        Launcher.OnLaunch = request =>
        {
            string script = request.Arguments[2];
            string runDirectory = Path.GetDirectoryName(script)!;
            byte[] bytes = File.ReadAllBytes(script);
            UnixFileMode? fileMode = null;
            UnixFileMode? directoryMode = null;
            if (!OperatingSystem.IsWindows())
            {
                fileMode = File.GetUnixFileMode(script);
                directoryMode = File.GetUnixFileMode(runDirectory);
            }

            snapshots.Add(new BashLaunchSnapshot(
                script,
                System.Text.Encoding.UTF8.GetString(bytes),
                bytes,
                [.. Directory.GetFiles(runDirectory).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)],
                runDirectory,
                fileMode,
                directoryMode));
            return new ExecutionResult(0, string.Empty, string.Empty);
        };
        return snapshots;
    }

    public void Dispose()
    {
        Temp.Dispose();
    }
}
