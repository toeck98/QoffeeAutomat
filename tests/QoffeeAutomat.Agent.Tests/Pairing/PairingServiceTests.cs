using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Pairing;

/// <summary>Token issuing, hashed storage, listing, revocation and the connection registry (T211).</summary>
[Trait("Category", "Pairing")]
public sealed class PairingServiceTests : IDisposable
{
    private static readonly PairClientInfo HomeAssistant = new("homeassistant", "Home");

    private readonly SimulatedClock _clock = new();
    private readonly TestSecrets _secrets = new();

    public void Dispose() => _secrets.Dispose();

    [Fact]
    public async Task Pairing_with_the_right_code_issues_a_token_that_authenticates()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairingCode code = pairing.StartPairing();

        PairResult result = await pairing.PairAsync(code.Code, HomeAssistant, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(43, result.Token!.Length);
        LinkClient? client = await pairing.AuthenticateAsync(result.Token, CancellationToken.None);
        Assert.NotNull(client);
        Assert.Equal(result.Client!.Id, client.Id);
        Assert.Equal("Home", client.Name);
        Assert.Equal("homeassistant", client.Kind);
        Assert.Equal(_clock.GetUtcNow(), client.CreatedAt);
        Assert.Null(client.LastSeenAt);
    }

    [Fact]
    public async Task A_wrong_code_issues_no_token()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairingCode code = pairing.StartPairing();
        string wrong = string.Equals(code.Code, "000000", StringComparison.Ordinal) ? "000001" : "000000";

        PairResult result = await pairing.PairAsync(wrong, HomeAssistant, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(PairingAttempt.WrongCode, result.Attempt);
        Assert.Empty(await pairing.ListClientsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_code_pairs_only_one_client()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairingCode code = pairing.StartPairing();

        PairResult first = await pairing.PairAsync(code.Code, HomeAssistant, CancellationToken.None);
        PairResult second = await pairing.PairAsync(code.Code, new PairClientInfo("tool", "Other"), CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Single(await pairing.ListClientsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tokens_are_stored_as_hashes_and_survive_a_restart()
    {
        string token;
        string clientId;
        using (LinkTokenStore tokens = new(_secrets.Open(), _clock))
        {
            IssuedLinkToken issued = await tokens.IssueAsync("Home", "homeassistant", CancellationToken.None);
            token = issued.Token;
            clientId = issued.Client.Id;
        }

        // A new store over the same files is a restart of the agent.
        SecretStore reopened = _secrets.Open();
        string stored = (await reopened.GetStringAsync(SecretNames.LinkTokenHashes, CancellationToken.None))!;
        Assert.DoesNotContain(token, stored, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored, StringComparison.Ordinal);

        using var restarted = new LinkTokenStore(reopened, _clock);
        LinkClient? client = await restarted.ValidateAsync(token, CancellationToken.None);
        Assert.Equal(clientId, client?.Id);
    }

    [Fact]
    public async Task Unknown_and_malformed_tokens_are_rejected()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        await tokens.IssueAsync("Home", "homeassistant", CancellationToken.None);

        Assert.Null(await tokens.ValidateAsync(null, CancellationToken.None));
        Assert.Null(await tokens.ValidateAsync(string.Empty, CancellationToken.None));
        Assert.Null(await tokens.ValidateAsync("not-a-token", CancellationToken.None));
        Assert.Null(await tokens.ValidateAsync(new string('x', 5000), CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_token_is_rejected()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairResult result = await PairAsync(pairing);
        string clientId = result.Client!.Id;

        Assert.True(await pairing.RevokeAsync(clientId, CancellationToken.None));

        Assert.Null(await pairing.AuthenticateAsync(result.Token, CancellationToken.None));
        Assert.Empty(await pairing.ListClientsAsync(CancellationToken.None));
        Assert.False(await pairing.RevokeAsync(clientId, CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_token_stays_revoked_after_a_restart()
    {
        string token;
        using (LinkTokenStore tokens = new(_secrets.Open(), _clock))
        {
            PairingService pairing = CreatePairing(tokens);
            PairResult result = await PairAsync(pairing);
            token = result.Token!;
            await pairing.RevokeAsync(result.Client!.Id, CancellationToken.None);
        }

        using var restarted = new LinkTokenStore(_secrets.Open(), _clock);

        Assert.Null(await restarted.ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_a_token_closes_its_open_connections_with_4401()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairResult result = await PairAsync(pairing);
        PairResult other = await PairAsync(pairing);
        LinkClient client = result.Client!;

        var first = new FakeLinkConnection();
        var second = new FakeLinkConnection();
        var unrelated = new FakeLinkConnection();
        IDisposable? firstRegistration = await pairing.RegisterConnectionAsync(client, first, CancellationToken.None);
        IDisposable? secondRegistration = await pairing.RegisterConnectionAsync(client, second, CancellationToken.None);
        IDisposable? unrelatedRegistration = await pairing.RegisterConnectionAsync(other.Client!, unrelated, CancellationToken.None);
        Assert.NotNull(firstRegistration);
        Assert.NotNull(secondRegistration);
        Assert.NotNull(unrelatedRegistration);
        Assert.Equal(2, pairing.Connections.Count(client.Id));

        await pairing.RevokeAsync(client.Id, CancellationToken.None);

        Assert.Equal(CloseCodes.Unauthorized, first.CloseCode);
        Assert.Equal(CloseCodes.Unauthorized, second.CloseCode);
        Assert.Equal(4401, first.CloseCode);
        Assert.False(unrelated.IsClosed);
        Assert.Equal(0, pairing.Connections.Count(client.Id));
        Assert.Equal(1, pairing.Connections.TotalCount);
    }

    [Fact]
    public async Task A_connection_cannot_register_for_a_client_that_was_revoked()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairResult result = await PairAsync(pairing);
        LinkClient client = result.Client!;
        await pairing.RevokeAsync(client.Id, CancellationToken.None);

        var connection = new FakeLinkConnection();
        IDisposable? registration = await pairing.RegisterConnectionAsync(client, connection, CancellationToken.None);

        Assert.Null(registration);
        Assert.Equal(0, pairing.Connections.Count(client.Id));
    }

    [Fact]
    public async Task Disposing_a_registration_removes_the_connection()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairResult result = await PairAsync(pairing);
        var connection = new FakeLinkConnection();
        IDisposable registration = (await pairing.RegisterConnectionAsync(result.Client!, connection, CancellationToken.None))!;

        registration.Dispose();
        registration.Dispose();
        await pairing.RevokeAsync(result.Client!.Id, CancellationToken.None);

        Assert.Equal(0, pairing.Connections.TotalCount);
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task Registering_records_the_last_seen_time()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairResult result = await PairAsync(pairing);
        _clock.Advance(TimeSpan.FromHours(1));

        await pairing.RegisterConnectionAsync(result.Client!, new FakeLinkConnection(), CancellationToken.None);

        LinkClient listed = Assert.Single(await pairing.ListClientsAsync(CancellationToken.None));
        Assert.Equal(_clock.GetUtcNow(), listed.LastSeenAt);
        Assert.True(listed.CreatedAt < listed.LastSeenAt);
    }

    [Fact]
    public async Task Several_clients_can_be_paired_and_revoked_one_by_one()
    {
        using LinkTokenStore tokens = new(_secrets.Open(), _clock);
        PairingService pairing = CreatePairing(tokens);
        PairResult first = await PairAsync(pairing);
        _clock.Advance(TimeSpan.FromMinutes(1));
        PairResult second = await PairAsync(pairing);

        IReadOnlyList<LinkClient> clients = await pairing.ListClientsAsync(CancellationToken.None);
        Assert.Equal([first.Client!.Id, second.Client!.Id], clients.Select(client => client.Id));

        await pairing.RevokeAsync(first.Client.Id, CancellationToken.None);

        Assert.Null(await pairing.AuthenticateAsync(first.Token, CancellationToken.None));
        Assert.NotNull(await pairing.AuthenticateAsync(second.Token, CancellationToken.None));
    }

    [Theory]
    [InlineData("homeassistant", "Home", true)]
    [InlineData("hub", "Hub", true)]
    [InlineData("tool", "CLI", true)]
    [InlineData("robot", "Home", false)]
    [InlineData("homeassistant", "", false)]
    [InlineData("homeassistant", "   ", false)]
    [InlineData("homeassistant", "bad\nname", false)]
    public void Client_data_is_validated(string kind, string name, bool valid)
    {
        string? problem = PairingService.ValidateClient(new PairClientInfo(kind, name));

        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void A_name_longer_than_100_characters_is_invalid()
    {
        Assert.NotNull(PairingService.ValidateClient(new PairClientInfo("tool", new string('n', 101))));
        Assert.Null(PairingService.ValidateClient(new PairClientInfo("tool", new string('n', 100))));
    }

    private PairingService CreatePairing(LinkTokenStore tokens)
    {
        return new PairingService(new PairingCodeManager(_clock), tokens, new LinkConnectionRegistry(), new NoCertificateSource());
    }

    private static async Task<PairResult> PairAsync(PairingService pairing)
    {
        PairingCode code = pairing.StartPairing();
        PairResult result = await pairing.PairAsync(code.Code, HomeAssistant, CancellationToken.None);
        Assert.True(result.Succeeded);
        return result;
    }
}
