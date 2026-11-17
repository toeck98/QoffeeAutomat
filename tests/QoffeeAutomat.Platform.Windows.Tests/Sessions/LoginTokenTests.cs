using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.SessionHost.Ipc;
using QoffeeAutomat.SessionHost.Tray;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// One-click UI login from the tray (PLAN.md S09 item 12, T711): an administrator in the console session gets a single-use token
/// over the pipe and the browser opens with it; everybody else gets the normal login page and never a token. The decision is the
/// agent's: what the SessionHost claims about itself does not matter.
/// </summary>
[Trait("Category", "OneTimeLogin")]
public sealed class LoginTokenTests
{
    private const string UiAddress = "http://127.0.0.1:8480/";

    [Fact]
    public void The_login_address_is_the_ui_address_plus_the_escaped_token()
    {
        Uri address = TrayPresenter.BuildLoginUri(new Uri(UiAddress), "ab-_c+/=");

        Assert.Equal("http://127.0.0.1:8480/api/v1/auth/one-time?token=ab-_c%2B%2F%3D", address.AbsoluteUri);
        Assert.True(address.IsLoopback);
    }

    [Fact]
    public async Task An_administrator_in_the_console_session_gets_a_token()
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = ElevatedTokenStatus.Available };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        h.StartHost();
        await h.NextSessionReadyAsync();
        SessionConnection connection = h.Bridge.ActiveConnection!;
        var service = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);

        SessionLoginTokenMessage answer = service.Decide(connection, "r1");

        Assert.Equal("r1", answer.RequestId);
        Assert.Equal(FakeIssuer.Token, answer.Token);
        Assert.Null(answer.Reason);
        Assert.Equal([SessionHarness.SessionId], native.OpenedSessions);
        Assert.True(native.Tokens.All(token => token.IsDisposed));
    }

    [Fact]
    public async Task A_standard_user_never_gets_a_token_even_when_the_helper_claims_to_be_an_administrator()
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = ElevatedTokenStatus.NoLinkedToken };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        h.StartHost();
        await h.NextSessionReadyAsync();
        SessionConnection connection = h.Bridge.ActiveConnection!;
        var service = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);

        SessionLoginTokenMessage answer = service.Decide(connection, "r2");

        Assert.Null(answer.Token);
        Assert.Equal(SessionLoginDenials.NotAdministrator, answer.Reason);
        Assert.Equal(0, issuer.Issued);
    }

    [Theory]
    [InlineData(ElevatedTokenStatus.NoSession)]
    [InlineData(ElevatedTokenStatus.Failed)]
    public async Task When_the_user_cannot_be_determined_no_token_is_issued(ElevatedTokenStatus status)
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = status };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        h.StartHost();
        await h.NextSessionReadyAsync();
        SessionConnection connection = h.Bridge.ActiveConnection!;
        var service = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);

        SessionLoginTokenMessage answer = service.Decide(connection, "r3");

        Assert.Null(answer.Token);
        Assert.Equal(SessionLoginDenials.Unavailable, answer.Reason);
        Assert.Equal(0, issuer.Issued);
    }

    [Fact]
    public async Task A_session_that_is_not_the_console_gets_no_token_and_the_user_is_not_even_looked_up()
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = ElevatedTokenStatus.Available };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        h.StartHost();
        await h.NextSessionReadyAsync();
        SessionConnection connection = h.Bridge.ActiveConnection!;
        var service = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);

        // Another session became the console: this one is a background session now.
        h.Lookup.Console = 99;
        SessionLoginTokenMessage answer = service.Decide(connection, "r4");

        Assert.Null(answer.Token);
        Assert.Equal(SessionLoginDenials.NotConsole, answer.Reason);
        Assert.Empty(native.OpenedSessions);
        Assert.Equal(0, issuer.Issued);
    }

    [Fact]
    public async Task Open_ui_for_an_administrator_opens_the_browser_with_the_token()
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = ElevatedTokenStatus.Available };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        var login = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);
        await login.StartAsync(CancellationToken.None);
        using var trayState = StartTrayState(h);
        await trayState.StartAsync(CancellationToken.None);
        using var shell = new RecordingShell();
        TrayCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new TrayCoordinator(client, shell, isAdministrator: true));
        using TrayCoordinator tray = coordinator!;
        await TestWait.UntilAsync(() => shell.Last is { CanOpenUi: true }, "an icon that can open the UI");

        shell.ChooseOpenUi();

        await TestWait.UntilAsync(() => shell.Opened is not null, "the browser");
        Assert.Equal(UiAddress + "api/v1/auth/one-time?token=" + FakeIssuer.Token, shell.Opened!.AbsoluteUri);
        Assert.Equal(1, issuer.Issued);

        await login.StopAsync(CancellationToken.None);
        await trayState.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Open_ui_for_a_standard_user_opens_the_plain_login_page_even_when_the_helper_asks_anyway()
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = ElevatedTokenStatus.NoLinkedToken };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        var login = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);
        await login.StartAsync(CancellationToken.None);
        using var trayState = StartTrayState(h);
        await trayState.StartAsync(CancellationToken.None);
        using var shell = new RecordingShell();
        TrayCoordinator? coordinator = null;

        // A tampered helper that claims to be an administrator: it asks, the agent refuses.
        h.StartHost(beforeStart: client => coordinator = new TrayCoordinator(client, shell, isAdministrator: true));
        using TrayCoordinator tray = coordinator!;
        await TestWait.UntilAsync(() => shell.Last is { CanOpenUi: true }, "an icon that can open the UI");

        shell.ChooseOpenUi();

        await TestWait.UntilAsync(() => shell.Opened is not null, "the browser");
        Assert.Equal(UiAddress, shell.Opened!.AbsoluteUri);
        Assert.Equal(0, issuer.Issued);

        await login.StopAsync(CancellationToken.None);
        await trayState.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_helper_that_knows_it_is_a_standard_user_does_not_even_ask()
    {
        using var h = new SessionHarness();
        var native = new FakeElevatedNativeApi { TokenStatus = ElevatedTokenStatus.Available };
        var issuer = new FakeIssuer();
        await h.StartAsync();
        var login = new LoginTokenService(h.Manager, () => h.Bridge.ActiveConnection, native, issuer, h.Logger);
        await login.StartAsync(CancellationToken.None);
        using var trayState = StartTrayState(h);
        await trayState.StartAsync(CancellationToken.None);
        using var shell = new RecordingShell();
        TrayCoordinator? coordinator = null;
        h.StartHost(beforeStart: client => coordinator = new TrayCoordinator(client, shell, isAdministrator: false));
        using TrayCoordinator tray = coordinator!;
        await TestWait.UntilAsync(() => shell.Last is { CanOpenUi: true }, "an icon that can open the UI");

        shell.ChooseOpenUi();

        await TestWait.UntilAsync(() => shell.Opened is not null, "the browser");
        Assert.Equal(UiAddress, shell.Opened!.AbsoluteUri);
        Assert.Empty(native.OpenedSessions);
        Assert.Equal(0, issuer.Issued);

        await login.StopAsync(CancellationToken.None);
        await trayState.StopAsync(CancellationToken.None);
    }

    private static TrayStateService StartTrayState(SessionHarness h)
    {
        var source = new FixedTrayStatus(new TrayStatus("Gaming-PC", 1, 1, true, UiAddress));
        return new TrayStateService(h.Manager, h.Bridge, new AgentPause(), source, TimeProvider.System, h.Logger, TimeSpan.FromMilliseconds(50));
    }

    private sealed class FakeIssuer : ILoginTokenIssuer
    {
        public const string Token = "test-token_0123456789";

        private int _issued;

        public int Issued => Volatile.Read(ref _issued);

        public string Issue()
        {
            Interlocked.Increment(ref _issued);
            return Token;
        }
    }

    private sealed class FixedTrayStatus : ITrayStatusSource
    {
        private readonly TrayStatus _status;

        public FixedTrayStatus(TrayStatus status)
        {
            _status = status;
        }

        public ValueTask<TrayStatus> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_status);
    }

    private sealed class RecordingShell : ITrayShell
    {
        private TrayView? _last;
        private Uri? _opened;

        public event EventHandler<TrayPauseRequestedEventArgs>? PauseRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? OpenUiRequested;

        public TrayView? Last => Volatile.Read(ref _last);

        public Uri? Opened => Volatile.Read(ref _opened);

        public void Update(TrayView view) => Volatile.Write(ref _last, view);

        public void OpenBrowser(Uri address) => Volatile.Write(ref _opened, address);

        public void ChooseOpenUi() => OpenUiRequested?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }
}
