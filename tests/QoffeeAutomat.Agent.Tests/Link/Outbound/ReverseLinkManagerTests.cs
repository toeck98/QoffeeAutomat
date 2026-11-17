using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>Joining and leaving the reverse link, and where its pieces are kept: the URL in the settings, the token in the secret store (ADR 0003, S09).</summary>
[Trait("Category", "OutboundLink")]
public sealed class ReverseLinkManagerTests
{
    private static readonly string Fingerprint = string.Concat(Enumerable.Repeat("A1B2C3D4E5F60718293A4B5C6D7E8F90", 2));

    [Fact]
    public async Task Joining_stores_the_token_in_the_secret_store_and_the_url_in_the_settings()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        ISecretStore secrets = host.Agent.Services.GetRequiredService<ISecretStore>();
        string token = NewToken();
        int changed = 0;
        manager.Changed += (_, _) => changed++;

        ReverseLinkJoinResult joined = await manager.JoinAsync(Enrollment(token, Fingerprint), CancellationToken.None);

        Assert.True(joined.Joined, joined.Error);
        Assert.Equal(1, changed);
        Assert.Equal(token, await secrets.GetStringAsync(SecretNames.ReverseLinkToken, CancellationToken.None));
        ReverseLinkSettings settings = host.Config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)!.ReverseLink!;
        Assert.True(settings.Enabled);
        Assert.Equal("wss://ha.example.org/api/qoffeeautomat/reverse/v1", settings.Url);
        Assert.Equal(Fingerprint, settings.Fingerprint);
        ReverseLinkConfiguration configuration = await manager.GetAsync(CancellationToken.None);
        Assert.Equal(new ReverseLinkConfiguration(true, settings.Url, true, true), configuration);
    }

    [Fact]
    public async Task The_token_is_not_part_of_the_settings_document_or_of_what_the_manager_reports()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        string token = NewToken();
        await manager.JoinAsync(Enrollment(token, null), CancellationToken.None);

        AgentSettingsDocument document = host.Config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)!;
        string json = JsonSerializer.Serialize(document, ConfigJsonContext.Default.AgentSettingsDocument);
        ReverseLinkConfiguration configuration = await manager.GetAsync(CancellationToken.None);

        Assert.DoesNotContain(token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(token, configuration.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_endpoint_combines_the_settings_with_the_stored_token()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        string token = NewToken();
        await manager.JoinAsync(Enrollment(token, Fingerprint), CancellationToken.None);

        OutboundEndpoint? endpoint = await manager.ResolveAsync(CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal("wss://ha.example.org/api/qoffeeautomat/reverse/v1", endpoint.Url.AbsoluteUri);
        Assert.Equal(token, endpoint.Credential);
        Assert.Equal(Fingerprint, endpoint.Fingerprint);
    }

    [Fact]
    public async Task A_string_that_is_not_valid_changes_nothing()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        ISecretStore secrets = host.Agent.Services.GetRequiredService<ISecretStore>();
        int changed = 0;
        manager.Changed += (_, _) => changed++;

        ReverseLinkJoinResult joined = await manager.JoinAsync("qalr1.not-a-valid-string", CancellationToken.None);

        Assert.False(joined.Joined);
        Assert.False(string.IsNullOrEmpty(joined.Error));
        Assert.Equal(0, changed);
        Assert.Null(await secrets.GetStringAsync(SecretNames.ReverseLinkToken, CancellationToken.None));
        Assert.Null(await manager.ResolveAsync(CancellationToken.None));
        Assert.Equal(new ReverseLinkConfiguration(false, null, false, false), await manager.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Leaving_deletes_the_token_and_turns_the_link_off_but_keeps_the_url()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        ISecretStore secrets = host.Agent.Services.GetRequiredService<ISecretStore>();
        await manager.JoinAsync(Enrollment(NewToken(), null), CancellationToken.None);
        int changed = 0;
        manager.Changed += (_, _) => changed++;

        bool left = await manager.LeaveAsync(CancellationToken.None);

        Assert.True(left);
        Assert.Equal(1, changed);
        Assert.Null(await secrets.GetStringAsync(SecretNames.ReverseLinkToken, CancellationToken.None));
        Assert.Null(await manager.ResolveAsync(CancellationToken.None));
        ReverseLinkConfiguration configuration = await manager.GetAsync(CancellationToken.None);
        Assert.False(configuration.Enabled);
        Assert.False(configuration.HasToken);
        Assert.Equal("wss://ha.example.org/api/qoffeeautomat/reverse/v1", configuration.Url);
        Assert.False(await manager.LeaveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Joining_again_replaces_the_token()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        await manager.JoinAsync(Enrollment(NewToken(), null), CancellationToken.None);
        string second = NewToken();

        await manager.JoinAsync(Enrollment(second, null), CancellationToken.None);

        OutboundEndpoint? endpoint = await manager.ResolveAsync(CancellationToken.None);
        Assert.Equal(second, endpoint!.Credential);
    }

    [Fact]
    public async Task A_link_that_is_turned_off_in_the_settings_is_not_dialled_even_with_a_token()
    {
        await using var host = new LinkTestHost();
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        await manager.JoinAsync(Enrollment(NewToken(), null), CancellationToken.None);
        AgentSettingsDocument settings = host.Config.Local.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)!;

        SaveResult<AgentSettingsDocument> saved = await host.Config.Local.SaveAsync(
            settings with { ReverseLink = settings.ReverseLink! with { Enabled = false } },
            settings.Revision,
            CancellationToken.None);

        Assert.True(saved.IsApplied);
        Assert.Null(await manager.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public void The_settings_validator_checks_the_url_and_the_fingerprint_of_the_reverse_link()
    {
        AgentSettingsDocument valid = new(DocumentSchema.CurrentVersion, "Laptop", ReverseLink: new ReverseLinkSettings(true, "wss://ha.example.org/x", Fingerprint));

        Assert.True(DocumentValidator.ValidateSettings(valid).IsValid);
        Assert.False(DocumentValidator.ValidateSettings(valid with { ReverseLink = new ReverseLinkSettings(true, "ws://ha.example.org/x") }).IsValid);
        Assert.False(DocumentValidator.ValidateSettings(valid with { ReverseLink = new ReverseLinkSettings(true, "https://ha.example.org/x") }).IsValid);
        Assert.False(DocumentValidator.ValidateSettings(valid with { ReverseLink = new ReverseLinkSettings(true, null) }).IsValid);
        Assert.False(DocumentValidator.ValidateSettings(valid with { ReverseLink = new ReverseLinkSettings(true, "wss://ha.example.org/x", "12:34") }).IsValid);
        Assert.True(DocumentValidator.ValidateSettings(valid with { ReverseLink = new ReverseLinkSettings(false, null) }).IsValid);
        Assert.True(DocumentValidator.ValidateSettings(valid with { ReverseLink = new ReverseLinkSettings(true, "ws://localhost:8123/x") }).IsValid);
    }

    private static string NewToken() => ReverseLinkEnrollment.CreateToken(RandomNumberGenerator.GetBytes(32));

    private static string Enrollment(string token, string? fingerprint)
    {
        return new ReverseLinkEnrollment(new Uri("wss://ha.example.org/api/qoffeeautomat/reverse/v1"), token, fingerprint).ToEnrollmentString();
    }
}
