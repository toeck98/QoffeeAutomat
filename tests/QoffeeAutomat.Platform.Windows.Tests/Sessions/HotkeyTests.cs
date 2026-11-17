using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.SessionHost.Hotkeys;
using QoffeeAutomat.SessionHost.Ipc;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// Global hotkeys (PLAN.md S16, T708): the registration with conflicts and invalid combinations, and the way from the settings over
/// the session pipe to the event <c>hotkey_pressed</c>. <c>RegisterHotKey</c> itself needs a desktop and is checked by hand
/// (human_verify); everything else runs here through a real named pipe and a fake registration API.
/// </summary>
[Trait("Category", "Hotkeys")]
public sealed class HotkeyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTime _time = new();

    public void Dispose() => _temp.Dispose();

    private static SessionHotkeyDefinition Hotkey(string name, string keys) => new(name, keys);

    [Fact]
    public void A_valid_hotkey_is_registered_and_a_press_reports_its_configured_name()
    {
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);
        var pressed = new List<string>();
        manager.HotkeyPressed += (_, e) => pressed.Add(e.Name);

        IReadOnlyList<SessionHotkeyStatus> status = manager.Apply([Hotkey("mute_mic", "ctrl + alt + F9")]);

        SessionHotkeyStatus entry = Assert.Single(status);
        Assert.Equal("mute_mic", entry.Name);
        Assert.Equal(SessionHotkeyStates.Registered, entry.State);
        Assert.Null(entry.Message);
        (HotkeyModifiers modifiers, int key) = Assert.Single(api.Snapshot()).Value;
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, modifiers);
        Assert.Equal(0x78, key);

        api.Press(api.Snapshot().Keys.Single());
        Assert.Equal(["mute_mic"], pressed);
    }

    [Fact]
    public void A_combination_that_windows_gives_to_another_program_is_a_conflict()
    {
        using var api = new FakeHotkeyApi();
        api.Taken.Add(0x78);
        using var manager = new HotkeyManager(api);

        IReadOnlyList<SessionHotkeyStatus> status = manager.Apply([Hotkey("taken", "Ctrl+Alt+F9"), Hotkey("free", "Ctrl+Alt+F10")]);

        Assert.Equal(SessionHotkeyStates.Conflict, status[0].State);
        Assert.False(string.IsNullOrWhiteSpace(status[0].Message));
        Assert.Equal(SessionHotkeyStates.Registered, status[1].State);
        Assert.Single(api.Snapshot());
    }

    [Theory]
    [InlineData("F9")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Hyper+F9")]
    [InlineData("")]
    public void Keys_that_cannot_be_registered_are_invalid_and_never_reach_windows(string keys)
    {
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);

        SessionHotkeyStatus entry = Assert.Single(manager.Apply([Hotkey("bad", keys)]));

        Assert.Equal(SessionHotkeyStates.Invalid, entry.State);
        Assert.False(string.IsNullOrWhiteSpace(entry.Message));
        Assert.Equal(0, api.RegisterCalls);
    }

    [Fact]
    public void A_name_or_a_combination_that_the_list_uses_twice_is_reported_without_asking_windows()
    {
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);

        IReadOnlyList<SessionHotkeyStatus> status = manager.Apply(
        [
            Hotkey("one", "Ctrl+Alt+F9"),
            Hotkey("one", "Ctrl+Alt+F10"),
            Hotkey("two", "Alt+Ctrl+f9"),
        ]);

        Assert.Equal(SessionHotkeyStates.Registered, status[0].State);
        Assert.Equal(SessionHotkeyStates.Invalid, status[1].State);
        Assert.Equal(SessionHotkeyStates.Conflict, status[2].State);
        Assert.Contains("one", status[2].Message, StringComparison.Ordinal);
        Assert.Equal(1, api.RegisterCalls);
    }

    [Fact]
    public void A_new_list_replaces_the_old_one_and_a_press_of_a_removed_hotkey_does_nothing()
    {
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);
        var pressed = new List<string>();
        manager.HotkeyPressed += (_, e) => pressed.Add(e.Name);
        manager.Apply([Hotkey("old", "Ctrl+Alt+F9")]);
        int oldId = api.Snapshot().Keys.Single();

        manager.Apply([Hotkey("new", "Ctrl+Alt+F10")]);

        Assert.DoesNotContain(oldId, api.Snapshot().Keys);
        api.Press(oldId);
        Assert.Empty(pressed);
        api.Press(api.Snapshot().Keys.Single());
        Assert.Equal(["new"], pressed);
        Assert.Equal(["new"], manager.Status.Select(entry => entry.Name));
    }

    [Fact]
    public void Clear_releases_every_hotkey()
    {
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);
        manager.Apply([Hotkey("a", "Ctrl+Alt+F9"), Hotkey("b", "Ctrl+Alt+F10")]);

        manager.Clear();

        Assert.Empty(api.Snapshot());
        Assert.Empty(manager.Status);
    }

    [Fact]
    public async Task A_hotkey_of_the_settings_is_registered_in_the_session_and_its_press_becomes_the_event()
    {
        using var h = new SessionHarness();
        using ConfigStore store = await OpenStoreAsync();
        await SaveHotkeysAsync(store, new HotkeySettings("mute_mic", "Ctrl+Alt+F9"), new HotkeySettings("off", "Ctrl+Alt+F10", Enabled: false));
        await h.StartAsync();
        using var service = new HotkeyService(h.Manager, () => h.Bridge.ActiveConnection, store, h.Bus, h.Logger);
        await service.StartAsync(CancellationToken.None);
        using EventSubscription events = h.Bus.Subscribe([EventTypes.HotkeyPressed]);
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);
        HotkeyCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new HotkeyCoordinator(client, manager));
        using HotkeyCoordinator running = coordinator!;

        // Only the enabled hotkey reaches the session helper.
        await TestWait.UntilAsync(() => api.Snapshot().Count == 1, "the registration of the hotkey");
        await TestWait.UntilAsync(() => service.GetStatus().Any(entry => entry.State == SessionHotkeyStates.Registered), "the status of the hotkey");
        Assert.True(service.IsSupported);
        Assert.Equal(["mute_mic"], service.GetStatus().Select(entry => entry.Name));

        api.Press(api.Snapshot().Keys.Single());

        using var timeout = new CancellationTokenSource(TestWait.Patience);
        AgentEvent published = await events.Events.ReadAsync(timeout.Token);
        Assert.Equal(EventTypes.HotkeyPressed, published.Type);
        Assert.Equal("mute_mic", published.Data!.Value.GetProperty("name").GetString());

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_conflict_that_the_session_reports_is_available_for_the_ui()
    {
        using var h = new SessionHarness();
        using ConfigStore store = await OpenStoreAsync();
        await SaveHotkeysAsync(store, new HotkeySettings("taken", "Ctrl+Alt+F9"));
        await h.StartAsync();
        using var service = new HotkeyService(h.Manager, () => h.Bridge.ActiveConnection, store, h.Bus, h.Logger);
        await service.StartAsync(CancellationToken.None);
        using var api = new FakeHotkeyApi();
        api.Taken.Add(0x78);
        using var manager = new HotkeyManager(api);
        HotkeyCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new HotkeyCoordinator(client, manager));
        using HotkeyCoordinator running = coordinator!;

        await TestWait.UntilAsync(() => service.GetStatus().Count == 1, "the answer of the session helper");

        SessionHotkeyStatus status = Assert.Single(service.GetStatus());
        Assert.Equal("taken", status.Name);
        Assert.Equal(SessionHotkeyStates.Conflict, status.State);
        Assert.Contains(h.Logger.Entries, entry => entry.Message.Contains("not active", StringComparison.Ordinal));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Changing_the_settings_registers_the_new_hotkeys_in_the_running_session()
    {
        using var h = new SessionHarness();
        using ConfigStore store = await OpenStoreAsync();
        await SaveHotkeysAsync(store, new HotkeySettings("first", "Ctrl+Alt+F9"));
        await h.StartAsync();
        using var service = new HotkeyService(h.Manager, () => h.Bridge.ActiveConnection, store, h.Bus, h.Logger);
        await service.StartAsync(CancellationToken.None);
        using var api = new FakeHotkeyApi();
        using var manager = new HotkeyManager(api);
        HotkeyCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new HotkeyCoordinator(client, manager));
        using HotkeyCoordinator running = coordinator!;
        await TestWait.UntilAsync(() => api.Snapshot().Count == 1, "the first hotkey");

        await SaveHotkeysAsync(store, new HotkeySettings("second", "Ctrl+Alt+F10"), new HotkeySettings("third", "Ctrl+Alt+F11"));

        await TestWait.UntilAsync(() => api.Snapshot().Count == 2, "the new list");
        await TestWait.UntilAsync(() => service.GetStatus().Count == 2, "the status of the new list");
        Assert.Equal(["second", "third"], service.GetStatus().Select(entry => entry.Name));

        await service.StopAsync(CancellationToken.None);
    }

    private async Task<ConfigStore> OpenStoreAsync()
    {
        var store = new ConfigStore(
            new ConfigStoreOptions
            {
                ConfigDirectory = Path.Combine(_temp.Path, "config"),
                ScriptsDirectory = Path.Combine(_temp.Path, "scripts"),
                EnableWatcher = false,
            },
            _time);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private static async Task SaveHotkeysAsync(ConfigStore store, params HotkeySettings[] hotkeys)
    {
        var settings = new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Gaming-PC", Hotkeys: hotkeys);
        Assert.True(DocumentValidator.ValidateSettings(settings).IsValid);
        await store.Local.SaveAsync(settings, expectedRevision: null, CancellationToken.None);
    }

    private sealed class FakeHotkeyApi : IHotkeyApi
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, (HotkeyModifiers Modifiers, int Key)> _registered = [];
        private int _registerCalls;

        public event EventHandler<HotkeyIdEventArgs>? Pressed;

        /// <summary>The virtual-key codes that "another program" owns.</summary>
        public HashSet<int> Taken { get; } = [];

        public int RegisterCalls => Volatile.Read(ref _registerCalls);

        public Dictionary<int, (HotkeyModifiers Modifiers, int Key)> Snapshot()
        {
            lock (_gate)
            {
                return new Dictionary<int, (HotkeyModifiers Modifiers, int Key)>(_registered);
            }
        }

        public HotkeyRegistrationResult Register(int id, HotkeyModifiers modifiers, int virtualKey, out string? detail)
        {
            Interlocked.Increment(ref _registerCalls);
            if (Taken.Contains(virtualKey))
            {
                detail = "Another program already uses this key combination.";
                return HotkeyRegistrationResult.Conflict;
            }

            lock (_gate)
            {
                _registered[id] = (modifiers, virtualKey);
            }

            detail = null;
            return HotkeyRegistrationResult.Registered;
        }

        public void Unregister(int id)
        {
            lock (_gate)
            {
                _registered.Remove(id);
            }
        }

        public void Press(int id) => Pressed?.Invoke(this, new HotkeyIdEventArgs(id));

        public void Dispose()
        {
        }
    }
}
