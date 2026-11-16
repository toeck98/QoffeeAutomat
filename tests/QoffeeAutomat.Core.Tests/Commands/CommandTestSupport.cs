using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Commands;

/// <summary>
/// An action handler for the type <c>powershell</c> (the type that T208 implements) that the test
/// controls: every call blocks until the test completes it, or until the engine cancels it. It records
/// the calls in the order they started.
/// </summary>
internal sealed class GateActionHandler : IActionHandler, IDisposable
{
    private readonly object _gate = new();
    private readonly List<GateCall> _calls = [];
    private readonly SemaphoreSlim _started = new(0);

    /// <summary>When set, calls complete at once with this result instead of waiting.</summary>
    public Func<GateCall, ExecutionResult>? AutoComplete { get; set; }

    /// <summary>When set, calls throw it.</summary>
    public Exception? Failure { get; set; }

    public string ActionType => ActionTypes.PowerShell;

    public IReadOnlyList<GateCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public async ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var call = new GateCall(context);
        lock (_gate)
        {
            _calls.Add(call);
        }

        _started.Release();

        if (Failure is not null)
        {
            throw Failure;
        }

        if (AutoComplete is { } auto)
        {
            return auto(call);
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            call.WasCancelled = true;
            call.Completion.TrySetResult(ExecutionResult.Failure(ErrorCodes.Cancelled, "cancelled by the engine"));
        });
        return await call.Completion.Task;
    }

    /// <summary>Waits until a call has started that has not been waited for yet.</summary>
    public async Task<GateCall> NextStartAsync()
    {
        if (!await _started.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            throw new Xunit.Sdk.XunitException("No call reached the handler within 10 seconds.");
        }

        lock (_gate)
        {
            int index = _consumed++;
            return _calls[index];
        }
    }

    private int _consumed;

    public void Dispose() => _started.Dispose();
}

internal sealed class GateCall
{
    public GateCall(ActionContext context)
    {
        Context = context;
    }

    public ActionContext Context { get; }

    public string CommandId => Context.Command.Id;

    public IReadOnlyDictionary<string, object> Variables => Context.Variables;

    public TaskCompletionSource<ExecutionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public volatile bool WasCancelled;

    public void Finish(int exitCode = 0, string standardOutput = "", string standardError = "")
    {
        Completion.TrySetResult(new ExecutionResult(exitCode, standardOutput, standardError));
    }
}

/// <summary>A process runner that records requests and answers with a configurable result; it never starts a process.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessLaunchRequest> _requests = [];

    public Func<ProcessLaunchRequest, ProcessRunResult>? Answer { get; set; }

    public IReadOnlyList<ProcessLaunchRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public IProcessRun Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        ProcessRunResult result = Answer?.Invoke(request) ?? new ProcessRunResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, false);
        return Task.FromResult(result);
    }
}

/// <summary>A history that cannot store anything.</summary>
internal sealed class FailingRunHistory : IRunHistory
{
    public Task AddAsync(RunRecord record, CancellationToken cancellationToken) => throw new InvalidOperationException("The history database is locked.");

    public Task<RunRecord?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<RunRecord?>(null);

    public Task<IReadOnlyList<RunRecord>> QueryAsync(RunQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunRecord>>([]);

    public Task<int> PruneAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Everything a command engine test needs, wired to a fake clock and a temporary directory.</summary>
internal sealed class CommandHarness : IAsyncDisposable
{
    private readonly SqliteRunHistory? _sqliteHistory;
    private readonly SqliteEventStore _eventStore;

    private CommandHarness(
        TempDirectory temp,
        SimulatedClock clock,
        SimulatedPlatform platform,
        ConfigStore config,
        FakeProcessRunner runner,
        GateActionHandler? gate,
        SqliteRunHistory? sqliteHistory,
        IRunHistory history,
        SqliteEventStore eventStore,
        EventBus bus,
        CommandRegistry registry,
        AgentPause pause,
        CommandEngine engine)
    {
        Temp = temp;
        Clock = clock;
        Platform = platform;
        Config = config;
        Runner = runner;
        Gate = gate;
        _sqliteHistory = sqliteHistory;
        History = history;
        _eventStore = eventStore;
        Bus = bus;
        Registry = registry;
        Pause = pause;
        Engine = engine;
    }

    public TempDirectory Temp { get; }

    public SimulatedClock Clock { get; }

    public SimulatedPlatform Platform { get; }

    public ConfigStore Config { get; }

    public FakeProcessRunner Runner { get; }

    /// <summary>The handler for <c>powershell</c> actions; null when the harness was created without one.</summary>
    public GateActionHandler? Gate { get; }

    public IRunHistory History { get; }

    public EventBus Bus { get; }

    public SqliteEventStore EventStore => _eventStore;

    public CommandRegistry Registry { get; }

    public AgentPause Pause { get; }

    public CommandEngine Engine { get; }

    public static async Task<CommandHarness> CreateAsync(
        CommandEngineOptions? engineOptions = null,
        RunHistoryOptions? historyOptions = null,
        bool withGate = true,
        IRunHistory? historyOverride = null,
        Action<TempDirectory>? seed = null,
        IActionHandler? powerShell = null)
    {
        var temp = new TempDirectory();
        var clock = new SimulatedClock();
        var platform = new SimulatedPlatform(clock);
        seed?.Invoke(temp);
        ConfigStore config = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp), clock);
        var runner = new FakeProcessRunner();
        GateActionHandler? gate = withGate && powerShell is null ? new GateActionHandler() : null;

        SqliteRunHistory? sqliteHistory = historyOverride is null
            ? new SqliteRunHistory(temp.Combine("data", "runs.db"), clock, historyOptions)
            : null;
        IRunHistory history = historyOverride ?? sqliteHistory!;
        var eventStore = new SqliteEventStore(temp.Combine("data", "events.db"), clock);
        var bus = new EventBus(clock, eventStore);
        var registry = new CommandRegistry(config);
        var pause = new AgentPause();

        var launcher = new RoutingProcessLauncher(runner, platform.Session, platform.ElevatedLauncher);
        List<IActionHandler> handlers =
        [
            new ProcessActionHandler(),
            new BuiltinActionHandler(platform.Actions, platform.Session),
        ];
        if (gate is not null)
        {
            handlers.Add(gate);
        }

        if (powerShell is not null)
        {
            handlers.Add(powerShell);
        }

        var engine = new CommandEngine(registry, handlers, launcher, history, bus, clock, engineOptions, pause);
        return new CommandHarness(temp, clock, platform, config, runner, gate, sqliteHistory, history, eventStore, bus, registry, pause, engine);
    }

    public async Task SaveAsync(params CommandDocument[] documents)
    {
        foreach (CommandDocument document in documents)
        {
            SaveResult<CommandDocument> result = await Config.Local.SaveAsync(document, null, CancellationToken.None);
            if (!result.IsApplied)
            {
                throw new InvalidOperationException(
                    $"Saving '{document.Id}' failed: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}")));
            }
        }
    }

    public static async Task<RunResult> Finished(RunHandle handle)
    {
        return await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task<RunResult> RunAsync(string commandId, params (string Name, object? Value)[] variables)
    {
        return RunFromAsync(RunSources.Ui, commandId, variables);
    }

    public async Task<RunResult> RunFromAsync(string source, string commandId, params (string Name, object? Value)[] variables)
    {
        return await Engine.RunAsync(new RunRequest(commandId, Vars(variables), source)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task<RunHandle> StartAsync(string commandId, params (string Name, object? Value)[] variables)
    {
        return Engine.StartAsync(new RunRequest(commandId, Vars(variables)));
    }

    public static IReadOnlyDictionary<string, JsonElement> Vars(params (string Name, object? Value)[] values)
    {
        JsonElement document = EventData.Create(values);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.EnumerateObject())
        {
            result[property.Name] = property.Value;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        Registry.Dispose();
        Bus.Dispose();
        _eventStore.Dispose();
        _sqliteHistory?.Dispose();
        Config.Dispose();
        Platform.Dispose();
        Temp.Dispose();
    }
}

/// <summary>Builders for command documents.</summary>
internal static class Cmd
{
    public static CommandDocument Gated(
        string id,
        string concurrency = ConcurrencyModes.Exclusive,
        int cooldownSeconds = 0,
        int timeoutSeconds = 60,
        params ParameterDefinition[] parameters) => new(
            DocumentSchema.CurrentVersion,
            id,
            id,
            true,
            false,
            RunAsValues.System,
            new PowerShellAction(PowerShellSources.Inline, Code: "# controlled by the test"),
            Parameters: parameters.Length == 0 ? null : parameters,
            TimeoutSeconds: timeoutSeconds,
            Concurrency: concurrency,
            CooldownSeconds: cooldownSeconds,
            CaptureOutput: true);

    public static CommandDocument Builtin(string id, string name, string runAs = RunAsValues.System, params ParameterDefinition[] parameters) => new(
        DocumentSchema.CurrentVersion,
        id,
        id,
        true,
        false,
        runAs,
        new BuiltinAction(name),
        Parameters: parameters.Length == 0 ? null : parameters,
        CaptureOutput: true);

    public static CommandDocument Process(
        string id,
        string file,
        string[]? args = null,
        string runAs = RunAsValues.System,
        bool captureOutput = true,
        string? workingDirectory = null,
        bool waitForExit = true,
        params ParameterDefinition[] parameters) => new(
            DocumentSchema.CurrentVersion,
            id,
            id,
            true,
            false,
            runAs,
            new ProcessAction(file, args, workingDirectory, waitForExit),
            Parameters: parameters.Length == 0 ? null : parameters,
            CaptureOutput: captureOutput);

    public static CommandDocument Sequence(string id, string onError, params SequenceStep[] steps) => new(
        DocumentSchema.CurrentVersion,
        id,
        id,
        true,
        false,
        RunAsValues.System,
        new SequenceAction(steps, onError),
        TimeoutSeconds: 3600);

    public static SequenceStep Step(string command, params (string Name, object? Value)[] variables) =>
        new(Command: command, Vars: variables.Length == 0 ? null : CommandHarness.Vars(variables));

    public static SequenceStep Delay(double seconds) => new(DelaySeconds: seconds);

    public static ParameterDefinition Text(string name, bool required = false, bool secret = false) =>
        new(name, ParameterTypes.Text, Required: required, Secret: secret);

    public static ParameterDefinition Number(string name) => new(name, ParameterTypes.WholeNumber);
}
