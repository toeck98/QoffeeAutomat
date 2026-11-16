using System.IO.Pipes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Metrics.SessionMetrics;
using QoffeeAutomat.Platform.Windows.Service;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.SessionHost;
using QoffeeAutomat.SessionHost.Execution;
using QoffeeAutomat.SessionHost.Ipc;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>Polling helpers for conditions that become true on another thread.</summary>
internal static class TestWait
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? Patience);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what + ".");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>A process runner that starts no process: a test emits the output and decides when a "process" ends.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<FakeProcessRun> _runs = [];
    private readonly bool _autoFinish;

    public FakeProcessRunner(bool autoFinish = false)
    {
        _autoFinish = autoFinish;
    }

    /// <summary>The exit code of runs that end by themselves.</summary>
    public int ExitCode { get; set; }

    public FakeProcessRun[] Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    public IProcessRun Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var run = new FakeProcessRun(request);
        lock (_gate)
        {
            _runs.Add(run);
        }

        // Like the real runner, a cancelled token ends the run as cancelled.
        cancellationToken.Register(run.Cancel);
        if (_autoFinish)
        {
            run.Emit(ProcessOutputSource.StandardOutput, "done\n");
            run.Finish(ExitCode, "done\n", string.Empty);
        }

        return run;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        await using IProcessRun run = Start(request, cancellationToken);
        return await run.Completion;
    }

    public async Task<FakeProcessRun> WaitForRunAsync(int count = 1)
    {
        await TestWait.UntilAsync(() => Runs.Length >= count, "the session helper to start a process");
        return Runs[count - 1];
    }
}

internal sealed class FakeProcessRun : IProcessRun
{
    private readonly Channel<ProcessOutputChunk> _output = Channel.CreateUnbounded<ProcessOutputChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<ProcessRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancelled;

    public FakeProcessRun(ProcessLaunchRequest request)
    {
        Request = request;
    }

    public ProcessLaunchRequest Request { get; }

    public bool WasCancelled => Volatile.Read(ref _cancelled) != 0;

    public IAsyncEnumerable<ProcessOutputChunk> Output => _output.Reader.ReadAllAsync();

    public Task<ProcessRunResult> Completion => _completion.Task;

    public void Emit(ProcessOutputSource source, string text)
    {
        _output.Writer.TryWrite(new ProcessOutputChunk(source, text, DateTimeOffset.UtcNow));
    }

    public void Finish(int exitCode, string standardOutput, string standardError)
    {
        _output.Writer.TryComplete();
        _completion.TrySetResult(new ProcessRunResult(exitCode, standardOutput, standardError, TimeSpan.FromMilliseconds(5), TimedOut: false, OutputTruncated: false));
    }

    public void Cancel()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        _output.Writer.TryComplete();
        _completion.TrySetResult(new ProcessRunResult(-1, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false, OutputTruncated: false, Cancelled: true));
    }

    public ValueTask DisposeAsync()
    {
        Cancel();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A logger that keeps what it is given; safe for the several threads of the session code.</summary>
internal sealed class ConcurrentLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public (LogLevel Level, string Message)[] Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        lock (_gate)
        {
            _entries.Add((logLevel, message));
        }
    }
}

/// <summary>The console session and its user, as the terminal services would report them.</summary>
internal sealed class FakeSessionLookup : ISessionUserLookup
{
    public int? Console { get; set; } = SessionHarness.SessionId;

    public string? User { get; set; } = "alice";

    public int? GetActiveConsoleSessionId() => Console;

    public string? GetUserName(int sessionId) => sessionId == Console ? User : null;
}

/// <summary>Sessions and the processes started in them, without Windows.</summary>
internal sealed class FakeSessionNativeApi : ISessionNativeApi
{
    private readonly object _gate = new();
    private readonly List<WtsSessionInfo> _sessions = [];
    private readonly List<(int SessionId, SessionLaunch Launch)> _launches = [];
    private readonly List<FakeSessionProcess> _processes = [];
    private readonly List<TimeSpan> _delays = [];

    public Exception? LaunchFailure { get; set; }

    public void SetSessions(params WtsSessionInfo[] sessions)
    {
        lock (_gate)
        {
            _sessions.Clear();
            _sessions.AddRange(sessions);
        }
    }

    public (int SessionId, SessionLaunch Launch)[] Launches
    {
        get
        {
            lock (_gate)
            {
                return [.. _launches];
            }
        }
    }

    public FakeSessionProcess[] Processes
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes];
            }
        }
    }

    public TimeSpan[] Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    /// <summary>A delay that records how long the manager wanted to wait and does not wait.</summary>
    public async Task NoDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _delays.Add(delay);
        }

        // A millisecond keeps a failing launch from spinning the processor.
        await Task.Delay(1, cancellationToken);
    }

    public IReadOnlyList<WtsSessionInfo> EnumerateSessions()
    {
        lock (_gate)
        {
            return [.. _sessions];
        }
    }

    public ISessionProcess StartInSession(int sessionId, SessionLaunch launch)
    {
        if (LaunchFailure is { } failure)
        {
            throw failure;
        }

        var process = new FakeSessionProcess(100 + sessionId);
        lock (_gate)
        {
            _launches.Add((sessionId, launch));
            _processes.Add(process);
        }

        return process;
    }
}

internal sealed class FakeSessionProcess : ISessionProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeSessionProcess(int processId)
    {
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public bool Killed { get; private set; }

    public void Exit(int exitCode) => _exit.TrySetResult(exitCode);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Kill()
    {
        Killed = true;
        _exit.TrySetResult(-1);
    }

    public void Dispose()
    {
    }
}

/// <summary>Tells the server who is on the other end of the pipe; the test decides.</summary>
internal sealed class FakePeerInspector : IPipePeerInspector
{
    public PipePeer Peer { get; set; } = new(Environment.ProcessId, SessionHarness.SessionId, SessionHarness.SessionHostPath);

    public PipePeer Inspect(NamedPipeServerStream pipe) => Peer;
}

/// <summary>Accepts every server (or refuses with a reason) without asking the operating system.</summary>
internal sealed class FakeServerVerifier : IPipeServerVerifier
{
    public string? Refusal { get; set; }

    public string? Verify(NamedPipeClientStream pipe) => Refusal;
}

/// <summary>Built-in actions of the SessionHost that record what they were asked to do.</summary>
internal sealed class FakeBuiltinActions : IBuiltinActionProvider
{
    private readonly object _gate = new();
    private readonly List<(string Name, IReadOnlyDictionary<string, object> Variables)> _calls = [];

    public IReadOnlyList<BuiltinActionDescriptor> Actions { get; set; } = [];

    public Func<string, IReadOnlyDictionary<string, object>, CancellationToken, Task<ExecutionResult>>? Handler { get; set; }

    public (string Name, IReadOnlyDictionary<string, object> Variables)[] Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public async ValueTask<ExecutionResult> ExecuteAsync(string name, IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _calls.Add((name, variables));
        }

        return Handler is { } handler
            ? await handler(name, variables, cancellationToken)
            : new ExecutionResult(0, "ok:" + name, string.Empty);
    }
}

/// <summary>A running SessionHost client of a test.</summary>
internal sealed class RunningHost : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunningHost(SessionHostClient client)
    {
        client.Connected += (_, _) => _connected.TrySetResult();
        Exit = Task.Run(() => client.RunAsync(_cancel.Token));
    }

    /// <summary>Completes with the exit code of the client.</summary>
    public Task<int> Exit { get; }

    /// <summary>Completes after the handshake.</summary>
    public Task Connected => _connected.Task;

    public async Task StopAsync()
    {
        await _cancel.CancelAsync();
        await Exit.WaitAsync(TestWait.Patience);
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();
        _cancel.Dispose();
    }
}

/// <summary>
/// The agent side (pipe server, session manager, bridge, event bus) and the SessionHost side (client, executor) in one
/// process, connected through a real named pipe with a unique name, so that no service, no SYSTEM account and no second
/// session is needed. The peer inspector and the server verifier are fakes that the test controls.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    public const int SessionId = 7;

    public static readonly string SessionHostPath = Path.Combine(Path.GetTempPath(), "qa-installed", "QoffeeAutomat.SessionHost.exe");

    private readonly List<RunningHost> _hosts = [];

    public SessionHarness(
        IProcessRunner? hostRunner = null,
        IBuiltinActionProvider? hostBuiltins = null,
        IPowerShellEngineLocator? locator = null,
        TimeSpan? cancelGrace = null,
        TimeSpan? handshakeTimeout = null,
        UserMetricsRelay? metricsRelay = null)
    {
        PipeName = "qa-test-" + Guid.NewGuid().ToString("N");
        ScriptsDirectory = Path.Combine(Path.GetTempPath(), "qa-scripts-" + Guid.NewGuid().ToString("N")[..8]);
        HostRunner = hostRunner ?? new FakeProcessRunner(autoFinish: true);
        HostBuiltins = hostBuiltins ?? new NoBuiltinActions();
        Locator = locator;
        Logger = new ConcurrentLogger();
        Lookup = new FakeSessionLookup();
        Native = new FakeSessionNativeApi();
        Inspector = new FakePeerInspector();
        Verifier = new FakeServerVerifier();

        Server = new SessionPipeServer(
            new SessionPipeServerOptions
            {
                PipeName = PipeName,
                SessionHostPath = SessionHostPath,
                ScriptsDirectory = ScriptsDirectory,
                ApplyPipeSecurity = false,
                HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(5),
                CancelGrace = cancelGrace ?? TimeSpan.FromSeconds(2),
            },
            Inspector,
            TimeProvider.System,
            Logger);
        Manager = new WindowsSessionManager(
            Native,
            Server,
            new SessionManagerOptions { SessionHostPath = SessionHostPath, PipeName = PipeName, Delay = Native.NoDelay },
            TimeProvider.System,
            Logger);
        Monitor = new WindowsSessionMonitor(Lookup);
        Bridge = new WindowsSessionBridge(Monitor, Manager, Lookup, TimeProvider.System, metricsRelay);
        Bus = new EventBus(TimeProvider.System);
        Publisher = new PlatformEventPublisher(Bus, Bridge, null);
        Ready = Bus.Subscribe([EventTypes.SessionReady]);
    }

    public string PipeName { get; }

    public string ScriptsDirectory { get; }

    public IProcessRunner HostRunner { get; }

    public IBuiltinActionProvider HostBuiltins { get; }

    public IPowerShellEngineLocator? Locator { get; }

    public ConcurrentLogger Logger { get; }

    public FakeSessionLookup Lookup { get; }

    public FakeSessionNativeApi Native { get; }

    public FakePeerInspector Inspector { get; }

    public FakeServerVerifier Verifier { get; }

    public SessionPipeServer Server { get; }

    public WindowsSessionManager Manager { get; }

    public WindowsSessionMonitor Monitor { get; }

    public WindowsSessionBridge Bridge { get; }

    public EventBus Bus { get; }

    public PlatformEventPublisher Publisher { get; }

    public EventSubscription Ready { get; }

    /// <summary>Starts the manager, which starts the pipe server.</summary>
    public Task StartAsync() => Manager.StartAsync(CancellationToken.None);

    /// <summary>Starts a SessionHost client for the session of the harness.</summary>
    public RunningHost StartHost(int? sessionId = null, IUserMetricProbe? metrics = null, Action<SessionHostClient>? beforeStart = null)
    {
        var options = new SessionHostClientOptions
        {
            SessionId = sessionId ?? SessionId,
            PipeName = PipeName,
            UserName = "alice",
            IsAdministrator = false,
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        };
        var client = new SessionHostClient(options, Verifier, HostRunner, HostBuiltins, Locator, TimeProvider.System, log: null, metrics);

        // Subscribers must exist before the client runs, or they miss the first events.
        beforeStart?.Invoke(client);
        var host = new RunningHost(client);
        _hosts.Add(host);
        return host;
    }

    /// <summary>Waits for the next <c>session_ready</c> event.</summary>
    public async Task<AgentEvent> NextSessionReadyAsync()
    {
        using var timeout = new CancellationTokenSource(TestWait.Patience);
        return await Ready.Events.ReadAsync(timeout.Token);
    }

    public void Dispose()
    {
        foreach (RunningHost host in _hosts)
        {
            host.Dispose();
        }

        Ready.Dispose();
        Publisher.Dispose();
        Bridge.Dispose();
        Manager.Dispose();
        Server.Dispose();
        Bus.Dispose();
    }
}
