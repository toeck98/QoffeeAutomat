using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands.Conditions;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Secrets.Named;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Commands;

/// <summary>
/// Runs commands (PLAN.md S06, S07, S09, T207). Every entry point (QoffeeLink, the management API, the
/// command line, a sequence step) calls <see cref="StartAsync"/> or <see cref="RunAsync"/>, so the same
/// rules apply everywhere. A request passes these checks in order, and the first that fails rejects it
/// with the error code of S07:
/// <list type="number">
/// <item><c>unknown_command</c>: no command with that id.</item>
/// <item><c>command_disabled</c>: the command is disabled.</item>
/// <item><c>paused</c>: the agent is paused (the maintenance mode switch itself is exempt, otherwise nobody could switch it off).</item>
/// <item><c>condition_not_met</c>: the command's conditions (session state, time window) do not hold (T705).</item>
/// <item><c>cooldown</c>: the previous run of the command started less than <c>cooldownSeconds</c> ago.</item>
/// <item><c>invalid_vars</c>: a variable is missing, unknown or invalid (<see cref="VariableBinder"/>).</item>
/// <item><c>busy</c>: the concurrency mode forbids another run (<c>single</c>, or a full queue or parallel limit).</item>
/// </list>
/// Concurrency modes behave like the Home Assistant script modes of the same name: <c>single</c> rejects
/// a new run while one is active, <c>queue</c> runs them one after the other in order, <c>parallel</c>
/// lets them overlap and <c>restart</c> cancels the active runs and then starts the new one.
/// An admitted run is dispatched to the <see cref="IActionHandler"/> of its action type (a sequence is
/// executed by the engine), under the command's timeout. The named secrets that a command lists (T706) are read when
/// the run starts and reach the process only as <c>QA_SECRET_&lt;NAME&gt;</c> environment variables; their values are
/// masked in the captured output before it is stored, streamed or returned, and a command with secrets shows its
/// output only when it ends, because a value could be split across two pieces of live output. Rejected requests of existing commands and every
/// run are written to the <see cref="IRunHistory"/>; <see cref="EventTypes.CommandStarted"/> and
/// <see cref="EventTypes.CommandFinished"/> are published on the <see cref="IEventBus"/>. All timing uses the
/// injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class CommandEngine : IAsyncDisposable
{
    private const string DefaultAgentName = "QoffeeAutomat";

    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromHours(24);

    private readonly CommandRegistry _registry;
    private readonly Dictionary<string, IActionHandler> _handlers = new(StringComparer.Ordinal);
    private readonly IProcessLauncher _launcher;
    private readonly IRunHistory _history;
    private readonly IEventBus _events;
    private readonly TimeProvider _time;
    private readonly CommandEngineOptions _options;
    private readonly IAgentPause? _pause;
    private readonly ISessionBridge? _session;
    private readonly INamedSecretStore? _secrets;
    private readonly ConcurrentDictionary<string, CommandSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

    /// <summary>Creates the engine.</summary>
    /// <param name="registry">The commands.</param>
    /// <param name="handlers">The handlers of the action types <c>process</c>, <c>builtin</c> and later <c>powershell</c>; each type at most once.</param>
    /// <param name="launcher">Starts processes in the run context of a command; handlers receive it in their <see cref="ActionContext"/>.</param>
    /// <param name="history">Where runs are recorded.</param>
    /// <param name="events">The event bus for run started and finished.</param>
    /// <param name="time">The clock for timeouts, cooldowns, delays and timestamps.</param>
    /// <param name="options">Tuning; null uses the defaults.</param>
    /// <param name="pause">The global pause flag; null means the agent is never paused.</param>
    /// <param name="session">The session bridge, for the session condition of commands; null means nobody is ever logged in.</param>
    /// <param name="secrets">The named secrets that commands may list; null makes a command that lists one fail with <c>internal</c>.</param>
    public CommandEngine(
        CommandRegistry registry,
        IEnumerable<IActionHandler> handlers,
        IProcessLauncher launcher,
        IRunHistory history,
        IEventBus events,
        TimeProvider time,
        CommandEngineOptions? options = null,
        IAgentPause? pause = null,
        ISessionBridge? session = null,
        INamedSecretStore? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);

        _registry = registry;
        _launcher = launcher;
        _history = history;
        _events = events;
        _time = time;
        _options = options ?? new CommandEngineOptions();
        _options.Validate();
        _pause = pause;
        _session = session;
        _secrets = secrets;

        foreach (IActionHandler handler in handlers)
        {
            if (!_handlers.TryAdd(handler.ActionType, handler))
            {
                throw new ArgumentException($"More than one handler for the action type '{handler.ActionType}'.", nameof(handlers));
            }
        }
    }

    /// <summary>Raised when recording a run or publishing an event failed; the run itself is unaffected. Handlers log and must not throw.</summary>
    public event EventHandler<CommandEngineFaultEventArgs>? Faulted;

    /// <summary>The commands that the engine knows.</summary>
    public CommandRegistry Registry => _registry;

    /// <summary>The runs that have not finished, oldest first.</summary>
    public IReadOnlyList<ActiveRunInfo> ActiveRuns =>
    [
        .. _runs.Values
            .OrderBy(run => run.AcceptedAt)
            .Select(run => new ActiveRunInfo(
                run.RunId,
                run.Command.Id,
                run.Request.Source,
                run.IsRunning ? ActiveRunState.Running : ActiveRunState.Waiting,
                run.AcceptedAt,
                run.Lineage.ParentRunId)),
    ];

    /// <summary>
    /// Applies the checks and, when the request is accepted, starts the run in the background. Returns as
    /// soon as the request is accepted or rejected.
    /// </summary>
    /// <param name="request">What to run.</param>
    /// <param name="cancellationToken">Cancels recording of a rejection; it does not cancel an accepted run (use <see cref="RunHandle.Cancel"/>).</param>
    /// <returns>The handle; a rejected request has <see cref="RunHandle.IsAccepted"/> false and a completed <see cref="RunHandle.Completion"/>.</returns>
    public Task<RunHandle> StartAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source, nameof(request));
        ObjectDisposedException.ThrowIf(_disposed, this);

        return AdmitAsync(request, Lineage.Root, cancellationToken, draft: null);
    }

    /// <summary>
    /// Starts a test run of a command document that is not saved (the editor's test panel, PLAN.md S08
    /// <c>/commands/test</c>). The caller has validated the document. The run is recorded in the history with the
    /// source <see cref="RunSources.Test"/>, no events are published for it, and it is subject to the agent's pause
    /// and to variable binding like any run, but not to <c>enabled</c> and the cooldown: the draft is being tried out.
    /// Steps of a sequence still refer to the saved commands.
    /// </summary>
    /// <param name="draft">The validated draft.</param>
    /// <param name="variables">Variable values for the draft's parameters.</param>
    /// <param name="clientId">The UI session, for the history.</param>
    /// <param name="cancellationToken">Cancels recording of a rejection; it does not cancel an accepted run.</param>
    /// <returns>The handle, as for <see cref="StartAsync"/>.</returns>
    public Task<RunHandle> StartDraftAsync(
        CommandDocument draft,
        IReadOnlyDictionary<string, JsonElement>? variables = null,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new RunRequest(draft.Id, variables, RunSources.Test, clientId);
        return AdmitAsync(request, Lineage.Root, cancellationToken, draft);
    }

    /// <summary>Runs a command and waits for the end.</summary>
    /// <param name="request">What to run.</param>
    /// <param name="cancellationToken">Cancelling it cancels the run: a process tree is killed and the result has the error <c>cancelled</c>.</param>
    /// <returns>The outcome; never throws for a failing, rejected, timed-out or cancelled run.</returns>
    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        RunHandle handle = await StartAsync(request, cancellationToken);
        using CancellationTokenRegistration registration = cancellationToken.Register(handle.Cancel);
        return await handle.Completion;
    }

    /// <summary>Cancels a waiting or running run.</summary>
    /// <param name="runId">The run id.</param>
    /// <returns>True when the run is active and was asked to stop.</returns>
    public bool Cancel(string runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        if (_runs.TryGetValue(runId, out ActiveRun? run))
        {
            run.Cancel();
            return true;
        }

        return false;
    }

    /// <summary>Cancels every active run and waits (up to <see cref="CommandEngineOptions.ShutdownTimeout"/>) for them to end.</summary>
    /// <returns>A task that completes when the runs have ended or the wait timed out.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        Task[] pending = [.. _runs.Values.Select(run => (Task)run.Result.Task)];
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(_options.ShutdownTimeout, TimeProvider.System);
            }
            catch (TimeoutException)
            {
                // A run that ignores cancellation must not hold the shutdown back.
            }
        }

        _shutdown.Dispose();
    }

    // ------------------------------------------------------------------
    // Admission
    // ------------------------------------------------------------------

    private async Task<RunHandle> AdmitAsync(RunRequest request, Lineage lineage, CancellationToken cancellationToken, CommandDocument? draft = null)
    {
        string runId = NewRunId();
        DateTimeOffset now = _time.GetUtcNow();
        bool isDraft = draft is not null;

        CommandDocument? command = draft ?? _registry.TryGet(request.CommandId);
        if (command is null)
        {
            // Nothing to record: there is no command the attempt could belong to.
            var unknown = new RunResult(
                runId,
                request.CommandId,
                false,
                now,
                TimeSpan.Zero,
                null,
                string.Empty,
                string.Empty,
                false,
                ErrorCodes.UnknownCommand,
                $"There is no command '{request.CommandId}'.");
            return new RunHandle(runId, false, Task.FromResult(unknown), null);
        }

        if (lineage.Chain.Contains(command.Id, StringComparer.Ordinal))
        {
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.Internal, $"The command '{command.Id}' is already part of this sequence call chain.", cancellationToken);
        }

        if (lineage.Chain.Count >= _options.MaxSequenceDepth)
        {
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.Internal, $"Sequences are nested deeper than {_options.MaxSequenceDepth} levels.", cancellationToken);
        }

        if (!command.Enabled && !isDraft)
        {
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.CommandDisabled, "The command is disabled.", cancellationToken);
        }

        if (_pause?.IsPaused == true && !SystemCommands.IsMaintenance(command))
        {
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.Paused, "The agent is paused.", cancellationToken);
        }

        ConditionVerdict verdict = CommandConditionEvaluator.Evaluate(command.Conditions, _session?.State ?? SessionState.None, _time.GetLocalNow());
        if (!verdict.IsMet)
        {
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.ConditionNotMet, verdict.Reason!, cancellationToken);
        }

        // A draft has its own slot, so trying it out neither blocks nor is blocked by the saved command of the same id.
        CommandSlot slot = _slots.GetOrAdd(isDraft ? "draft:" + command.Id : command.Id, static _ => new CommandSlot());
        TimeSpan? cooldown;
        lock (slot.Gate)
        {
            cooldown = isDraft ? null : CooldownRemaining(slot, command, now);
        }

        if (cooldown is { } wait)
        {
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.Cooldown, CooldownMessage(wait), cancellationToken);
        }

        BindResult bound = VariableBinder.Bind(command.Parameters, request.Variables);
        if (!bound.IsValid)
        {
            string message = string.Join("; ", bound.Errors.Select(error => $"{error.Path}: {error.Message}"));
            return await RejectAsync(runId, request, command, lineage, ErrorCodes.InvalidVars, message, cancellationToken);
        }

        ActiveRun? run = null;
        Task? predecessor = null;
        var toCancel = new List<ActiveRun>();
        string? rejectionCode = null;
        string? rejectionMessage = null;

        lock (slot.Gate)
        {
            // The earlier check and this one are separate so binding happens outside the lock; two
            // simultaneous requests must still not both pass the cooldown.
            DateTimeOffset checkedAt = _time.GetUtcNow();
            if (!isDraft && CooldownRemaining(slot, command, checkedAt) is { } remaining)
            {
                rejectionCode = ErrorCodes.Cooldown;
                rejectionMessage = CooldownMessage(remaining);
            }
            else
            {
                switch (command.Concurrency)
                {
                    case ConcurrencyModes.Queue:
                        if (slot.Active.Count > _options.QueueLimit)
                        {
                            rejectionCode = ErrorCodes.Busy;
                            rejectionMessage = $"The command already has {_options.QueueLimit} runs waiting.";
                        }
                        else if (slot.Active.Count > 0)
                        {
                            predecessor = slot.Active[^1].Result.Task;
                        }

                        break;
                    case ConcurrencyModes.Parallel:
                        if (slot.Active.Count >= _options.ParallelLimit)
                        {
                            rejectionCode = ErrorCodes.Busy;
                            rejectionMessage = $"The command already has {_options.ParallelLimit} runs in parallel.";
                        }

                        break;
                    case ConcurrencyModes.Restart:
                        toCancel.AddRange(slot.Active);
                        if (toCancel.Count > 0)
                        {
                            predecessor = Task.WhenAll(toCancel.Select(active => (Task)active.Result.Task));
                        }

                        break;
                    default:
                        if (slot.Active.Count > 0)
                        {
                            rejectionCode = ErrorCodes.Busy;
                            rejectionMessage = "The command is already running.";
                        }

                        break;
                }

                if (rejectionCode is null)
                {
                    run = new ActiveRun(
                        runId,
                        command,
                        request,
                        lineage,
                        checkedAt,
                        bound.Values,
                        CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token),
                        slot,
                        isDraft)
                    {
                        // Registered before the handle is returned, so a client that asks for the output at once finds the run.
                        Feed = _options.RunOutput?.Register(runId),
                    };
                    slot.Active.Add(run);
                    slot.LastStart = checkedAt;
                    _runs[runId] = run;
                }
            }
        }

        if (run is null)
        {
            return await RejectAsync(runId, request, command, lineage, rejectionCode!, rejectionMessage!, cancellationToken);
        }

        foreach (ActiveRun previous in toCancel)
        {
            previous.Cancel();
        }

        ActiveRun started = run;
        _ = Task.Run(() => ExecuteAsync(started, predecessor), CancellationToken.None);
        return new RunHandle(runId, true, run.Result.Task, run.Cancel);
    }

    private static TimeSpan? CooldownRemaining(CommandSlot slot, CommandDocument command, DateTimeOffset now)
    {
        if (command.CooldownSeconds <= 0 || slot.LastStart is not { } last)
        {
            return null;
        }

        TimeSpan remaining = last + TimeSpan.FromSeconds(command.CooldownSeconds) - now;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private static string CooldownMessage(TimeSpan remaining)
    {
        return $"The command is cooling down for another {Math.Ceiling(remaining.TotalSeconds):0} seconds.";
    }

    private async Task<RunHandle> RejectAsync(
        string runId,
        RunRequest request,
        CommandDocument command,
        Lineage lineage,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        var result = new RunResult(
            runId,
            command.Id,
            false,
            _time.GetUtcNow(),
            TimeSpan.Zero,
            null,
            string.Empty,
            message,
            false,
            errorCode,
            message);

        JsonElement variables = RunVariables.FromRaw(command.Parameters, request.Variables);
        await RecordAsync(result, request, lineage, variables, command, bound: null, cancellationToken);
        return new RunHandle(runId, false, Task.FromResult(result), null);
    }

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    private async Task ExecuteAsync(ActiveRun run, Task? predecessor)
    {
        RunResult result;
        try
        {
            result = await ExecuteCoreAsync(run, predecessor);
        }
#pragma warning disable CA1031 // Whatever goes wrong, the run must end with a result, or its slot would stay occupied forever.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RaiseFault(run.RunId, "handler", exception);
            result = new RunResult(
                run.RunId,
                run.Command.Id,
                run.IsRunning,
                _time.GetUtcNow(),
                TimeSpan.Zero,
                null,
                string.Empty,
                "The run failed unexpectedly.",
                false,
                ErrorCodes.Internal,
                "The run failed unexpectedly.");
        }

        lock (run.Slot.Gate)
        {
            run.Slot.Active.Remove(run);
        }

        _runs.TryRemove(run.RunId, out _);
        EndLiveOutput(run, result);
        run.Result.TrySetResult(result);
        run.Dispose();
    }

    /// <summary>Tells the subscribers of the live output that the run ended; the history already has the run.</summary>
    private void EndLiveOutput(ActiveRun run, RunResult result)
    {
        if (run.Feed is null || _options.RunOutput is not { } hub)
        {
            return;
        }

        hub.Finish(
            run.RunId,
            result,
            RunVariables.RedactOutput(result.StandardOutput, run.Command.Parameters, run.Variables),
            RunVariables.RedactOutput(result.StandardError, run.Command.Parameters, run.Variables));
    }

    private async Task<RunResult> ExecuteCoreAsync(ActiveRun run, Task? predecessor)
    {
        if (predecessor is not null)
        {
            try
            {
                await predecessor.WaitAsync(run.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelled while waiting for its turn (restart, cancel or shutdown).
            }
        }

        if (run.Token.IsCancellationRequested)
        {
            return await CancelledBeforeStartAsync(run);
        }

        run.IsRunning = true;
        DateTimeOffset startedAt = _time.GetUtcNow();
        if (!run.IsDraft)
        {
            await PublishAsync(
                run.RunId,
                EventTypes.CommandStarted,
                EventData.Create(("command", run.Command.Id), ("run_id", run.RunId), ("source", run.Request.Source)));
        }

        TimeSpan timeout = Clamp(TimeSpan.FromSeconds(run.Command.TimeoutSeconds));
        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, timeoutSource.Token);

        ExecutionResult execution;
        SecretMasker masker = SecretMasker.Empty;
        try
        {
            ResolvedSecrets secrets = await ResolveSecretsAsync(run.Command, linked.Token);
            if (secrets.IsComplete)
            {
                masker = secrets.CreateMasker();
                execution = await DispatchAsync(run, secrets, linked.Token);
            }
            else
            {
                execution = ExecutionResult.Failure(
                    ErrorCodes.Internal,
                    $"The command uses secrets that are not stored: {string.Join(", ", secrets.Missing)}.");
            }
        }
        catch (OperationCanceledException)
        {
            execution = ExecutionResult.Failure(ErrorCodes.Cancelled, "The run was cancelled.");
        }
        catch (SecretStoreException exception)
        {
            // The message of the exception never contains a secret value.
            execution = ExecutionResult.Failure(ErrorCodes.Internal, "The secrets of the command could not be read: " + exception.Message);
        }
#pragma warning disable CA1031 // A failing handler ends this run with the error internal, nothing more.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            RaiseFault(run.RunId, "handler", exception);
            execution = ExecutionResult.Failure(ErrorCodes.Internal, "The action failed unexpectedly: " + exception.Message);
        }

        // Whatever the script printed, the values of its secrets do not leave the engine: not in the history, not in the
        // live output, not in the result that goes back to the caller.
        execution = MaskExecution(execution, masker);

        bool timedOut = timeoutSource.IsCancellationRequested && !run.Token.IsCancellationRequested;
        if (timedOut && execution.ErrorCode == ErrorCodes.Cancelled)
        {
            execution = execution with { ErrorCode = ErrorCodes.Timeout };
        }

        DateTimeOffset endedAt = _time.GetUtcNow();
        RunResult result = BuildResult(run, started: true, startedAt, endedAt - startedAt, execution, timeout);

        await RecordAsync(
            result,
            run.Request,
            run.Lineage,
            RunVariables.FromBound(run.Command.Parameters, run.Variables),
            run.Command,
            run.Variables,
            CancellationToken.None);
        if (!run.IsDraft)
        {
            await PublishAsync(run.RunId, EventTypes.CommandFinished, FinishedData(run, result));
        }

        return result;
    }

    private async Task<RunResult> CancelledBeforeStartAsync(ActiveRun run)
    {
        DateTimeOffset now = _time.GetUtcNow();
        RunResult result = BuildResult(
            run,
            started: false,
            now,
            TimeSpan.Zero,
            ExecutionResult.Failure(ErrorCodes.Cancelled, "The run was cancelled before it started."),
            TimeSpan.Zero);
        await RecordAsync(
            result,
            run.Request,
            run.Lineage,
            RunVariables.FromBound(run.Command.Parameters, run.Variables),
            run.Command,
            run.Variables,
            CancellationToken.None);
        return result;
    }

    private async Task<ResolvedSecrets> ResolveSecretsAsync(CommandDocument command, CancellationToken cancellationToken)
    {
        if (command.Secrets is not { Count: > 0 } names)
        {
            return ResolvedSecrets.None;
        }

        if (_secrets is null)
        {
            throw new SecretStoreException("This agent has no secret store.");
        }

        return await _secrets.ResolveAsync(names, cancellationToken);
    }

    private static ExecutionResult MaskExecution(ExecutionResult execution, SecretMasker masker)
    {
        if (masker.IsEmpty)
        {
            return execution;
        }

        return execution with
        {
            StandardOutput = masker.Apply(execution.StandardOutput),
            StandardError = masker.Apply(execution.StandardError),
        };
    }

    private async ValueTask<ExecutionResult> DispatchAsync(ActiveRun run, ResolvedSecrets secrets, CancellationToken cancellationToken)
    {
        if (SystemCommands.IsMaintenance(run.Command))
        {
            return RunMaintenance(run);
        }

        CommandAction action = run.Command.Action;
        if (action is SequenceAction sequence)
        {
            return await RunSequenceAsync(run, sequence, cancellationToken);
        }

        if (_options.PlatformSupport is { } support && support.Check(run.Command) is { Count: > 0 } unsupported)
        {
            // Flagged, not deleted: the document stays as it is (PLAN.md S17).
            return ExecutionResult.Failure(ErrorCodes.Internal, unsupported[0].Message);
        }

        if (!_handlers.TryGetValue(action.Type, out IActionHandler? handler))
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, $"The '{action.Type}' action is not available in this version of the agent.");
        }

        return await handler.ExecuteAsync(BuildContext(run, secrets), cancellationToken);
    }

    /// <summary>Switches the maintenance mode (the pause flag) on or off.</summary>
    private ExecutionResult RunMaintenance(ActiveRun run)
    {
        if (_pause is null)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "This agent has no maintenance mode.");
        }

        bool enable = run.Variables.TryGetValue(SystemCommands.MaintenanceVariable, out object? value) && value is true;
        _pause.SetPaused(enable);
        return new ExecutionResult(0, enable ? "Maintenance mode is on." : "Maintenance mode is off.", string.Empty);
    }

    private ActionContext BuildContext(ActiveRun run, ResolvedSecrets secrets)
    {
        string agentName = _options.AgentName?.Invoke() ?? DefaultAgentName;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (_options.LocalChannel is { } channel)
        {
            foreach ((string name, string value) in channel.ToEnvironment())
            {
                environment[name] = value;
            }
        }

        environment["QA_RUN_ID"] = run.RunId;
        environment["QA_SOURCE"] = run.Request.Source;
        environment["QA_COMMAND_ID"] = run.Command.Id;
        environment["QA_AGENT_NAME"] = agentName;
        foreach ((string name, object value) in run.Variables)
        {
            environment["QA_VAR_" + name.ToUpperInvariant()] = ActionContext.FormatValue(value);
        }

        // The named secrets the command listed (T706). They go to the process as environment variables and nowhere else.
        foreach ((string name, string value) in secrets.ToEnvironment())
        {
            environment[name] = value;
        }

        // A secret variable or a named secret could be split across two pieces of live output, so such a command only
        // shows its redacted output when it ends.
        bool hasSecrets = secrets.Count > 0 || (run.Command.Parameters?.Any(parameter => parameter.Secret) ?? false);
        bool streams = run.Feed is not null && !hasSecrets;
        IProcessLauncher launcher = streams ? new OutputTapLauncher(_launcher, run.Feed!) : _launcher;

        return new ActionContext(
            run.Command,
            run.Variables,
            run.RunId,
            run.Request.Source,
            agentName,
            Clamp(TimeSpan.FromSeconds(run.Command.TimeoutSeconds)),
            environment,
            launcher);
    }

    // ------------------------------------------------------------------
    // Sequences
    // ------------------------------------------------------------------

    private async ValueTask<ExecutionResult> RunSequenceAsync(ActiveRun run, SequenceAction sequence, CancellationToken cancellationToken)
    {
        string? cycle = FindCycle(run.Command);
        if (cycle is not null)
        {
            return ExecutionResult.Failure(ErrorCodes.Internal, "The sequence calls itself: " + cycle + ".");
        }

        bool stopOnError = !string.Equals(sequence.OnError, SequenceErrorModes.Continue, StringComparison.Ordinal);
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        Lineage lineage = run.Lineage.Descend(run.Command.Id, run.RunId);
        int failures = 0;
        string? firstErrorCode = null;
        bool truncated = false;

        for (int index = 0; index < sequence.Steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SequenceStep step = sequence.Steps[index];

            if (step.DelaySeconds is { } delay)
            {
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), _time, cancellationToken);
                }

                continue;
            }

            RunHandle handle = string.IsNullOrEmpty(step.Command)
                ? throw new InvalidOperationException($"Step {index + 1} of the sequence names no command and no delay.")
                : await AdmitAsync(
                    new RunRequest(step.Command, step.Vars, RunSources.Sequence, run.Request.ClientId, run.Request.UserId),
                    lineage,
                    cancellationToken);

            using CancellationTokenRegistration registration = cancellationToken.Register(handle.Cancel);
            RunResult child = await handle.Completion;

            truncated |= child.OutputTruncated;
            if (child.StandardOutput.Length > 0)
            {
                standardOutput.Append(child.StandardOutput);
                if (!child.StandardOutput.EndsWith('\n'))
                {
                    standardOutput.Append('\n');
                }
            }

            if (child.Succeeded)
            {
                continue;
            }

            failures++;
            firstErrorCode ??= child.ErrorCode;
            standardError.Append("Step ").Append((index + 1).ToString(CultureInfo.InvariantCulture)).Append(" ('").Append(step.Command).Append("') failed: ")
                .Append(child.ErrorCode is null ? $"exit code {child.ExitCode}" : $"{child.ErrorCode}: {child.ErrorMessage}")
                .Append('\n');

            if (stopOnError)
            {
                break;
            }
        }

        // A sequence that was cancelled or timed out reports that, not the failure of the step that was cut short.
        cancellationToken.ThrowIfCancellationRequested();

        string? errorCode = failures > 0 && stopOnError ? firstErrorCode : null;
        return new ExecutionResult(failures == 0 ? 0 : 1, standardOutput.ToString(), standardError.ToString(), errorCode, truncated);
    }

    private string? FindCycle(CommandDocument start)
    {
        var path = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        return Visit(start.Id, start);

        // The command that starts the walk is used as given (it may be a draft); the others come from the registry.
        string? Visit(string id, CommandDocument? known)
        {
            if (finished.Contains(id))
            {
                return null;
            }

            if (!visiting.Add(id))
            {
                return string.Join(" -> ", path.Skip(path.IndexOf(id)).Append(id));
            }

            path.Add(id);
            if ((known ?? _registry.TryGet(id))?.Action is SequenceAction sequence)
            {
                foreach (SequenceStep step in sequence.Steps)
                {
                    if (!string.IsNullOrEmpty(step.Command) && Visit(step.Command, null) is { } trail)
                    {
                        return trail;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(id);
            finished.Add(id);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Results, history, events
    // ------------------------------------------------------------------

    private static RunResult BuildResult(
        ActiveRun run,
        bool started,
        DateTimeOffset startedAt,
        TimeSpan duration,
        ExecutionResult execution,
        TimeSpan timeout)
    {
        string? code = execution.ErrorCode;
        int? exitCode = code is not null && execution.ExitCode == -1 ? null : execution.ExitCode;
        string? message = code switch
        {
            null => null,
            ErrorCodes.Timeout => $"The command did not finish within {timeout.TotalSeconds:0} seconds.",
            ErrorCodes.Cancelled => "The run was cancelled.",
            _ => string.IsNullOrWhiteSpace(execution.StandardError) ? "The run failed." : execution.StandardError.Trim(),
        };

        return new RunResult(
            run.RunId,
            run.Command.Id,
            started,
            startedAt,
            duration,
            exitCode,
            execution.StandardOutput,
            execution.StandardError,
            execution.OutputTruncated,
            code,
            message);
    }

    private static JsonElement FinishedData(ActiveRun run, RunResult result)
    {
        return EventData.Create(
            ("command", run.Command.Id),
            ("run_id", run.RunId),
            ("source", run.Request.Source),
            ("exit_code", result.ExitCode),
            ("error", result.ErrorCode),
            ("duration_ms", (long)result.Duration.TotalMilliseconds),
            ("user_id", run.Request.UserId),
            ("parent_run_id", run.Lineage.ParentRunId));
    }

    private async Task RecordAsync(
        RunResult result,
        RunRequest request,
        Lineage lineage,
        JsonElement variables,
        CommandDocument command,
        IReadOnlyDictionary<string, object>? bound,
        CancellationToken cancellationToken)
    {
        string standardError = result.StandardError.Length == 0 && result.ErrorMessage is not null
            ? result.ErrorMessage
            : result.StandardError;
        var record = new RunRecord(
            result.RunId,
            command.Id,
            request.Source,
            request.ClientId,
            request.UserId,
            variables,
            result.StartedAt,
            result.Duration,
            result.ExitCode,
            result.ErrorCode,
            RunVariables.RedactOutput(result.StandardOutput, command.Parameters, bound),
            RunVariables.RedactOutput(standardError, command.Parameters, bound),
            result.OutputTruncated,
            lineage.ParentRunId);

        try
        {
            await _history.AddAsync(record, cancellationToken);
        }
#pragma warning disable CA1031 // A broken history must not turn a finished run into a failed one.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            RaiseFault(result.RunId, "history", exception);
        }
    }

    private async Task PublishAsync(string runId, string type, JsonElement data)
    {
        try
        {
            await _events.PublishAsync(type, data);
        }
#pragma warning disable CA1031 // A broken event bus must not fail the run.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            RaiseFault(runId, "event", exception);
        }
    }

    private void RaiseFault(string runId, string stage, Exception exception)
    {
#pragma warning disable CA1031 // The fault handler is a logger; if it throws there is nobody left to tell.
        try
        {
            Faulted?.Invoke(this, new CommandEngineFaultEventArgs(runId, stage, exception));
        }
        catch (Exception)
        {
            // Handlers must not throw.
        }
#pragma warning restore CA1031
    }

    private static TimeSpan Clamp(TimeSpan timeout)
    {
        return timeout < MinimumTimeout ? MinimumTimeout : timeout > MaximumTimeout ? MaximumTimeout : timeout;
    }

    private static string NewRunId()
    {
        return Guid.NewGuid().ToString("N");
    }

    // ------------------------------------------------------------------
    // Bookkeeping types
    // ------------------------------------------------------------------

    private sealed class CommandSlot
    {
        public object Gate { get; } = new();

        public List<ActiveRun> Active { get; } = [];

        public DateTimeOffset? LastStart { get; set; }
    }

    private sealed record Lineage(IReadOnlyList<string> Chain, string? ParentRunId)
    {
        public static Lineage Root { get; } = new([], null);

        public Lineage Descend(string commandId, string runId)
        {
            return new Lineage([.. Chain, commandId], runId);
        }
    }

    private sealed class ActiveRun : IDisposable
    {
        private readonly CancellationTokenSource _cancel;

        public ActiveRun(
            string runId,
            CommandDocument command,
            RunRequest request,
            Lineage lineage,
            DateTimeOffset acceptedAt,
            IReadOnlyDictionary<string, object> variables,
            CancellationTokenSource cancel,
            CommandSlot slot,
            bool isDraft)
        {
            IsDraft = isDraft;
            RunId = runId;
            Command = command;
            Request = request;
            Lineage = lineage;
            AcceptedAt = acceptedAt;
            Variables = variables;
            _cancel = cancel;
            Slot = slot;
        }

        public string RunId { get; }

        /// <summary>True for a test run of an unsaved draft: no events are published for it.</summary>
        public bool IsDraft { get; }

        /// <summary>Where the live output goes; null when the engine has no <see cref="CommandEngineOptions.RunOutput"/>.</summary>
        public RunOutputFeed? Feed { get; init; }

        public CommandDocument Command { get; }

        public RunRequest Request { get; }

        public Lineage Lineage { get; }

        public DateTimeOffset AcceptedAt { get; }

        public IReadOnlyDictionary<string, object> Variables { get; }

        public CommandSlot Slot { get; }

        public TaskCompletionSource<RunResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool IsRunning;

        public CancellationToken Token => _cancel.Token;

        public void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run ended; there is nothing to cancel.
            }
        }

        public void Dispose()
        {
            _cancel.Dispose();
        }
    }
}
