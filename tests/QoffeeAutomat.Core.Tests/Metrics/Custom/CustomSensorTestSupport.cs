using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Core.Tests.Execution.PowerShell;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>What the fake launcher saw of one engine start.</summary>
internal sealed record SensorCall(string SensorId, string RunAs, ProcessLaunchRequest Request, string Code, DateTimeOffset At);

/// <summary>
/// An <see cref="IProcessLauncher"/> that starts nothing and answers per sensor (the sensor id arrives in
/// <c>QA_SENSOR_ID</c>). It reads the script file that the wrapper would run while the call is in progress, so a
/// test can tell which code the sensor ran.
/// </summary>
internal sealed class ScriptLauncher : IProcessLauncher, IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly List<SensorCall> _calls = [];
    private readonly Dictionary<string, Func<ProcessLaunchRequest, CancellationToken, Task<ExecutionResult>>> _answers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _started = new(0);

    public ScriptLauncher(TimeProvider time)
    {
        _time = time;
    }

    public IReadOnlyList<SensorCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public int CallsFor(string sensorId) => Calls.Count(call => call.SensorId == sensorId);

    public void Answer(string sensorId, Func<ProcessLaunchRequest, CancellationToken, Task<ExecutionResult>> answer)
    {
        lock (_gate)
        {
            _answers[sensorId] = answer;
        }
    }

    public void Output(string sensorId, string standardOutput, int exitCode = 0, string standardError = "", bool truncated = false, string? errorCode = null)
    {
        Answer(sensorId, (_, _) => Task.FromResult(new ExecutionResult(exitCode, standardOutput, standardError, errorCode, truncated)));
    }

    public void Hang(string sensorId)
    {
        Answer(sensorId, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ExecutionResult(0, string.Empty, string.Empty);
        });
    }

    public void Throw(string sensorId, Exception exception)
    {
        Answer(sensorId, (_, _) => throw exception);
    }

    /// <summary>Waits until the launcher has been called <paramref name="count"/> more times than before.</summary>
    public async Task WaitForCallsAsync(int count = 1)
    {
        for (int index = 0; index < count; index++)
        {
            if (!await _started.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                throw new Xunit.Sdk.XunitException("The launcher was not called within 10 seconds.");
            }
        }
    }

    public async ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        string sensorId = request.EnvironmentVariables["QA_SENSOR_ID"];
        byte[] script = File.ReadAllBytes(request.EnvironmentVariables[PowerShellWrapper.TargetScriptVariable]);
        string code = new UTF8Encoding(false).GetString(script).TrimStart('﻿');
        Func<ProcessLaunchRequest, CancellationToken, Task<ExecutionResult>>? answer;
        lock (_gate)
        {
            _calls.Add(new SensorCall(sensorId, runAs, request, code, _time.GetUtcNow()));
            _answers.TryGetValue(sensorId, out answer);
        }

        _started.Release();
        return answer is null
            ? new ExecutionResult(0, string.Empty, string.Empty)
            : await answer(request, cancellationToken);
    }

    public void Dispose()
    {
        _started.Dispose();
    }
}

/// <summary>A process list that the test controls.</summary>
internal sealed class FakeProcessNames : IProcessNameSource
{
    private int _reads;

    public HashSet<string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Failure { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public IReadOnlySet<string> GetRunningProcessNames()
    {
        Interlocked.Increment(ref _reads);
        if (Failure is not null)
        {
            throw Failure;
        }

        return new HashSet<string>(Names, Names.Comparer);
    }
}

/// <summary>Builders for sensor documents.</summary>
internal static class Sensors
{
    public static SensorDocument Script(
        string id,
        string valueType = "number",
        double? interval = 5,
        IReadOnlyList<string>? options = null,
        string code = "'1'",
        string engine = PowerShellEngines.Auto,
        bool enabled = true) => new(
            DocumentSchema.CurrentVersion,
            id,
            id,
            enabled,
            false,
            valueType == "bool" ? "binary_sensor" : "sensor",
            valueType,
            new PowerShellSensorSource(PowerShellSources.Inline, Code: code, Engine: engine),
            IntervalSeconds: interval,
            Options: options);

    public static SensorDocument Process(string id, string process, double? interval = null) => new(
        DocumentSchema.CurrentVersion,
        id,
        id,
        true,
        false,
        "binary_sensor",
        "bool",
        new ProcessRunningSensorSource(process),
        IntervalSeconds: interval);

    /// <summary>A sensor of type <c>state</c> (T714); a state sensor is event-driven and has no interval.</summary>
    public static SensorDocument State(
        string id,
        string valueType = "number",
        object? initial = null,
        bool persist = false,
        IReadOnlyList<string>? options = null,
        int? precision = null) => new(
            DocumentSchema.CurrentVersion,
            id,
            id,
            true,
            false,
            valueType == "bool" ? "binary_sensor" : "sensor",
            valueType,
            new StateSensorSource(initial is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(initial), persist),
            Precision: precision,
            Options: options);

    public static string MetricId(string id) => "custom." + id;
}

/// <summary>A custom sensor provider on a real config store, the real PowerShell runner and a fake launcher, driven by a fake clock; optionally with a metric engine on top.</summary>
internal sealed class SensorHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp;
    private readonly StateSensorStore _states;

    private SensorHarness(
        TempDirectory temp,
        SimulatedClock clock,
        ConfigStore store,
        ScriptLauncher launcher,
        FakeProcessNames processes,
        FakeEngineLocator locator,
        CustomSensorProvider provider,
        MetricEngine engine,
        StateSensorStore states)
    {
        _temp = temp;
        _states = states;
        Clock = clock;
        Store = store;
        Launcher = launcher;
        Processes = processes;
        Locator = locator;
        Provider = provider;
        Engine = engine;
    }

    public SimulatedClock Clock { get; }

    public ConfigStore Store { get; }

    public ScriptLauncher Launcher { get; }

    public FakeProcessNames Processes { get; }

    public FakeEngineLocator Locator { get; }

    public CustomSensorProvider Provider { get; }

    /// <summary>The values of the state sensors (T714).</summary>
    public StateSensorStore States => _states;

    public MetricEngine Engine { get; }

    /// <summary>The per-run directories that the PowerShell runner left behind.</summary>
    public string[] RunDirectories
    {
        get
        {
            string root = _temp.Combine("run");
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
    }

    public static async Task<SensorHarness> CreateAsync(
        CustomSensorOptions? options = null,
        MetricEngineOptions? engineOptions = null,
        FakeEngineLocator? locator = null,
        IReadOnlyList<IMetricProvider>? providers = null,
        IReadOnlyList<ISensorSourceHandler>? handlers = null)
    {
        var temp = new TempDirectory();
        var clock = new SimulatedClock();
        ConfigStore store = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp), clock);
        var launcher = new ScriptLauncher(clock);
        var processes = new FakeProcessNames();
        FakeEngineLocator engineLocator = locator ?? FakeEngineLocator.CreatePwsh();
        var runner = new PowerShellRunner(
            engineLocator,
            new PowerShellOptions { ScriptsDirectory = temp.Combine("scripts"), TempRoot = temp.Combine("run") },
            clock);
        var states = new StateSensorStore(store, clock, temp.Combine(StateFileName));
        var provider = new CustomSensorProvider(store, runner, launcher, clock, processes, options, engineOptions, handlers, states);
        var engine = new MetricEngine(store, providers ?? [], clock, engineOptions, session: null, customSensors: provider);

        // What the agent host does (T714): a value that was set is published at once.
        states.Changed += (_, change) => engine.RequestSample(change.MetricId);
        return new SensorHarness(temp, clock, store, launcher, processes, engineLocator, provider, engine, states);
    }

    /// <summary>The file name of the kept state values below the harness directory.</summary>
    public const string StateFileName = "state-values.json";

    /// <summary>The full path of the file that keeps the values of state sensors with persist.</summary>
    public string StatePath => _temp.Combine(StateFileName);

    /// <summary>A second store on the same configuration and file, the way a restarted agent finds them: nothing is in memory, only what was written.</summary>
    public StateSensorStore RestartedStates() => new(Store, Clock, StatePath);

    public async Task SaveAsync(params SensorDocument[] sensors)
    {
        foreach (SensorDocument sensor in sensors)
        {
            SaveResult<SensorDocument> result = await Store.Local.SaveAsync(sensor, null, CancellationToken.None);
            if (!result.IsApplied)
            {
                throw new InvalidOperationException(
                    $"Saving '{sensor.Id}' failed: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}")));
            }
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> SampleAsync(params string[] sensorIds)
    {
        return await Provider.SampleAsync([.. sensorIds.Select(Sensors.MetricId)], CancellationToken.None);
    }

    /// <summary>Moves the clock and lets the engine do what is due.</summary>
    public async Task TickAsync(TimeSpan step, int count = 1)
    {
        for (int index = 0; index < count; index++)
        {
            Clock.Advance(step);
            await Engine.PollAsync(CancellationToken.None);
        }
    }

    public static List<MetricUpdate> Drain(MetricSubscription subscription)
    {
        var updates = new List<MetricUpdate>();
        while (subscription.Updates.TryRead(out MetricUpdate? update))
        {
            updates.Add(update);
        }

        return updates;
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        _states.Dispose();
        Launcher.Dispose();
        Store.Dispose();
        _temp.Dispose();
    }
}
