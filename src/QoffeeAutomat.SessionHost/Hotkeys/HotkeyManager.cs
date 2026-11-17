using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.SessionHost.Hotkeys;

/// <summary>What the operating system said to a registration.</summary>
public enum HotkeyRegistrationResult
{
    /// <summary>The combination is registered.</summary>
    Registered,

    /// <summary>Another program (or another registration of this process) owns the combination.</summary>
    Conflict,

    /// <summary>Windows refused the registration for another reason.</summary>
    Invalid,
}

/// <summary>Raised when a registered hotkey was pressed.</summary>
public sealed class HotkeyPressedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="name">The configured name of the hotkey.</param>
    public HotkeyPressedEventArgs(string name)
    {
        Name = name;
    }

    /// <summary>The configured name of the hotkey.</summary>
    public string Name { get; }
}

/// <summary>Raised by <see cref="IHotkeyApi"/> when Windows reports that a registered combination was pressed.</summary>
public sealed class HotkeyIdEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="id">The id that was given to <see cref="IHotkeyApi.Register"/>.</param>
    public HotkeyIdEventArgs(int id)
    {
        Id = id;
    }

    /// <summary>The id of the registration.</summary>
    public int Id { get; }
}

/// <summary>The part of the hotkey support that touches Windows (<c>RegisterHotKey</c>); <see cref="WindowsHotkeyApi"/> is the real one.</summary>
public interface IHotkeyApi : IDisposable
{
    /// <summary>Raised when a registered combination is pressed. May be raised on any thread.</summary>
    event EventHandler<HotkeyIdEventArgs>? Pressed;

    /// <summary>Registers a combination.</summary>
    /// <param name="id">An id that is unique for this process and that <see cref="Pressed"/> reports.</param>
    /// <param name="modifiers">The modifier keys.</param>
    /// <param name="virtualKey">The virtual-key code.</param>
    /// <param name="detail">An explanation when the registration failed.</param>
    /// <returns>What Windows said.</returns>
    HotkeyRegistrationResult Register(int id, HotkeyModifiers modifiers, int virtualKey, out string? detail);

    /// <summary>Removes a registration; an unknown id is ignored.</summary>
    /// <param name="id">The id given to <see cref="Register"/>.</param>
    void Unregister(int id);
}

/// <summary>
/// Registers the global hotkeys that the agent sends (PLAN.md S16, T708) and reports what became of each one. A combination
/// that cannot be parsed is <c>invalid</c>; a combination that Windows refuses because it is taken is a <c>conflict</c>; a
/// name or a combination that the list contains twice is reported without asking Windows. Every list replaces the previous
/// one completely. The manager does not decide what a press means: it raises <see cref="HotkeyPressed"/> with the configured
/// name and the caller reports it to the agent.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int MaxNameLength = 63;

    private readonly IHotkeyApi _api;
    private readonly ISessionHostLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, string> _names = [];
    private List<SessionHotkeyStatus> _status = [];
    private int _nextId;
    private bool _disposed;

    /// <summary>Creates the manager and starts listening for presses.</summary>
    /// <param name="api">Registers combinations with the operating system.</param>
    /// <param name="log">The diagnostics log.</param>
    public HotkeyManager(IHotkeyApi api, ISessionHostLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
        _log = log ?? NullSessionHostLog.Instance;
        _api.Pressed += OnPressed;
    }

    /// <summary>Raised when a registered hotkey was pressed. May be raised on any thread; handlers must return quickly.</summary>
    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>The outcome of the last <see cref="Apply"/>.</summary>
    public IReadOnlyList<SessionHotkeyStatus> Status
    {
        get
        {
            lock (_gate)
            {
                return [.. _status];
            }
        }
    }

    /// <summary>Replaces all registrations by the given list.</summary>
    /// <param name="hotkeys">The hotkeys to register.</param>
    /// <returns>One status per hotkey, in the order of the list.</returns>
    public IReadOnlyList<SessionHotkeyStatus> Apply(IReadOnlyList<SessionHotkeyDefinition> hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            UnregisterAll();

            var result = new List<SessionHotkeyStatus>(hotkeys.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SessionHotkeyDefinition hotkey in hotkeys)
            {
                SessionHotkeyStatus status = Register(hotkey, names, owners);
                if (status.State != SessionHotkeyStates.Registered)
                {
                    _log.Warning($"The hotkey '{hotkey.Name}' ({hotkey.Keys}) is not active: {status.State}. {status.Message}");
                }

                result.Add(status);
            }

            _status = result;
            return [.. result];
        }
    }

    /// <summary>Removes every registration, for example when the connection to the agent is lost: a key that does nothing should not be taken away from other programs.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            UnregisterAll();
            _status = [];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            UnregisterAll();
            _disposed = true;
        }

        _api.Pressed -= OnPressed;
    }

    private SessionHotkeyStatus Register(SessionHotkeyDefinition hotkey, HashSet<string> names, Dictionary<string, string> owners)
    {
        if (string.IsNullOrWhiteSpace(hotkey.Name) || hotkey.Name.Length > MaxNameLength)
        {
            return new SessionHotkeyStatus(hotkey.Name ?? string.Empty, hotkey.Keys, SessionHotkeyStates.Invalid, "The name is empty or longer than 63 characters.");
        }

        if (!names.Add(hotkey.Name))
        {
            return new SessionHotkeyStatus(hotkey.Name, hotkey.Keys, SessionHotkeyStates.Invalid, "The name is used by another hotkey in the list.");
        }

        if (!HotkeyGesture.TryParse(hotkey.Keys, out HotkeyGesture? gesture, out string? problem))
        {
            return new SessionHotkeyStatus(hotkey.Name, hotkey.Keys, SessionHotkeyStates.Invalid, problem);
        }

        if (owners.TryGetValue(gesture!.Canonical, out string? owner))
        {
            return new SessionHotkeyStatus(hotkey.Name, hotkey.Keys, SessionHotkeyStates.Conflict, "The hotkey '" + owner + "' already uses " + gesture.Canonical + ".");
        }

        int id = ++_nextId;
        HotkeyRegistrationResult outcome = _api.Register(id, gesture.Modifiers, gesture.VirtualKey, out string? detail);
        switch (outcome)
        {
            case HotkeyRegistrationResult.Registered:
                _names[id] = hotkey.Name;
                owners[gesture.Canonical] = hotkey.Name;
                return new SessionHotkeyStatus(hotkey.Name, hotkey.Keys, SessionHotkeyStates.Registered);
            case HotkeyRegistrationResult.Conflict:
                return new SessionHotkeyStatus(
                    hotkey.Name,
                    hotkey.Keys,
                    SessionHotkeyStates.Conflict,
                    detail ?? "Another program already uses " + gesture.Canonical + ".");
            default:
                return new SessionHotkeyStatus(
                    hotkey.Name,
                    hotkey.Keys,
                    SessionHotkeyStates.Invalid,
                    detail ?? "Windows did not accept " + gesture.Canonical + ".");
        }
    }

    private void UnregisterAll()
    {
        foreach (int id in _names.Keys)
        {
            _api.Unregister(id);
        }

        _names.Clear();
    }

    private void OnPressed(object? sender, HotkeyIdEventArgs e)
    {
        string? name;
        lock (_gate)
        {
            if (!_names.TryGetValue(e.Id, out name))
            {
                return;
            }
        }

        HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(name));
    }
}
