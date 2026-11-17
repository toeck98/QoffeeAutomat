using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Modes;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Tests.Commands;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Core.Tests.Modes;

/// <summary>
/// A step runner that records every step in the order it ran, together with the current mode the engine reported at that moment, and
/// answers per command id. A command can be gated so that the test decides when it finishes.
/// </summary>
internal sealed class RecordingStepRunner
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, string?> _currentAtCall = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _exitCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _reached = new(StringComparer.Ordinal);
    private readonly SimulatedClock _clock;

    public RecordingStepRunner(SimulatedClock clock)
    {
        _clock = clock;
    }

    /// <summary>Reads the current mode of the group under test; set by the test.</summary>
    public Func<string?>? CurrentMode { get; set; }

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

    /// <summary>The current mode the engine reported while the command ran (the last time it ran).</summary>
    public string? CurrentModeDuring(string command)
    {
        lock (_gate)
        {
            return _currentAtCall[command];
        }
    }

    public void Fail(string command, int exitCode = 1)
    {
        lock (_gate)
        {
            _exitCodes[command] = exitCode;
        }
    }

    /// <summary>Makes the command wait until <see cref="Release"/>; returns the task that completes when it has started.</summary>
    public Task Gate(string command)
    {
        lock (_gate)
        {
            _gates[command] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _reached[command] = reached;
            return reached.Task;
        }
    }

    public void Release(string command)
    {
        lock (_gate)
        {
            _gates[command].TrySetResult();
        }
    }

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken)
    {
        TaskCompletionSource? gate;
        TaskCompletionSource? reached;
        int exitCode;
        lock (_gate)
        {
            _calls.Add(request.CommandId);
            _currentAtCall[request.CommandId] = CurrentMode?.Invoke();
            _gates.TryGetValue(request.CommandId, out gate);
            _reached.TryGetValue(request.CommandId, out reached);
            exitCode = _exitCodes.GetValueOrDefault(request.CommandId);
        }

        reached?.TrySetResult();
        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return new RunResult(
            "run-" + request.CommandId,
            request.CommandId,
            true,
            _clock.GetUtcNow(),
            TimeSpan.Zero,
            exitCode,
            string.Empty,
            string.Empty,
            false,
            null,
            null);
    }
}

/// <summary>
/// The action handler that stands for the mode routing of the agent: it hands a run of the generated command <c>mode_&lt;group&gt;</c> to the
/// mode engine, so that <see cref="ModeBootApplier"/> can be tested through a real <see cref="CommandEngine"/>.
/// </summary>
internal sealed class ModeRoutingHandler : IActionHandler
{
    private readonly Func<ModeEngine> _modes;
    private readonly ModeStepRunner _runner;

    public ModeRoutingHandler(Func<ModeEngine> modes, ModeStepRunner runner)
    {
        _modes = modes;
        _runner = runner;
    }

    public string ActionType => ActionTypes.Builtin;

    public async ValueTask<ExecutionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ModeNames.TryGetGroupId(context.Command.Id, out string groupId);
        ModeSwitchResult result = await _modes().SwitchAsync(groupId, (string)context.Variables[ModeNames.Variable], _runner, cancellationToken);
        return result.Succeeded
            ? new ExecutionResult(0, result.StandardOutput, result.StandardError)
            : ExecutionResult.Failure(result.ErrorCode ?? ErrorCodes.Internal, result.StandardError);
    }
}

/// <summary>A mode engine over a real config store and state file, a fake clock and a recording step runner.</summary>
internal sealed class ModeHarness : IAsyncDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private readonly EventSubscription _events;

    private ModeHarness(TempDirectory temp, SimulatedClock clock, ConfigStore config, EventBus bus, string bootId)
    {
        Temp = temp;
        Clock = clock;
        Config = config;
        Bus = bus;
        BootId = bootId;
        _events = bus.Subscribe([EventTypes.ModeChanged]);
        Runner = new RecordingStepRunner(clock);
        Open();
    }

    public TempDirectory Temp { get; }

    public SimulatedClock Clock { get; }

    public ConfigStore Config { get; }

    public EventBus Bus { get; }

    public string BootId { get; private set; }

    public RecordingStepRunner Runner { get; }

    public ModeStateStore State { get; private set; } = null!;

    public ModeEngine Engine { get; private set; } = null!;

    public static async Task<ModeHarness> CreateAsync(string bootId = "boot-1")
    {
        var temp = new TempDirectory();
        var clock = new SimulatedClock();
        ConfigStore config = await ConfigTestSupport.OpenAsync(ConfigTestSupport.Options(temp), clock);
        return new ModeHarness(temp, clock, config, new EventBus(clock), bootId);
    }

    /// <summary>Simulates a restart of the agent: a new engine and a new state store over the same file, with the given boot id.</summary>
    public void Restart(string bootId)
    {
        Engine.Dispose();
        State.Dispose();
        BootId = bootId;
        Open();
    }

    /// <summary>Saves the stored commands that the steps refer to and the group.</summary>
    public async Task SeedAsync(ModeGroupDocument group)
    {
        foreach (string id in ModeDocs.CommandsOf(group))
        {
            SaveResult<CommandDocument> command = await Config.Local.SaveAsync(ConfigTestSupport.Command(id), null, CancellationToken.None);
            Assert.True(command.IsApplied, "Saving command " + id + " failed.");
        }

        SaveResult<ModeGroupDocument> result = await Config.Local.SaveAsync(group, null, CancellationToken.None);
        if (!result.IsApplied)
        {
            throw new InvalidOperationException(
                $"Saving '{group.Id}' failed: " + string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}")));
        }

        Runner.CurrentMode = () => Engine.GetCurrent(group.Id);
    }

    public Task<ModeSwitchResult> SwitchAsync(string groupId, string modeId)
    {
        return Engine.SwitchAsync(groupId, modeId, Runner.RunAsync, CancellationToken.None);
    }

    /// <summary>Reads the <c>mode_changed</c> events that were published since the last call.</summary>
    public IReadOnlyList<(string Group, string? From, string To)> TakeChangedEvents()
    {
        var result = new List<(string, string?, string)>();
        while (_events.Events.TryRead(out AgentEvent? e))
        {
            Assert.NotNull(e.Data);
            JsonElement data = e.Data.Value;
            string? from = data.TryGetProperty("from", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            result.Add((data.GetProperty("group").GetString()!, from, data.GetProperty("to").GetString()!));
        }

        return result;
    }

    /// <summary>A command engine that routes the generated mode commands to the current mode engine; the session starts not ready.</summary>
    public (CommandEngine Commands, SimulatedPlatform Platform) CreateCommandEngine()
    {
        var platform = new SimulatedPlatform(Clock);
        platform.Session.SetState(SessionState.None);
        var history = new SqliteRunHistory(Temp.Combine("data", "runs.db"), Clock);
        var registry = new CommandRegistry(Config);
        var launcher = new RoutingProcessLauncher(new FakeProcessRunner(), platform.Session, platform.ElevatedLauncher);
        var engine = new CommandEngine(
            registry,
            [new ModeRoutingHandler(() => Engine, Runner.RunAsync)],
            launcher,
            history,
            Bus,
            Clock,
            null,
            new AgentPause(),
            platform.Session);
        _asyncDisposables.Add(engine);
        _disposables.AddRange([registry, history, platform]);
        return (engine, platform);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable disposable in _asyncDisposables)
        {
            await disposable.DisposeAsync();
        }

        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        Engine.Dispose();
        State.Dispose();
        _events.Dispose();
        Bus.Dispose();
        Config.Dispose();
        Temp.Dispose();
    }

    private void Open()
    {
        State = new ModeStateStore(Temp.Combine("data", "mode-state.json"));
        Engine = new ModeEngine(Config, State, Bus, Clock, () => BootId);
    }
}

/// <summary>Builders for mode group documents.</summary>
internal static class ModeDocs
{
    public static SequenceStep Step(string command) => new(Command: command);

    public static ModeDefinition Mode(string id, string[]? enter = null, string[]? exit = null) => new(
        id,
        id,
        enter?.Select(Step).ToArray(),
        exit?.Select(Step).ToArray());

    public static ModeGroupDocument Group(
        string id,
        string defaultMode,
        string onBoot,
        string onError,
        params ModeDefinition[] modes) => new(
            DocumentSchema.CurrentVersion,
            id,
            id,
            true,
            true,
            defaultMode,
            modes,
            OnBoot: onBoot,
            OnError: onError);

    /// <summary>The group used by most tests: modes a (default) and b, every mode with enter and exit steps.</summary>
    public static ModeGroupDocument Standard(string onBoot = ModeBootBehaviors.Reset, string onError = SequenceErrorModes.Stop) => Group(
        "room",
        "a",
        onBoot,
        onError,
        Mode("a", ["a_enter"], ["a_exit"]),
        Mode("b", ["b_enter1", "b_enter2"], ["b_exit"]));

    /// <summary>The ids of the commands that the steps of the group call.</summary>
    public static IEnumerable<string> CommandsOf(ModeGroupDocument group) => group.Modes
        .SelectMany(mode => (mode.Enter ?? []).Concat(mode.Exit ?? []))
        .Select(step => step.Command)
        .Where(command => command is not null)
        .Select(command => command!)
        .Distinct(StringComparer.Ordinal);
}
