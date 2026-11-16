using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>First-run setup, sign-in, sessions, anti-forgery protection and password change through the whole agent (T212).</summary>
[Trait("Category", "Auth")]
public sealed class AuthEndpointTests : IDisposable
{
    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task The_agent_writes_a_setup_token_on_first_start_and_setup_is_available()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage state = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/setup");

        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Contains("\"available\":true", await state.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(File.Exists(agent.SetupTokenPath));
        Assert.Equal(32, agent.ReadSetupToken().Length);
    }

    [Fact]
    public async Task Setup_is_unavailable_once_a_password_exists()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string token = agent.ReadSetupToken();
        await agent.SetUpAsync(client);

        using HttpResponseMessage state = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/setup");
        using HttpResponseMessage again = await TestAgent.SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/auth/setup",
            json: "{\"setupToken\":\"" + token + "\",\"password\":\"another password, also long\"}");

        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Contains("setup_unavailable", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(agent.SetupTokenPath));

        // The first password is still the one that works.
        Assert.NotEqual(HttpStatusCode.OK, await LoginStatusAsync(client, "another password, also long"));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(client, TestAgent.StrongPassword));
    }

    [Fact]
    public async Task Setup_needs_the_right_token_and_an_acceptable_password()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string token = agent.ReadSetupToken();

        using HttpResponseMessage wrongToken = await TestAgent.SendAsync(
            client, HttpMethod.Post, "/api/v1/auth/setup", json: "{\"setupToken\":\"wrong\",\"password\":\"" + TestAgent.StrongPassword + "\"}");
        using HttpResponseMessage shortPassword = await TestAgent.SendAsync(
            client, HttpMethod.Post, "/api/v1/auth/setup", json: "{\"setupToken\":\"" + token + "\",\"password\":\"short\"}");
        using HttpResponseMessage malformed = await TestAgent.SendAsync(
            client, HttpMethod.Post, "/api/v1/auth/setup", json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        Assert.Contains("invalid_setup_token", await wrongToken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Contains("weak_password", await shortPassword.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        // None of that set a password: setup still works with the right values.
        Assert.True(File.Exists(agent.SetupTokenPath));
        await agent.SetUpAsync(client);
    }

    [Fact]
    public async Task Setup_attempts_with_a_wrong_token_are_locked_out()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string body = "{\"setupToken\":\"wrong\",\"password\":\"" + TestAgent.StrongPassword + "\"}";

        for (int attempt = 1; attempt < 5; attempt++)
        {
            using HttpResponseMessage refused = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", json: body);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using HttpResponseMessage locked = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", json: body);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);

        // Even the right token is refused during the lockout.
        string right = "{\"setupToken\":\"" + agent.ReadSetupToken() + "\",\"password\":\"" + TestAgent.StrongPassword + "\"}";
        using HttpResponseMessage rightToken = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", json: right);
        Assert.Equal(HttpStatusCode.TooManyRequests, rightToken.StatusCode);
    }

    [Fact]
    public async Task Setup_signs_in_and_ends_earlier_sessions_and_the_token_is_gone()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        string cookie = await agent.SetUpAsync(client);

        using HttpResponseMessage me = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using JsonDocument profile = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(profile.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("administrator", profile.RootElement.GetProperty("name").GetString());
        Assert.False(File.Exists(agent.SetupTokenPath));
    }

    [Fact]
    public async Task Reset_password_makes_setup_available_again_and_a_new_password_replaces_the_old_one()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string oldCookie = await agent.SetUpAsync(client);
        Assert.False(File.Exists(agent.SetupTokenPath));

        // What "qoffeeautomat reset-password" does: a new token file next to the existing password.
        int exit = await ResetPasswordCommand.RunAsync(
            new AgentOptions(AgentVerb.ResetPassword, DataDir: _data.Path),
            TextWriter.Null,
            TextWriter.Null,
            () => true);
        Assert.Equal(0, exit);

        using HttpResponseMessage state = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/setup");
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);

        // The old password keeps working until the new one is set.
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(client, TestAgent.StrongPassword));

        const string newPassword = "a brand new password, long enough";
        string newCookie = await agent.SetUpAsync(client, newPassword);

        Assert.NotEqual(oldCookie, newCookie);
        Assert.False(File.Exists(agent.SetupTokenPath));
        Assert.NotEqual(HttpStatusCode.OK, await LoginStatusAsync(client, TestAgent.StrongPassword));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(client, newPassword));

        // Setting a password ends every session that existed before.
        using HttpResponseMessage old = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
    }

    [Fact]
    public async Task Login_with_the_right_password_gives_a_session_and_a_wrong_password_does_not()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        await agent.SetUpAsync(client);

        using HttpResponseMessage wrong = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"not the password\"}");
        string cookie = await TestAgent.LoginAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("invalid_credentials", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));
        using HttpResponseMessage me = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Login_before_setup_is_refused()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(client, TestAgent.StrongPassword));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_sign_in_out()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        await agent.SetUpAsync(client);

        for (int attempt = 1; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginStatusAsync(client, "wrong password " + attempt));
        }

        using HttpResponseMessage locked = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"one more wrong one\"}");
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains("locked_out", await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotNull(locked.Headers.RetryAfter);
        Assert.Equal(TimeSpan.FromMinutes(15), locked.Headers.RetryAfter!.Delta);

        // During the lockout even the right password is refused, and no session is created.
        using HttpResponseMessage right = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.False(right.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_lockout_of_one_address_does_not_lock_out_another()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        await agent.SetUpAsync(client);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage failed = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"wrong password\"}");
        }

        using HttpResponseMessage blocked = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");
        using HttpResponseMessage other = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", remoteIp: "127.0.0.2", json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task The_session_cookie_is_http_only_same_site_strict_and_secure_on_https()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient plain = agent.CreateApiClient();
        using HttpClient secure = agent.CreateApiClient(https: true);
        await agent.SetUpAsync(plain);

        using HttpResponseMessage overHttp = await TestAgent.SendAsync(plain, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");
        using HttpResponseMessage overHttps = await TestAgent.SendAsync(secure, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");

        string plainCookie = TestAgent.SetCookieHeader(overHttp);
        Assert.Contains("httponly", plainCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", plainCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", plainCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", plainCookie, StringComparison.OrdinalIgnoreCase);

        string secureCookie = TestAgent.SetCookieHeader(overHttps);
        Assert.Contains("httponly", secureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", secureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", secureCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", overHttps.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Logout_ends_the_session_and_clears_the_cookie()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(client);

        using HttpResponseMessage logout = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout", cookie);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=", TestAgent.SetCookieHeader(logout), StringComparison.OrdinalIgnoreCase);
        using HttpResponseMessage me = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Contains("unauthorized", await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", "/api/v1/auth/logout")]
    [InlineData("POST", "/api/v1/auth/password")]
    [InlineData("POST", "/api/v1/link/pairing")]
    [InlineData("DELETE", "/api/v1/link/pairing")]
    [InlineData("DELETE", "/api/v1/link/clients/abc")]
    public async Task State_changing_requests_without_the_anti_forgery_header_are_rejected(string method, string path)
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(client);

        using HttpResponseMessage rejected = await TestAgent.SendAsync(client, new HttpMethod(method), path, cookie, csrf: false, json: "{}");

        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Contains("csrf_header_missing", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The rejected request changed nothing: the session is still valid.
        using HttpResponseMessage me = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie, csrf: false);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Sign_in_and_setup_need_the_anti_forgery_header_too()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string token = agent.ReadSetupToken();

        using HttpResponseMessage setup = await TestAgent.SendAsync(
            client,
            HttpMethod.Post,
            "/api/v1/auth/setup",
            csrf: false,
            json: "{\"setupToken\":\"" + token + "\",\"password\":\"" + TestAgent.StrongPassword + "\"}");
        Assert.Equal(HttpStatusCode.Forbidden, setup.StatusCode);
        Assert.True(File.Exists(agent.SetupTokenPath));

        await agent.SetUpAsync(client);
        using HttpResponseMessage login = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", csrf: false, json: "{\"password\":\"" + TestAgent.StrongPassword + "\"}");
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task Reading_requests_do_not_need_the_anti_forgery_header_but_a_wrong_header_value_is_rejected()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(client);

        using HttpResponseMessage read = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie, csrf: false);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var wrongValue = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        wrongValue.Headers.Add("Cookie", cookie);
        wrongValue.Headers.Add(CsrfHeaderFilter.HeaderName, "0");
        using HttpResponseMessage rejected = await client.SendAsync(wrongValue);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
    }

    [Fact]
    public async Task A_request_from_another_origin_is_rejected_even_with_the_header()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(client);

        using HttpResponseMessage foreign = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout", cookie, origin: "http://evil.example");
        using HttpResponseMessage own = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout", cookie, origin: "http://localhost");

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Contains("cross_origin", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_keeps_this_session_and_ends_the_others()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string first = await agent.SetUpAsync(client);
        string second = await TestAgent.LoginAsync(client);
        const string newPassword = "a different password, long enough";

        using HttpResponseMessage wrongCurrent = await TestAgent.SendAsync(
            client, HttpMethod.Post, "/api/v1/auth/password", first, json: "{\"currentPassword\":\"nope\",\"newPassword\":\"" + newPassword + "\"}");
        using HttpResponseMessage weak = await TestAgent.SendAsync(
            client, HttpMethod.Post, "/api/v1/auth/password", first, json: "{\"currentPassword\":\"" + TestAgent.StrongPassword + "\",\"newPassword\":\"short\"}");
        Assert.Equal(HttpStatusCode.Forbidden, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        using HttpResponseMessage changed = await TestAgent.SendAsync(
            client, HttpMethod.Post, "/api/v1/auth/password", first, json: "{\"currentPassword\":\"" + TestAgent.StrongPassword + "\",\"newPassword\":\"" + newPassword + "\"}");
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using HttpResponseMessage stillIn = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", first);
        using HttpResponseMessage otherSession = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", second);
        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherSession.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, await LoginStatusAsync(client, TestAgent.StrongPassword));
        Assert.Equal(HttpStatusCode.OK, await LoginStatusAsync(client, newPassword));
    }

    [Fact]
    public async Task The_password_is_stored_as_a_hash_in_the_secret_store_and_never_in_plain_text()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        await agent.SetUpAsync(client);

        var secrets = agent.Services.GetRequiredService<ISecretStore>();
        string? hash = await secrets.GetStringAsync(SecretNames.PasswordHash, CancellationToken.None);

        Assert.NotNull(hash);
        Assert.StartsWith("pbkdf2-sha256$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain(TestAgent.StrongPassword, hash, StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(agent.SecretsDirectory))
        {
            Assert.DoesNotContain(TestAgent.StrongPassword, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }

    private static async Task<HttpStatusCode> LoginStatusAsync(HttpClient client, string password)
    {
        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"" + password + "\"}");
        return response.StatusCode;
    }
}
