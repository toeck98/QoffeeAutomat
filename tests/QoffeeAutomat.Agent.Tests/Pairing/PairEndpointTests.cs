using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Tests.Pairing;

/// <summary><c>POST /link/v1/pair</c> and the pairing management endpoints, through the whole agent (T211).</summary>
[Trait("Category", "Pairing")]
public sealed class PairEndpointTests : IDisposable
{
    private const string PairBody = "{\"code\":\"{CODE}\",\"client\":{\"kind\":\"homeassistant\",\"name\":\"Home\"}}";

    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task A_pairing_started_in_the_ui_pairs_a_client_and_the_token_authenticates()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();
        using HttpClient link = agent.CreateApiClient(https: true);
        string cookie = await agent.SetUpAsync(api);

        // Start pairing as the signed-in user: code, expiry and the certificate fingerprint.
        using HttpResponseMessage started = await TestAgent.SendAsync(api, HttpMethod.Post, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using JsonDocument info = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        string code = info.RootElement.GetProperty("code").GetString()!;
        string fingerprint = info.RootElement.GetProperty("fingerprint").GetString()!;
        Assert.Equal(6, code.Length);
        Assert.Equal(agent.Services.GetRequiredService<PairingService>().Fingerprint, fingerprint);
        Assert.True(info.RootElement.TryGetProperty("expiresAt", out _));

        // The client pairs over HTTPS without any bearer token.
        using HttpResponseMessage paired = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: PairBody.Replace("{CODE}", code, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        Assert.Equal("no-store", paired.Headers.CacheControl?.ToString());
        using JsonDocument response = JsonDocument.Parse(await paired.Content.ReadAsStringAsync());
        string token = response.RootElement.GetProperty("token").GetString()!;
        Assert.Equal(43, token.Length);
        JsonElement agentInfo = response.RootElement.GetProperty("agent");
        Assert.False(string.IsNullOrEmpty(agentInfo.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(agentInfo.GetProperty("name").GetString()));

        var pairing = agent.Services.GetRequiredService<PairingService>();
        Assert.NotNull(await pairing.AuthenticateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task The_client_list_shows_the_client_and_revoking_it_rejects_its_token_and_closes_its_connection()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();
        using HttpClient link = agent.CreateApiClient(https: true);
        string cookie = await agent.SetUpAsync(api);
        var pairing = agent.Services.GetRequiredService<PairingService>();
        string token = await PairAsync(api, link, cookie);

        using HttpResponseMessage listed = await TestAgent.SendAsync(api, HttpMethod.Get, "/api/v1/link/clients", cookie);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using JsonDocument list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        JsonElement client = Assert.Single(list.RootElement.GetProperty("clients").EnumerateArray());
        string id = client.GetProperty("id").GetString()!;
        Assert.Equal("Home", client.GetProperty("name").GetString());
        Assert.Equal("homeassistant", client.GetProperty("kind").GetString());
        Assert.Equal(0, client.GetProperty("connections").GetInt32());
        Assert.DoesNotContain(token, client.ToString(), StringComparison.Ordinal);

        var connection = new FakeLinkConnection();
        LinkClient known = (await pairing.AuthenticateAsync(token, CancellationToken.None))!;
        Assert.NotNull(await pairing.RegisterConnectionAsync(known, connection, CancellationToken.None));

        using HttpResponseMessage revoked = await TestAgent.SendAsync(api, HttpMethod.Delete, "/api/v1/link/clients/" + id, cookie);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Null(await pairing.AuthenticateAsync(token, CancellationToken.None));
        Assert.Equal(4401, connection.CloseCode);
        using HttpResponseMessage again = await TestAgent.SendAsync(api, HttpMethod.Delete, "/api/v1/link/clients/" + id, cookie);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_is_unauthorized_and_five_wrong_attempts_invalidate_the_code()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();
        using HttpClient link = agent.CreateApiClient(https: true);
        string cookie = await agent.SetUpAsync(api);
        string code = await StartPairingAsync(api, cookie);
        string wrong = string.Equals(code, "000000", StringComparison.Ordinal) ? "000001" : "000000";

        for (int attempt = 0; attempt < PairingCodeManager.MaxFailedAttempts; attempt++)
        {
            using HttpResponseMessage refused = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: PairBody.Replace("{CODE}", wrong, StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("unauthorized", problem.RootElement.GetProperty("code").GetString());
        }

        // The right code does not work any more.
        using HttpResponseMessage late = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: PairBody.Replace("{CODE}", code, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
        var pairing = agent.Services.GetRequiredService<PairingService>();
        Assert.Empty(await pairing.ListClientsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_code_pairs_only_once_over_http()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();
        using HttpClient link = agent.CreateApiClient(https: true);
        string cookie = await agent.SetUpAsync(api);
        string code = await StartPairingAsync(api, cookie);
        string body = PairBody.Replace("{CODE}", code, StringComparison.Ordinal);

        using HttpResponseMessage first = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: body);
        using HttpResponseMessage second = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Pairing_is_refused_over_plain_http()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(api);
        string code = await StartPairingAsync(api, cookie);

        using HttpResponseMessage response = await TestAgent.SendAsync(api, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: PairBody.Replace("{CODE}", code, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var pairing = agent.Services.GetRequiredService<PairingService>();
        Assert.NotNull(pairing.ActivePairing());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"code\":\"123456\"}")]
    [InlineData("{\"code\":\"123456\",\"client\":{\"kind\":\"robot\",\"name\":\"Home\"}}")]
    [InlineData("{\"code\":\"123456\",\"client\":{\"kind\":\"tool\",\"name\":\"\"}}")]
    public async Task A_malformed_pairing_request_is_a_bad_request(string body)
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient link = agent.CreateApiClient(https: true);

        using HttpResponseMessage response = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_pairing_request_is_rejected()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient link = agent.CreateApiClient(https: true);
        string body = "{\"code\":\"123456\",\"client\":{\"kind\":\"tool\",\"name\":\"" + new string('n', 8000) + "\"}}";

        using HttpResponseMessage response = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Starting_listing_and_revoking_need_a_session()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();

        foreach ((HttpMethod method, string path) in new[]
        {
            (HttpMethod.Post, "/api/v1/link/pairing"),
            (HttpMethod.Get, "/api/v1/link/pairing"),
            (HttpMethod.Delete, "/api/v1/link/pairing"),
            (HttpMethod.Get, "/api/v1/link/clients"),
            (HttpMethod.Delete, "/api/v1/link/clients/abc"),
        })
        {
            using HttpResponseMessage response = await TestAgent.SendAsync(api, method, path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_active_pairing_can_be_read_and_cancelled()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient api = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(api);

        using HttpResponseMessage none = await TestAgent.SendAsync(api, HttpMethod.Get, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);

        string code = await StartPairingAsync(api, cookie);
        using HttpResponseMessage active = await TestAgent.SendAsync(api, HttpMethod.Get, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        using JsonDocument info = JsonDocument.Parse(await active.Content.ReadAsStringAsync());
        Assert.Equal(code, info.RootElement.GetProperty("code").GetString());

        using HttpResponseMessage cancelled = await TestAgent.SendAsync(api, HttpMethod.Delete, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using HttpResponseMessage gone = await TestAgent.SendAsync(api, HttpMethod.Get, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
    }

    private static async Task<string> StartPairingAsync(HttpClient api, string cookie)
    {
        using HttpResponseMessage started = await TestAgent.SendAsync(api, HttpMethod.Post, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using JsonDocument info = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        return info.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<string> PairAsync(HttpClient api, HttpClient link, string cookie)
    {
        string code = await StartPairingAsync(api, cookie);
        using HttpResponseMessage paired = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: PairBody.Replace("{CODE}", code, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        using JsonDocument response = JsonDocument.Parse(await paired.Content.ReadAsStringAsync());
        return response.RootElement.GetProperty("token").GetString()!;
    }
}
