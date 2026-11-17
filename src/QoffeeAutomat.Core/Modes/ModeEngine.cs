using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Core.Modes;

/// <summary>
/// Switches mode groups (PLAN.md S06, T717). A switch from A to B runs the exit steps of A and then the enter steps of B; selecting the
/// current mode runs its enter steps again and no exit steps. Only one switch per group runs at a time: a second request is rejected
/// with <c>busy</c>. The current mode is kept by the <see cref="ModeStateStore"/> and becomes B only when the enter steps have finished;
/// a failing step with <c>onError: stop</c>, or a cancelled or timed-out switch, leaves it unknown (null) until the next successful
/// switch and publishes no event. With <c>onError: continue</c> a failing step is reported and the switch completes. Every change of
/// the current mode to another value publishes <see cref="EventTypes.ModeChanged"/> (<c>group</c>, <c>from</c>, <c>to</c>) and raises
/// <see cref="StateChanged"/>, which makes the state metric <c>mode.&lt;groupId&gt;</c> event-driven.
/// <para>
/// What <c>onBoot</c> asks for after an operating-system start (not after an agent restart) is decided by <see cref="ReconcileBootAsync"/>
/// from the boot id: the persisted entry of a group remembers the boot id in which it was last reconciled.
/// <c>apply_default</c> is carried out by <see cref="ModeBootApplier"/>, because it needs the command engine and the session.
/// </para>
/// </summary>
public sealed class ModeEngine : IModeSwitcher, IDisposable
{
    private readonly IConfigStore _config;
    private readonly ModeStateStore _state;
    private readonly IEventBus _events;
    private readonly TimeProvider _time;
    private readonly Func<string> _bootId;
    private readonly ConcurrentDictionary<string, string> _switching = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private ConfigSubscription? _subscription;
    private Task? _pump;
    private bool _disposed;

    /// <summary>Creates the engine. Call <see cref="ReconcileBootAsync"/> and <see cref="Start"/> once the configuration is loaded.</summary>
    /// <param name="config">The configuration; the mode groups of its effective view are used.</param>
    /// <param name="state">Where the current modes are kept.</param>
    /// <param name="events">The event bus for <c>mode_changed</c>.</param>
    /// <param name="time">The clock for delays and the time of a change.</param>
    /// <param name="bootId">Returns the boot id of the running operating system (<c>IPlatformInfo.BootId</c>).</param>
    public ModeEngine(IConfigStore config, ModeStateStore state, IEventBus events, TimeProvider time, Func<string> bootId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(bootId);

        _config = config;
        _state = state;
        _events = events;
        _time = time;
        _bootId = bootId;
    }

    /// <summary>Raised after the current mode of a group, or whether it is known, changed. Handlers return quickly and must not throw.</summary>
    public event EventHandler<ModeStateChangedEventArgs>? StateChanged;

    /// <summary>Raised after the set of mode groups, or one of them, changed. Handlers return quickly and must not throw.</summary>
    public event EventHandler? GroupsChanged;

    /// <summary>Raised when the state file or the event could not be written; the switch itself is unaffected. Handlers log and must not throw.</summary>
    public event EventHandler<ModeEngineFaultEventArgs>? Faulted;

    /// <summary>The mode groups, enabled or not, ordered by id.</summary>
    public IReadOnlyList<ModeGroupDocument> Groups => _config.Effective.List<ModeGroupDocument>();

    /// <summary>Finds a mode group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns>The group, or null.</returns>
    public ModeGroupDocument? Find(string groupId)
    {
        return string.IsNullOrEmpty(groupId) ? null : _config.Effective.GetDocument<ModeGroupDocument>(groupId);
    }

    /// <summary>Reads the state of a group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns>The state; a group that does not exist has no current mode.</returns>
    public ModeState GetState(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        ModeGroupDocument? group = Find(groupId);
        ModeStateEntry? entry = _state.Get(groupId);
        string? current = group is null ? null : Resolve(group, entry?.Current);
        bool switching = _switching.TryGetValue(groupId, out string? target);
        return new ModeState(groupId, current, switching, switching ? target : null, current is null ? null : entry?.ChangedAt);
    }

    /// <summary>The id of the current mode of a group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns>The mode id; null while the state is unknown, and for a mode that the group no longer has.</returns>
    public string? GetCurrent(string groupId)
    {
        return GetState(groupId).Current;
    }

    /// <summary>
    /// Decides what <c>onBoot</c> does for every group when the agent starts: when the boot id of the entry of a group is not the boot id of the
    /// running system the PC was started since the entry was written (or the group is new) and the behaviour applies; when it is the same the
    /// agent was only restarted and nothing happens. <c>keep</c> remembers the last mode, <c>reset</c> makes the default mode current, and
    /// <c>apply_default</c> makes the state unknown until <see cref="ModeBootApplier"/> has run the enter steps of the default mode.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ids of the groups whose default mode is to be applied once the session is ready.</returns>
    public async Task<IReadOnlyList<string>> ReconcileBootAsync(CancellationToken cancellationToken)
    {
        string bootId = _bootId();
        var pending = new List<string>();

        foreach (ModeGroupDocument group in Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModeStateEntry? entry = _state.Get(group.Id);
            if (entry?.BootId is { } known && string.Equals(known, bootId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(group.OnBoot, ModeBootBehaviors.Reset, StringComparison.Ordinal))
            {
                await StoreAsync(group.Id, new ModeStateEntry(group.DefaultMode, _time.GetUtcNow(), bootId)).ConfigureAwait(false);
            }
            else if (string.Equals(group.OnBoot, ModeBootBehaviors.ApplyDefault, StringComparison.Ordinal) && group.Enabled)
            {
                // The entry keeps the old boot id until the switch starts, so an agent restart before the session is ready applies it again.
                await StoreAsync(group.Id, new ModeStateEntry(null, null, entry?.BootId)).ConfigureAwait(false);
                pending.Add(group.Id);
            }
            else
            {
                await StoreAsync(group.Id, new ModeStateEntry(entry?.Current, entry?.ChangedAt, bootId)).ConfigureAwait(false);
            }

            RaiseStateChanged(group.Id);
        }

        return pending;
    }

    /// <summary>Tells whether the default mode of a group still has to be applied after the start of the PC.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns>
    /// True until a switch of the group started in this boot (a manual switch before the session was ready cancels the application of the default mode).
    /// </returns>
    public bool IsBootApplyPending(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        ModeStateEntry? entry = _state.Get(groupId);
        return !string.Equals(entry?.BootId, _bootId(), StringComparison.Ordinal);
    }

    /// <summary>Starts following the configuration: a deleted group is forgotten, a new group starts with an unknown state, and the metric follows edits.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pump is not null)
        {
            return;
        }

        ConfigSubscription subscription = _config.Subscribe();
        _subscription = subscription;
        _pump = Task.Run(() => PumpAsync(subscription, _stop.Token));
    }

    /// <summary>Stops following the configuration.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _subscription?.Dispose();
        try
        {
            _pump?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The pump was stopped on purpose.
        }

        _stop.Dispose();
    }

    /// <inheritdoc />
    public async Task<ModeSwitchResult> SwitchAsync(string groupId, string modeId, ModeStepRunner runStep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(modeId);
        ArgumentNullException.ThrowIfNull(runStep);

        ModeGroupDocument? group = Find(groupId);
        if (group is null)
        {
            return ModeSwitchResult.Rejected(groupId, modeId, ErrorCodes.UnknownCommand, $"There is no mode group '{groupId}'.");
        }

        ModeDefinition? target = group.Modes.FirstOrDefault(mode => string.Equals(mode.Id, modeId, StringComparison.Ordinal));
        if (target is null)
        {
            return ModeSwitchResult.Rejected(groupId, modeId, ErrorCodes.InvalidVars, $"The group '{groupId}' has no mode '{modeId}'.");
        }

        if (!_switching.TryAdd(groupId, modeId))
        {
            return ModeSwitchResult.Rejected(groupId, modeId, ErrorCodes.Busy, $"The group '{groupId}' is already being switched.");
        }

        try
        {
            return await ExecuteAsync(group, target, runStep, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switching.TryRemove(groupId, out _);
        }
    }

    private async Task<ModeSwitchResult> ExecuteAsync(ModeGroupDocument group, ModeDefinition target, ModeStepRunner runStep, CancellationToken cancellationToken)
    {
        ModeStateEntry? entry = _state.Get(group.Id);
        string? from = Resolve(group, entry?.Current);
        var log = new StepLog(string.Equals(group.OnError, SequenceErrorModes.Continue, StringComparison.Ordinal) ? OnErrorBehavior.Continue : OnErrorBehavior.Stop);

        // A switch that has started settles the application of onBoot for this boot: a pending apply_default must not overwrite the choice.
        await StoreAsync(group.Id, new ModeStateEntry(entry?.Current, entry?.ChangedAt, _bootId())).ConfigureAwait(false);
        RaiseStateChanged(group.Id);

        bool completed;
        try
        {
            ModeDefinition? previous = from is not null && !string.Equals(from, target.Id, StringComparison.Ordinal)
                ? group.Modes.FirstOrDefault(mode => string.Equals(mode.Id, from, StringComparison.Ordinal))
                : null;

            completed = previous is null || await RunStepsAsync(previous, "exit", previous.Exit, runStep, log, cancellationToken).ConfigureAwait(false);
            completed = completed && await RunStepsAsync(target, "enter", target.Enter, runStep, log, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The switch was cancelled or timed out half way: nothing can be said about the PC any more.
            await SetCurrentAsync(group.Id, null).ConfigureAwait(false);
            throw;
        }

        if (!completed)
        {
            await SetCurrentAsync(group.Id, null).ConfigureAwait(false);
            return log.ToResult(group.Id, target.Id, from, completed: false);
        }

        await SetCurrentAsync(group.Id, target.Id).ConfigureAwait(false);
        if (!string.Equals(from, target.Id, StringComparison.Ordinal))
        {
            await PublishChangedAsync(group.Id, from, target.Id).ConfigureAwait(false);
        }

        return log.ToResult(group.Id, target.Id, from, completed: true);
    }

    /// <summary>Runs the steps of one list. Returns false when a failing step ended the switch (<c>onError: stop</c>).</summary>
    private async Task<bool> RunStepsAsync(
        ModeDefinition mode,
        string phase,
        IReadOnlyList<SequenceStep>? steps,
        ModeStepRunner runStep,
        StepLog log,
        CancellationToken cancellationToken)
    {
        if (steps is null)
        {
            return true;
        }

        for (int index = 0; index < steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SequenceStep step = steps[index];

            if (step.DelaySeconds is { } delay)
            {
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), _time, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            string label = $"The {phase} step {(index + 1).ToString(CultureInfo.InvariantCulture)} of mode '{mode.Id}'";
            if (string.IsNullOrEmpty(step.Command))
            {
                log.Fail(null, label + " names no command and no delay.");
            }
            else if (IsModeCommand(step.Command))
            {
                log.Fail(ErrorCodes.Internal, $"{label} ('{step.Command}') failed: a step cannot switch a mode.");
            }
            else
            {
                RunResult result = await runStep(new RunRequest(step.Command, step.Vars, RunSources.Sequence), cancellationToken).ConfigureAwait(false);
                log.AppendOutput(result);
                if (result.Succeeded)
                {
                    continue;
                }

                string reason = result.ErrorCode is null ? $"exit code {result.ExitCode}" : $"{result.ErrorCode}: {result.ErrorMessage}";
                log.Fail(result.ErrorCode, $"{label} ('{step.Command}') failed: {reason}");
            }

            if (log.StopsOnError)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsModeCommand(string commandId)
    {
        return ModeNames.TryGetGroupId(commandId, out string groupId)
            && _config.Effective.GetDocument<CommandDocument>(commandId) is null
            && _config.Effective.GetDocument<ModeGroupDocument>(groupId) is not null;
    }

    private static string? Resolve(ModeGroupDocument group, string? current)
    {
        return current is not null && group.Modes.Any(mode => string.Equals(mode.Id, current, StringComparison.Ordinal)) ? current : null;
    }

    private async Task SetCurrentAsync(string groupId, string? current)
    {
        await StoreAsync(groupId, new ModeStateEntry(current, current is null ? null : _time.GetUtcNow(), _bootId())).ConfigureAwait(false);
        RaiseStateChanged(groupId);
    }

    private async Task StoreAsync(string groupId, ModeStateEntry entry)
    {
        try
        {
            await _state.SetAsync(groupId, entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The state is in memory; only its survival of a restart is lost.
            Faulted?.Invoke(this, new ModeEngineFaultEventArgs(groupId, "state", exception));
        }
    }

    private async Task PublishChangedAsync(string groupId, string? from, string to)
    {
#pragma warning disable CA1031 // A failing event must not turn a completed switch into a failure.
        try
        {
            await _events.PublishAsync(
                EventTypes.ModeChanged,
                EventData.Create(("group", groupId), ("from", from), ("to", to)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, new ModeEngineFaultEventArgs(groupId, "event", exception));
        }
#pragma warning restore CA1031
    }

    private void RaiseStateChanged(string groupId)
    {
        StateChanged?.Invoke(this, new ModeStateChangedEventArgs(groupId));
    }

    private async Task PumpAsync(ConfigSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConfigChange change in subscription.Changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind != ConfigKind.Mode)
                {
                    continue;
                }

                if (change.Type == ConfigChangeType.Removed)
                {
                    await RemoveStateAsync(change.Id).ConfigureAwait(false);
                }
                else if (change.Type == ConfigChangeType.Added && _state.Get(change.Id) is null)
                {
                    // A group that is created while the agent runs starts without a state; no boot behaviour applies to it.
                    await StoreAsync(change.Id, new ModeStateEntry(null, null, _bootId())).ConfigureAwait(false);
                }

                GroupsChanged?.Invoke(this, EventArgs.Empty);
                RaiseStateChanged(change.Id);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose stopped the pump.
        }
    }

    private async Task RemoveStateAsync(string groupId)
    {
        try
        {
            await _state.RemoveAsync(groupId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Faulted?.Invoke(this, new ModeEngineFaultEventArgs(groupId, "state", exception));
        }
    }

    private enum OnErrorBehavior
    {
        Stop,
        Continue,
    }

    private sealed class StepLog
    {
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _errors = new();
        private readonly OnErrorBehavior _onError;

        public StepLog(OnErrorBehavior onError)
        {
            _onError = onError;
        }

        public bool StopsOnError => _onError == OnErrorBehavior.Stop;

        private int Failures { get; set; }

        private string? FirstCode { get; set; }

        public void AppendOutput(RunResult result)
        {
            if (result.StandardOutput.Length == 0)
            {
                return;
            }

            _output.Append(result.StandardOutput);
            if (!result.StandardOutput.EndsWith('\n'))
            {
                _output.Append('\n');
            }
        }

        public void Fail(string? errorCode, string line)
        {
            Failures++;
            FirstCode ??= errorCode;
            _errors.Append(line).Append('\n');
        }

        public ModeSwitchResult ToResult(string groupId, string mode, string? from, bool completed)
        {
            return new ModeSwitchResult(
                groupId,
                mode,
                Started: true,
                completed,
                from,
                Failures,
                _output.ToString(),
                _errors.ToString(),
                completed ? null : FirstCode,
                null);
        }
    }
}
