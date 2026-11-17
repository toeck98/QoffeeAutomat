using System.Net;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>Single-use login tokens for the tray entry Open UI (T711): once, within 30 seconds, from loopback only.</summary>
[Trait("Category", "OneTimeLogin")]
public sealed class OneTimeLoginTests : IDisposable
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private readonly TempDataDirectory _data = new();
    private readonly SimulatedClock _clock = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void A_token_works_once()
    {
        var service = new OneTimeLoginService(_clock);
        string token = service.Issue();

        Assert.Equal(OneTimeLoginOutcome.Accepted, service.Redeem(token, Loopback));
        Assert.Equal(OneTimeLoginOutcome.Unknown, service.Redeem(token, Loopback));
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public void A_token_works_until_its_30_seconds_are_over_and_not_after()
    {
        var service = new OneTimeLoginService(_clock);
        string inTime = service.Issue();
        string late = service.Issue();

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(OneTimeLoginOutcome.Accepted, service.Redeem(inTime, Loopback));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(OneTimeLoginOutcome.Expired, service.Redeem(late, Loopback));

        // An expired token is gone, it does not come back to life.
        Assert.Equal(OneTimeLoginOutcome.Unknown, service.Redeem(late, Loopback));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.7")]
    [InlineData("2001:db8::1")]
    public void A_token_is_refused_from_an_address_that_is_not_loopback_and_is_spent_by_the_attempt(string address)
    {
        var service = new OneTimeLoginService(_clock);
        string token = service.Issue();

        Assert.Equal(OneTimeLoginOutcome.NotLoopback, service.Redeem(token, IPAddress.Parse(address)));

        // A token that was shown to another address is not trusted any more, not even from loopback.
        Assert.Equal(OneTimeLoginOutcome.Unknown, service.Redeem(token, Loopback));
    }

    [Fact]
    public void An_unknown_remote_address_never_counts_as_loopback()
    {
        var service = new OneTimeLoginService(_clock);

        Assert.Equal(OneTimeLoginOutcome.NotLoopback, service.Redeem(service.Issue(), remote: null));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Every_loopback_form_is_accepted(string address)
    {
        var service = new OneTimeLoginService(_clock);

        Assert.Equal(OneTimeLoginOutcome.Accepted, service.Redeem(service.Issue(), IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("guess")]
    public void An_unknown_token_is_refused(string? token)
    {
        var service = new OneTimeLoginService(_clock);
        service.Issue();

        Assert.Equal(OneTimeLoginOutcome.Unknown, service.Redeem(token, Loopback));
        Assert.Equal(OneTimeLoginOutcome.Unknown, service.Redeem(new string('a', 5000), Loopback));
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public void Tokens_are_long_random_and_different_every_time()
    {
        var service = new OneTimeLoginService(_clock);

        string first = service.Issue();
        string second = service.Issue();

        Assert.Equal(43, first.Length);
        Assert.NotEqual(first, second);
        Assert.Equal(OneTimeLoginOutcome.Accepted, service.Redeem(second, Loopback));
        Assert.Equal(OneTimeLoginOutcome.Accepted, service.Redeem(first, Loopback));
    }

    [Fact]
    public void Only_a_few_tokens_wait_at_once_and_the_oldest_gives_way()
    {
        var service = new OneTimeLoginService(_clock);
        string oldest = service.Issue();
        for (int index = 0; index < OneTimeLoginService.MaxPending; index++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(10));
            service.Issue();
        }

        Assert.Equal(OneTimeLoginService.MaxPending, service.PendingCount);
        Assert.Equal(OneTimeLoginOutcome.Unknown, service.Redeem(oldest, Loopback));
    }

    [Fact]
    public async Task The_login_link_starts_a_session_once_and_sends_the_browser_to_the_ui()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string token = agent.Services.GetRequiredService<ILoginTokenIssuer>().Issue();
        string path = "/api/v1/auth/one-time?token=" + Uri.EscapeDataString(token);

        using HttpResponseMessage first = await TestAgent.SendAsync(client, HttpMethod.Get, path, csrf: false, remoteIp: "127.0.0.1");

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/", first.Headers.Location?.OriginalString);
        Assert.True(first.Headers.CacheControl?.NoStore);
        Assert.Contains("no-referrer", first.Headers.GetValues("Referrer-Policy"));
        string setCookie = TestAgent.SetCookieHeader(first);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

        // The session is a real one: the management API accepts it.
        using HttpResponseMessage me = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie: TestAgent.CookieFrom(first), remoteIp: "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // The same link a second time: no session, no cookie.
        using HttpResponseMessage replay = await TestAgent.SendAsync(client, HttpMethod.Get, path, csrf: false, remoteIp: "127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("invalid_login_token", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(replay.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_login_link_does_not_work_from_the_network_and_the_attempt_spends_the_token()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string token = agent.Services.GetRequiredService<ILoginTokenIssuer>().Issue();
        string path = "/api/v1/auth/one-time?token=" + Uri.EscapeDataString(token);

        // Remote UI is off, so the gate already says 403; with it on, the token itself is bound to loopback (unit tests above).
        using HttpResponseMessage remote = await TestAgent.SendAsync(client, HttpMethod.Get, path, csrf: false, remoteIp: "192.168.1.20");
        Assert.NotEqual(HttpStatusCode.Redirect, remote.StatusCode);
        Assert.False(remote.Headers.Contains("Set-Cookie"));

        // No remote address at all is not loopback either.
        string other = agent.Services.GetRequiredService<ILoginTokenIssuer>().Issue();
        using HttpResponseMessage unknown = await TestAgent.SendAsync(
            client, HttpMethod.Get, "/api/v1/auth/one-time?token=" + Uri.EscapeDataString(other), csrf: false);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.False(unknown.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_missing_or_wrong_token_gives_no_session()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage missing = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/one-time", csrf: false, remoteIp: "127.0.0.1");
        using HttpResponseMessage wrong = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/one-time?token=nope", csrf: false, remoteIp: "127.0.0.1");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(missing.Headers.Contains("Set-Cookie"));
        Assert.False(wrong.Headers.Contains("Set-Cookie"));
    }
}
