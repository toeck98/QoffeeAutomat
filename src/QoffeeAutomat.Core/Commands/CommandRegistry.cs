using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Core.Commands;

/// <summary>
/// The commands that the agent can run: a snapshot of the command documents of the effective
/// configuration (PLAN.md T207). A background pump follows the change stream of the config store and
/// raises <see cref="Changed"/>; reads additionally compare the snapshot with the store, so a command
/// that was saved a moment ago is never missed and a run that follows a save always sees the new
/// document.
/// </summary>
public sealed class CommandRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly IConfigStore _config;
    private readonly ConfigSubscription _subscription;
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<CommandDocument> _commands = [];
    private Dictionary<string, CommandDocument> _byId = new(StringComparer.Ordinal);
    private IReadOnlyList<ModeGroupDocument> _groups = [];
    private IReadOnlyList<CommandDocument> _modeCommands = [];
    private Dictionary<string, CommandDocument> _modeById = new(StringComparer.Ordinal);
    private bool _notifyPending;
    private Task? _pump;

    /// <summary>Creates the registry from the current configuration and starts receiving changes.</summary>
    /// <param name="config">The configuration store; its effective view is read.</param>
    public CommandRegistry(IConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _subscription = config.Subscribe();
        Refresh(markPending: false);
    }

    /// <summary>Raised on a background thread after the set of commands or one of them changed. Handlers must not throw.</summary>
    public event EventHandler? Changed;

    /// <summary>All commands, enabled or not, ordered by id.</summary>
    public IReadOnlyList<CommandDocument> Commands
    {
        get
        {
            Refresh(markPending: true);
            lock (_gate)
            {
                return _commands;
            }
        }
    }

    /// <summary>
    /// The commands that the mode groups provide (<c>mode_&lt;groupId&gt;</c>, PLAN.md S06, T717), ordered by group id. They are derived from the
    /// mode group documents, not stored, and are not part of <see cref="Commands"/>. A group whose command id is taken by a stored command
    /// provides none: the stored command wins.
    /// </summary>
    public IReadOnlyList<CommandDocument> ModeGroupCommands
    {
        get
        {
            Refresh(markPending: true);
            lock (_gate)
            {
                return _modeCommands;
            }
        }
    }

    /// <summary>Finds a command: a stored one, the command of a mode group, or one of the <see cref="SystemCommands"/>.</summary>
    /// <param name="id">The command id.</param>
    /// <returns>The document, or null when there is no such command.</returns>
    public CommandDocument? TryGet(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        Refresh(markPending: true);
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id) ?? _modeById.GetValueOrDefault(id) ?? SystemCommands.TryGet(id);
        }
    }

    /// <summary>Tells whether a command is the generated command of a mode group, which the engine hands to the mode engine.</summary>
    /// <param name="command">The command as <see cref="TryGet"/> returned it.</param>
    /// <returns>True for the instance that the registry holds for a mode group; a draft or a stored command with the same id is not one.</returns>
    public bool IsModeCommand(CommandDocument command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_gate)
        {
            return _modeById.TryGetValue(command.Id, out CommandDocument? generated) && ReferenceEquals(generated, command);
        }
    }

    /// <summary>Starts the background pump that raises <see cref="Changed"/>. Calling it twice has no effect.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _pump ??= Task.Run(() => PumpAsync(_stop.Token));
        }
    }

    /// <summary>Stops the pump and the subscription.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _subscription.Dispose();
        Task? pump;
        lock (_gate)
        {
            pump = _pump;
        }

        try
        {
            pump?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The pump was stopped on purpose.
        }

        _stop.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConfigChange change in _subscription.Changes.ReadAllAsync(cancellationToken))
            {
                if (change.Kind is not (ConfigKind.Command or ConfigKind.Mode))
                {
                    continue;
                }

                Refresh(markPending: true);
                bool raise;
                lock (_gate)
                {
                    raise = _notifyPending;
                    _notifyPending = false;
                }

                if (raise)
                {
                    RaiseChanged();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose stopped the pump.
        }
    }

    private void Refresh(bool markPending)
    {
        IReadOnlyList<CommandDocument> current = _config.Effective.List<CommandDocument>();
        IReadOnlyList<ModeGroupDocument> groups = _config.Effective.List<ModeGroupDocument>();
        lock (_gate)
        {
            bool commandsChanged = !SameDocuments(_commands, current);
            bool groupsChanged = !SameDocuments(_groups, groups);
            if (!commandsChanged && !groupsChanged)
            {
                return;
            }

            if (commandsChanged)
            {
                _commands = current;
                _byId = current.ToDictionary(command => command.Id, StringComparer.Ordinal);
            }

            _groups = groups;
            _modeCommands =
            [
                .. groups
                    .Select(Modes.ModeCommands.Generate)
                    .Where(command => !_byId.ContainsKey(command.Id)),
            ];
            _modeById = _modeCommands.ToDictionary(command => command.Id, StringComparer.Ordinal);
            _notifyPending |= markPending;
        }
    }

    private void RaiseChanged()
    {
#pragma warning disable CA1031 // A faulty subscriber must not stop the pump.
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Handlers must not throw; the next change is still delivered.
        }
#pragma warning restore CA1031
    }

    private static bool SameDocuments<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        where T : class
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!ReferenceEquals(left[index], right[index]) && !left[index].Equals(right[index]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The global pause flag (maintenance mode, PLAN.md S07 error <c>paused</c>, S08 <c>/status</c>).</summary>
public interface IAgentPause
{
    /// <summary>True while every run is rejected with <c>paused</c>.</summary>
    bool IsPaused { get; }

    /// <summary>Pauses or resumes the agent; runs that are already running are not affected.</summary>
    /// <param name="paused">The new value.</param>
    void SetPaused(bool paused);
}

/// <summary>The default <see cref="IAgentPause"/>: a flag that the API sets.</summary>
public sealed class AgentPause : IAgentPause
{
    private volatile bool _isPaused;

    /// <inheritdoc />
    public bool IsPaused => _isPaused;

    /// <summary>Raised after the flag changed.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
