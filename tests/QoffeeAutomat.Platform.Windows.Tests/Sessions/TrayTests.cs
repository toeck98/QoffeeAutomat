using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.SessionHost.Ipc;
using QoffeeAutomat.SessionHost.Tray;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The tray icon (PLAN.md S11, T310): what the icon shows for each state, and the loop between the icon, the SessionHost
/// client, the pipe and the agent's global pause flag. The Windows Forms shell itself needs a desktop and is checked by hand
/// (open item O-06); everything else runs here through a real named pipe and a fake shell.
/// </summary>
[Trait("Category", "Tray")]
public sealed class TrayTests
{
    private static SessionTrayStateMessage State(
        bool paused = false,
        int paired = 1,
        int connected = 1,
        bool show = true,
        string? url = "http://127.0.0.1:8480/",
        string name = "Gaming-PC")
    {
        return new SessionTrayStateMessage(name, paused, paired, connected, show, url);
    }

    [Fact]
    public void The_icon_stays_hidden_until_the_agent_reports_a_state()
    {
        TrayView view = TrayPresenter.Present(null, connected: true);

        Assert.False(view.Visible);
        Assert.False(view.CanPause);
        Assert.False(view.CanResume);
        Assert.False(view.CanOpenUi);
    }

    [Fact]
    public void The_setting_that_hides_the_icon_hides_it_in_every_state()
    {
        Assert.False(TrayPresenter.Present(State(show: false), connected: true).Visible);
        Assert.False(TrayPresenter.Present(State(show: false, paused: true), connected: true).Visible);
        Assert.False(TrayPresenter.Present(State(show: false), connected: false).Visible);
    }

    [Fact]
    public void A_paired_agent_with_a_link_shows_the_connected_icon_with_the_name_in_the_tooltip()
    {
        TrayView view = TrayPresenter.Present(State(), connected: true);

        Assert.True(view.Visible);
        Assert.Equal(TrayIconState.Connected, view.Icon);
        Assert.Equal("Gaming-PC: connected to Home Assistant", view.Tooltip);
        Assert.True(view.CanOpenUi);
        Assert.True(view.CanPause);
        Assert.False(view.CanResume);
        Assert.Equal(new Uri("http://127.0.0.1:8480/"), view.UiUri);
    }

    [Fact]
    public void A_paired_agent_that_nobody_is_connected_to_still_shows_the_connected_icon()
    {
        TrayView view = TrayPresenter.Present(State(connected: 0), connected: true);

        Assert.Equal(TrayIconState.Connected, view.Icon);
        Assert.Equal("Gaming-PC: paired, Home Assistant not connected", view.Tooltip);
    }

    [Fact]
    public void An_agent_without_a_paired_client_shows_the_not_paired_icon()
    {
        TrayView view = TrayPresenter.Present(State(paired: 0, connected: 0), connected: true);

        Assert.Equal(TrayIconState.NotPaired, view.Icon);
        Assert.Equal("Gaming-PC: not paired with Home Assistant", view.Tooltip);
    }

    [Fact]
    public void A_paused_agent_shows_the_paused_icon_and_offers_resume_instead_of_pause()
    {
        TrayView paired = TrayPresenter.Present(State(paused: true), connected: true);
        TrayView unpaired = TrayPresenter.Present(State(paused: true, paired: 0, connected: 0), connected: true);

        foreach (TrayView view in new[] { paired, unpaired })
        {
            Assert.Equal(TrayIconState.Paused, view.Icon);
            Assert.EndsWith("commands paused", view.Tooltip, StringComparison.Ordinal);
            Assert.False(view.CanPause);
            Assert.True(view.CanResume);
        }
    }

    [Fact]
    public void While_the_agent_is_away_the_icon_is_grey_and_only_the_about_box_works()
    {
        TrayView view = TrayPresenter.Present(State(), connected: false);

        Assert.True(view.Visible);
        Assert.Equal(TrayIconState.Disconnected, view.Icon);
        Assert.Equal("Gaming-PC: agent not reachable", view.Tooltip);
        Assert.False(view.CanOpenUi);
        Assert.False(view.CanPause);
        Assert.False(view.CanResume);
    }

    [Fact]
    public void A_long_agent_name_cannot_make_the_tooltip_longer_than_the_notification_area_allows()
    {
        TrayView view = TrayPresenter.Present(State(name: new string('x', 300)), connected: true);

        Assert.Equal(TrayPresenter.MaxTooltipLength, view.Tooltip.Length);
        Assert.EndsWith("...", view.Tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8480/", true)]
    [InlineData("http://localhost:8480/", true)]
    [InlineData("http://[::1]:8480/", true)]
    [InlineData("https://127.0.0.1:8480/", false)]
    [InlineData("http://192.168.1.20:8480/", false)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://user:pass@127.0.0.1:8480/", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("calc.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Open_UI_opens_only_loopback_http_addresses(string? url, bool accepted)
    {
        Assert.Equal(accepted, TrayPresenter.ParseUiUrl(url) is not null);
    }

    [Fact]
    public async Task The_icon_follows_the_agent_and_the_menu_pauses_and_resumes_the_commands_of_every_source()
    {
        using var h = new SessionHarness();
        await h.StartAsync();
        var pause = new AgentPause();
        var source = new FakeTrayStatusSource(new TrayStatus("Gaming-PC", 0, 0, true, "http://127.0.0.1:8480/"));
        using var service = new TrayStateService(h.Manager, h.Bridge, pause, source, TimeProvider.System, h.Logger, TimeSpan.FromMilliseconds(50));
        await service.StartAsync(CancellationToken.None);
        using var shell = new FakeTrayShell();
        TrayCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new TrayCoordinator(client, shell));
        using TrayCoordinator tray = coordinator!;

        // The agent reports the state after the handshake: not paired yet.
        await TestWait.UntilAsync(() => shell.Last is { Visible: true, Icon: TrayIconState.NotPaired }, "the not paired icon");
        Assert.Equal("Gaming-PC: not paired with Home Assistant", shell.Last!.Tooltip);

        // Pairing and a link connection change the icon without a click.
        source.Status = source.Status with { PairedClients = 1, ConnectedClients = 1 };
        await TestWait.UntilAsync(() => shell.Last is { Icon: TrayIconState.Connected }, "the connected icon");

        // Pause commands: the agent's flag is set, which is what /status reports and what the engine rejects with 'paused'.
        shell.ChoosePause(true);
        await TestWait.UntilAsync(() => pause.IsPaused, "the global pause");
        await TestWait.UntilAsync(() => shell.Last is { Icon: TrayIconState.Paused, CanResume: true }, "the paused icon");

        // Resume commands.
        shell.ChoosePause(false);
        await TestWait.UntilAsync(() => !pause.IsPaused, "the resumed commands");
        await TestWait.UntilAsync(() => shell.Last is { Icon: TrayIconState.Connected, CanPause: true }, "the connected icon again");

        // The pause flag changed elsewhere (the API) shows in the icon as well.
        pause.SetPaused(true);
        await TestWait.UntilAsync(() => shell.Last is { Icon: TrayIconState.Paused }, "the icon of a pause set elsewhere");

        // The setting trayIcon: false removes the icon.
        source.Status = source.Status with { ShowTrayIcon = false };
        await TestWait.UntilAsync(() => shell.Last is { Visible: false }, "the hidden icon");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_session_helper_that_is_not_in_the_console_session_cannot_pause_the_commands()
    {
        using var h = new SessionHarness();
        await h.StartAsync();
        var pause = new AgentPause();
        var source = new FakeTrayStatusSource(new TrayStatus("Gaming-PC", 1, 1, true, "http://127.0.0.1:8480/"));
        using var service = new TrayStateService(h.Manager, h.Bridge, pause, source, TimeProvider.System, h.Logger, TimeSpan.FromMilliseconds(50));
        await service.StartAsync(CancellationToken.None);
        using var shell = new FakeTrayShell();
        TrayCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new TrayCoordinator(client, shell));
        using TrayCoordinator tray = coordinator!;
        await TestWait.UntilAsync(() => shell.Last is { Icon: TrayIconState.Connected }, "the connected icon");

        // Another session became the console: the helper of this one is only a background session now.
        h.Lookup.Console = 99;
        shell.ChoosePause(true);

        await TestWait.UntilAsync(
            () => h.Logger.Entries.Any(entry => entry.Message.Contains("not the session of the console", StringComparison.Ordinal)),
            "the refusal in the log");
        Assert.False(pause.IsPaused);
        Assert.True(shell.Last!.CanPause);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_request_without_a_connection_to_the_agent_is_not_sent()
    {
        using var h = new SessionHarness();
        SessionHostClient? client = null;
        using RunningHost host = h.StartHost(beforeStart: created => client = created);

        // Nothing listens on the pipe: the harness was never started.
        bool sent = await client!.RequestPauseAsync(true);

        Assert.False(sent);
    }

    private sealed class FakeTrayStatusSource : ITrayStatusSource
    {
        private TrayStatus _status;

        public FakeTrayStatusSource(TrayStatus status)
        {
            _status = status;
        }

        public TrayStatus Status
        {
            get => Volatile.Read(ref _status);
            set => Volatile.Write(ref _status, value);
        }

        public ValueTask<TrayStatus> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Status);
    }

    private sealed class FakeTrayShell : ITrayShell
    {
        private TrayView? _last;

        public event EventHandler<TrayPauseRequestedEventArgs>? PauseRequested;

        public event EventHandler? OpenUiRequested;

        public TrayView? Last => Volatile.Read(ref _last);

        public Uri? Opened { get; private set; }

        public void Update(TrayView view)
        {
            Volatile.Write(ref _last, view);
        }

        public void OpenBrowser(Uri address)
        {
            Opened = address;
        }

        public void ChooseOpenUi()
        {
            OpenUiRequested?.Invoke(this, EventArgs.Empty);
        }

        public void ChoosePause(bool paused)
        {
            PauseRequested?.Invoke(this, new TrayPauseRequestedEventArgs(paused));
        }

        public void Dispose()
        {
        }
    }
}
