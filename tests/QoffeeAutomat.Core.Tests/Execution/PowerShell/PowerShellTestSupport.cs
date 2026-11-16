using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Tests.Execution.PowerShell;

/// <summary>A test that needs PowerShell 7 (<c>pwsh</c>); it is reported as skipped when the machine has none.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PwshFactAttribute : FactAttribute
{
    public PwshFactAttribute()
    {
        PowerShellEngineResolution pwsh = PowerShellTestSupport.Pwsh.Value;
        if (!pwsh.IsFound)
        {
            Skip = "PowerShell 7 (pwsh) is not installed on this machine.";
        }
    }
}

/// <summary>A test that needs Windows PowerShell 5.1 (<c>powershell.exe</c>); it is reported as skipped elsewhere.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsPowerShellFactAttribute : FactAttribute
{
    public WindowsPowerShellFactAttribute()
    {
        PowerShellEngineResolution windowsPowerShell = PowerShellTestSupport.WindowsPowerShell.Value;
        if (!windowsPowerShell.IsFound)
        {
            Skip = "Windows PowerShell (powershell.exe) is not available on this machine.";
        }
    }
}

/// <summary>Builders and helpers shared by the PowerShell tests.</summary>
internal static class PowerShellTestSupport
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static readonly Lazy<PowerShellEngineResolution> Pwsh = new(
        () => new PowerShellEngineLocator().Resolve(PowerShellEngines.Pwsh));

    public static readonly Lazy<PowerShellEngineResolution> WindowsPowerShell = new(
        () => new PowerShellEngineLocator().Resolve(PowerShellEngines.PowerShell));

    public static CommandDocument Command(
        CommandAction action,
        string runAs = RunAsValues.System,
        bool captureOutput = true,
        int timeoutSeconds = 60,
        params ParameterDefinition[] parameters) => new(
            DocumentSchema.CurrentVersion,
            "ps_test",
            "ps_test",
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
        return new ActionContext(
            command,
            variables ?? new Dictionary<string, object>(),
            "run1",
            RunSources.Ui,
            "Test-PC",
            timeout ?? TimeSpan.FromSeconds(30),
            environment ?? new Dictionary<string, string> { ["QA_RUN_ID"] = "run1" },
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

    /// <summary>Reads the parameter file the wrapper gets: name to (type, value text).</summary>
    public static Dictionary<string, (string Type, string Value)> ReadParameters(byte[] content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        Assert.Equal(PowerShellParameterFile.Version, document.RootElement.GetProperty("version").GetInt32());

        var result = new Dictionary<string, (string Type, string Value)>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.GetProperty("parameters").EnumerateObject())
        {
            result[property.Name] = (
                property.Value.GetProperty("type").GetString()!,
                property.Value.GetProperty("value").GetString()!);
        }

        return result;
    }
}

/// <summary>A locator that always answers with the same engine (or the same reason for its absence).</summary>
internal sealed class FakeEngineLocator : IPowerShellEngineLocator
{
    private readonly PowerShellEngineResolution _resolution;

    public FakeEngineLocator(PowerShellEngine engine)
    {
        _resolution = PowerShellEngineResolution.Found(engine);
    }

    public FakeEngineLocator(string missingReason)
    {
        _resolution = PowerShellEngineResolution.Missing(missingReason);
    }

    public List<string> Requested { get; } = [];

    /// <summary>A locator that finds PowerShell 7 at a fake path; each test gets its own, so the request log is not shared.</summary>
    public static FakeEngineLocator CreatePwsh() => new(new PowerShellEngine(PowerShellEngines.Pwsh, "/fake/bin/pwsh"));

    /// <summary>A locator that finds Windows PowerShell at a fake path.</summary>
    public static FakeEngineLocator CreateWindowsPowerShell() =>
        new(new PowerShellEngine(PowerShellEngines.PowerShell, "/fake/bin/powershell.exe"));

    public PowerShellEngineResolution Resolve(string engine)
    {
        Requested.Add(engine);
        return _resolution;
    }
}

/// <summary>
/// A launcher that does not start anything. What the test wants to know about the files at the moment of
/// the launch is captured by <see cref="OnLaunch"/>, because the run directory is gone afterwards.
/// </summary>
internal sealed class RecordingLauncher : IProcessLauncher
{
    public List<(string RunAs, ProcessLaunchRequest Request)> Launches { get; } = [];

    public Func<ProcessLaunchRequest, ExecutionResult> OnLaunch { get; set; } = _ => new ExecutionResult(0, string.Empty, string.Empty);

    public ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        Launches.Add((runAs, request));
        return ValueTask.FromResult(OnLaunch(request));
    }
}

/// <summary>Everything a test can see of the run directory while the engine would be running.</summary>
internal sealed record LaunchSnapshot(
    string WrapperPath,
    string WrapperText,
    byte[] WrapperBytes,
    byte[] ParameterBytes,
    string TargetPath,
    byte[] TargetBytes,
    string[] FilesInRunDirectory,
    string RunDirectory);

/// <summary>A runner on real files with a recording launcher: the handler and the runner without an engine.</summary>
internal sealed class RunnerHarness : IDisposable
{
    public RunnerHarness(IPowerShellEngineLocator? locator = null, Action<TempDirectory>? seed = null, Func<string, PowerShellOptions>? options = null)
    {
        Temp = new TempDirectory();
        seed?.Invoke(Temp);
        ScriptsDirectory = Temp.Combine("scripts");
        TempRoot = Temp.Combine("run");
        Directory.CreateDirectory(ScriptsDirectory);
        Locator = locator ?? FakeEngineLocator.CreatePwsh();
        Runner = new PowerShellRunner(
            Locator,
            options?.Invoke(Temp.FullPath) ?? new PowerShellOptions { ScriptsDirectory = ScriptsDirectory, TempRoot = TempRoot });
        Handler = new PowerShellActionHandler(Runner);
        Launcher = new RecordingLauncher();
    }

    public TempDirectory Temp { get; }

    public string ScriptsDirectory { get; }

    public string TempRoot { get; }

    public IPowerShellEngineLocator Locator { get; }

    public PowerShellRunner Runner { get; }

    public PowerShellActionHandler Handler { get; }

    public RecordingLauncher Launcher { get; }

    /// <summary>The run directories that are currently below the temp root.</summary>
    public string[] RunDirectories => Directory.Exists(TempRoot) ? Directory.GetDirectories(TempRoot) : [];

    public string WriteScript(string name, byte[] content)
    {
        string path = Path.Combine(ScriptsDirectory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public PowerShellInvocation Invocation(
        string source = PowerShellSources.Inline,
        string? code = "'x'",
        string? script = null,
        string engine = PowerShellEngines.Auto,
        IReadOnlyDictionary<string, object>? variables = null,
        string runAs = RunAsValues.System) => new(
            source,
            code,
            script,
            engine,
            variables ?? new Dictionary<string, object>(),
            new Dictionary<string, string> { ["QA_RUN_ID"] = "run1" },
            TimeSpan.FromSeconds(30),
            runAs);

    /// <summary>Makes the launcher record a <see cref="LaunchSnapshot"/> of the files at the moment of the launch.</summary>
    public List<LaunchSnapshot> CaptureSnapshots()
    {
        var snapshots = new List<LaunchSnapshot>();
        Launcher.OnLaunch = request =>
        {
            string wrapper = request.Arguments[^1];
            string target = request.EnvironmentVariables[PowerShellWrapper.TargetScriptVariable];
            string parameters = request.EnvironmentVariables[PowerShellWrapper.ParametersFileVariable];
            string runDirectory = Path.GetDirectoryName(wrapper)!;
            byte[] wrapperBytes = File.ReadAllBytes(wrapper);
            snapshots.Add(new LaunchSnapshot(
                wrapper,
                System.Text.Encoding.UTF8.GetString(wrapperBytes),
                wrapperBytes,
                File.ReadAllBytes(parameters),
                target,
                File.ReadAllBytes(target),
                [.. Directory.GetFiles(runDirectory).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)],
                runDirectory));
            return new ExecutionResult(0, string.Empty, string.Empty);
        };
        return snapshots;
    }

    public void Dispose()
    {
        Temp.Dispose();
    }
}
