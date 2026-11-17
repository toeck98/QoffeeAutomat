using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// The agent's half of the global hotkeys (PLAN.md S11, S16, T708). It reads the <c>hotkeys</c> of the settings, sends the enabled
/// ones to every connected SessionHost (<c>hotkeys</c>, again whenever the settings or the connections change), keeps the answers
/// (<c>hotkey_state</c>) so that the UI can report conflicts, and turns every press that a SessionHost reports into the event
/// <c>hotkey_pressed</c> with the configured name, which Home Assistant receives like any other event.
/// </summary>
/// <remarks>
/// A press is published only when its name is one of the enabled hotkeys of the settings: the event stream does not carry names
/// that the user never defined, whatever a session helper sends.
/// </remarks>
public sealed class HotkeyService : IHostedService, IHotkeyStatusSource, IDisposable
{
    private readonly WindowsSessionManager _manager;
    private readonly Func<SessionConnection?> _activeConnection;
    private readonly IConfigStore _config;
    private readonly IEventBus _bus;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<SessionConnection> _attached = [];
    private readonly ConcurrentDictionary<SessionConnection, IReadOnlyList<SessionHotkeyStatus>> _status = new();
    private readonly CancellationTokenSource _stop = new();
    private ConfigSubscription? _settings;
    private Task _settingsPump = Task.CompletedTask;
    private IReadOnlyList<SessionHotkeyDefinition> _last = [];

    /// <summary>Creates the service.</summary>
    /// <param name="manager">Holds the connections of the SessionHosts.</param>
    /// <param name="activeConnection">Gives the connection of the active console session; null when there is none.</param>
    /// <param name="config">The settings.</param>
    /// <param name="bus">Receives the <c>hotkey_pressed</c> events.</param>
    /// <param name="logger">The logger.</param>
    public HotkeyService(
        WindowsSessionManager manager,
        Func<SessionConnection?> activeConnection,
        IConfigStore config,
        IEventBus bus,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(activeConnection);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _activeConnection = activeConnection;
        _config = config;
        _bus = bus;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public IReadOnlyList<SessionHotkeyStatus> GetStatus()
    {
        SessionConnection? connection = _activeConnection();
        if (connection is not null && _status.TryGetValue(connection, out IReadOnlyList<SessionHotkeyStatus>? active))
        {
            return active;
        }

        // Nobody at the console: the answer of any other session still tells about conflicts.
        return _status.Values.FirstOrDefault() ?? [];
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _manager.ConnectionsChanged += OnConnectionsChanged;
        _settings = _config.Subscribe();
        ConfigSubscription settings = _settings;
        _settingsPump = Task.Run(() => PumpSettingsAsync(settings, _stop.Token), CancellationToken.None);
        AttachAndPush(forceAll: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _manager.ConnectionsChanged -= OnConnectionsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        _settings?.Dispose();
        await _settingsPump.ConfigureAwait(false);

        lock (_gate)
        {
            foreach (SessionConnection connection in _attached)
            {
                Detach(connection);
            }

            _attached.Clear();
        }

        _status.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();

    /// <summary>The hotkeys that the settings define and enable, as they are sent to the session helpers.</summary>
    /// <returns>The definitions; empty when there are none.</returns>
    public IReadOnlyList<SessionHotkeyDefinition> ReadDefinitions()
    {
        IReadOnlyList<HotkeySettings>? hotkeys = _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.Hotkeys;
        if (hotkeys is null)
        {
            return [];
        }

        return [.. hotkeys.Where(hotkey => hotkey.EffectiveEnabled).Select(hotkey => new SessionHotkeyDefinition(hotkey.Name, hotkey.Keys))];
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) => AttachAndPush(forceAll: false);

    private void AttachAndPush(bool forceAll)
    {
        IReadOnlyList<SessionConnection> open = _manager.GetConnections();
        IReadOnlyList<SessionHotkeyDefinition> definitions = ReadDefinitions();
        var fresh = new List<SessionConnection>();

        lock (_gate)
        {
            _last = definitions;
            foreach (SessionConnection connection in open)
            {
                if (_attached.Add(connection))
                {
                    connection.EventReceived += OnEvent;
                    connection.HotkeyStateReceived += OnState;
                    fresh.Add(connection);
                }
            }

            foreach (SessionConnection connection in _attached.Where(connection => !open.Contains(connection)).ToArray())
            {
                Detach(connection);
                _attached.Remove(connection);
                _status.TryRemove(connection, out _);
            }
        }

        // A new session helper gets the list at once. When the list itself changed (or at the start) everybody gets it, an
        // empty one too, so that removed hotkeys are released. A new helper has none registered, so an empty list is no news.
        foreach (SessionConnection connection in forceAll ? open : fresh)
        {
            if (definitions.Count > 0 || forceAll)
            {
                _ = SendAsync(connection, definitions);
            }
        }
    }

    private void Detach(SessionConnection connection)
    {
        connection.EventReceived -= OnEvent;
        connection.HotkeyStateReceived -= OnState;
    }

    private async Task SendAsync(SessionConnection connection, IReadOnlyList<SessionHotkeyDefinition> definitions)
    {
        try
        {
            await connection.SendHotkeysAsync(definitions, CancellationToken.None).ConfigureAwait(false);
            if (definitions.Count == 0)
            {
                _status.TryRemove(connection, out _);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            SessionLog.HotkeysNotSent(_logger, connection.SessionId, exception.Message);
        }
    }

    private void OnState(object? sender, SessionHotkeyStateEventArgs e)
    {
        if (sender is not SessionConnection connection)
        {
            return;
        }

        _status[connection] = e.Message.Hotkeys;
        foreach (SessionHotkeyStatus hotkey in e.Message.Hotkeys.Where(hotkey => hotkey.State != SessionHotkeyStates.Registered))
        {
            SessionLog.HotkeyNotActive(_logger, hotkey.Name, hotkey.Keys, hotkey.State, hotkey.Message ?? string.Empty);
        }
    }

    private void OnEvent(object? sender, SessionHostEventArgs e)
    {
        if (e.Message.Type != SessionHotkeyEvents.Pressed
            || e.Message.Data is not { ValueKind: JsonValueKind.Object } data
            || !data.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            return;
        }

        bool known;
        lock (_gate)
        {
            known = _last.Any(hotkey => string.Equals(hotkey.Name, name, StringComparison.Ordinal));
        }

        if (!known)
        {
            return;
        }

        _ = PublishAsync(name);
    }

    private async Task PublishAsync(string name)
    {
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteEndObject();
            }

            using JsonDocument document = JsonDocument.Parse(stream.ToArray());
            await _bus.PublishAsync(EventTypes.HotkeyPressed, document.RootElement, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The agent is stopping.
        }
    }

    private async Task PumpSettingsAsync(ConfigSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ConfigChange change in subscription.Changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind != ConfigKind.Settings)
                {
                    continue;
                }

                IReadOnlyList<SessionHotkeyDefinition> current = ReadDefinitions();
                bool changed;
                lock (_gate)
                {
                    changed = !_last.SequenceEqual(current);
                }

                if (changed)
                {
                    AttachAndPush(forceAll: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }
    }
}
