using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tests.Enrollment;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Fleet;

/// <summary>
/// Pairing of Home Assistant with the hub and the management of fleet tokens (T913, PLAN.md S07, S20 "Fleet link", "Hub security"): the code flow of <c>POST /hub/v1/pair</c>, the scope
/// of a token (all agents or a set of tags), listing and revoking tokens in the management API, and the checks of the fleet link's upgrade request.
/// </summary>
[Trait("Category", "Fleet")]
public sealed class FleetPairingTests
{
    private static string WrongCodeFor(string code) => code == "000000" ? "000001" : "000000";

    // ------------------------------------------------------------------
    // The code flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_pairing_code_turns_into_a_token_that_names_the_hub_the_scope_and_the_fingerprint()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        JsonNode pairing = await rig.CreatePairingAsync();
        string code = pairing["code"]!.GetValue<string>();

        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal("all", pairing["scope"]!["type"]!.GetValue<string>());
        Assert.Equal(rig.Hub.Clock.GetUtcNow() + TimeSpan.FromMinutes(5), DateTimeOffset.Parse(pairing["expiresAt"]!.GetValue<string>(), CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await rig.PairRequestAsync(code);
        JsonNode paired = await HubAssert.JsonAsync(response);

        using JsonDocument document = JsonDocument.Parse(paired.ToJsonString());
        Assert.Empty(FleetSchemas.Validate(document.RootElement, FleetSchemas.PairResponse));
        Assert.Equal(43, paired["token"]!.GetValue<string>().Length);
        Assert.Equal("all", paired["scope"]!["type"]!.GetValue<string>());
        Assert.Null(paired["scope"]!["tags"]);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        // The hub is named so that Home Assistant can use its id as the id of the config entry, and the fingerprint is the one of the hub's certificate.
        using HttpResponseMessage hubResponse = await rig.Hub.Admin.GetAsync("/api/v1/hub");
        JsonNode hub = await HubAssert.JsonAsync(hubResponse);
        Assert.Equal(hub["id"]!.GetValue<string>(), paired["hub"]!["id"]!.GetValue<string>());
        Assert.Equal(hub["name"]!.GetValue<string>(), paired["hub"]!["name"]!.GetValue<string>());
        Assert.Equal(hub["fingerprint"]?.GetValue<string>(), paired["fingerprint"]?.GetValue<string>());
        Assert.Equal(hub["fingerprint"]?.GetValue<string>(), pairing["fingerprint"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_tag_scope_travels_from_the_code_to_the_token()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        JsonNode pairing = await rig.CreatePairingAsync("tags", ["lab", "gaming", "lab"], name: "Living room");

        Assert.Equal("tags", pairing["scope"]!["type"]!.GetValue<string>());
        Assert.Equal(["gaming", "lab"], pairing["scope"]!["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Equal("Living room", pairing["name"]!.GetValue<string>());

        using HttpResponseMessage response = await rig.PairRequestAsync(pairing["code"]!.GetValue<string>(), kind: "tool", name: "Script");
        JsonNode paired = await HubAssert.JsonAsync(response);
        using JsonDocument document = JsonDocument.Parse(paired.ToJsonString());
        Assert.Empty(FleetSchemas.Validate(document.RootElement, FleetSchemas.PairResponse));
        Assert.Equal("tags", paired["scope"]!["type"]!.GetValue<string>());
        Assert.Equal(["gaming", "lab"], paired["scope"]!["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));

        // The administrator's label wins over the name that the client gives.
        string id = await rig.TokenIdAsync(paired["token"]!.GetValue<string>());
        using HttpResponseMessage listResponse = await rig.Hub.Admin.GetAsync("/api/v1/fleet-tokens");
        JsonNode token = (await HubAssert.JsonAsync(listResponse))["tokens"]!.AsArray().Single(candidate => candidate!["id"]!.GetValue<string>() == id)!;
        Assert.Equal("Living room", token["name"]!.GetValue<string>());
        Assert.Equal("Script", token["clientName"]!.GetValue<string>());
        Assert.Equal("tool", token["clientKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_code_works_once()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string code = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();

        using HttpResponseMessage first = await rig.PairRequestAsync(code, remoteIp: "10.0.0.1");
        using HttpResponseMessage second = await rig.PairRequestAsync(code, remoteIp: "10.0.0.2");

        _ = await HubAssert.JsonAsync(first);
        Assert.Equal("unauthorized", await EnrollmentKit.ErrorCodeAsync(second, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task A_code_expires_after_five_minutes()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string code = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();

        rig.Hub.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        using HttpResponseMessage response = await rig.PairRequestAsync(code);

        Assert.Equal("unauthorized", await EnrollmentKit.ErrorCodeAsync(response, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Five_wrong_attempts_invalidate_the_code_and_the_address_is_locked_out()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string code = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage wrong = await rig.PairRequestAsync(WrongCodeFor(code), remoteIp: "10.0.0.1");
            Assert.Equal("unauthorized", await EnrollmentKit.ErrorCodeAsync(wrong, HttpStatusCode.Unauthorized));
        }

        // The right code from another address no longer works: the code is gone.
        using HttpResponseMessage late = await rig.PairRequestAsync(code, remoteIp: "10.0.0.2");
        Assert.Equal("unauthorized", await EnrollmentKit.ErrorCodeAsync(late, HttpStatusCode.Unauthorized));

        // The address that guessed is locked out, with the time to wait.
        using HttpResponseMessage lockedOut = await rig.PairRequestAsync(code, remoteIp: "10.0.0.1");
        Assert.Equal("rate_limited", await EnrollmentKit.ErrorCodeAsync(lockedOut, HttpStatusCode.TooManyRequests));
        Assert.NotNull(lockedOut.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_new_code_replaces_the_previous_one_and_a_cancelled_code_does_not_work()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string first = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();
        string second = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();

        if (first != second)
        {
            using HttpResponseMessage replaced = await rig.PairRequestAsync(first, remoteIp: "10.0.0.1");
            Assert.Equal("unauthorized", await EnrollmentKit.ErrorCodeAsync(replaced, HttpStatusCode.Unauthorized));
            second = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();
        }

        using HttpResponseMessage cancel = await rig.Hub.Admin.DeleteAsync("/api/v1/fleet-pairings");
        HubAssert.Status(cancel, HttpStatusCode.NoContent);
        using HttpResponseMessage cancelled = await rig.PairRequestAsync(second, remoteIp: "10.0.0.3");

        Assert.Equal("unauthorized", await EnrollmentKit.ErrorCodeAsync(cancelled, HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task A_pairing_request_that_is_not_valid_is_refused_with_bad_request()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string code = (await rig.CreatePairingAsync())["code"]!.GetValue<string>();

        // The hub is not a client of its own fleet link, and a client needs a name.
        using HttpResponseMessage hubKind = await rig.PairRequestAsync(code, kind: "hub", remoteIp: "10.0.1.1");
        using HttpResponseMessage noName = await rig.PairRequestAsync(code, name: "   ", remoteIp: "10.0.1.2");
        HubApiClient raw = rig.Hub.CreateClient();
        raw.SendCsrf = false;
        raw.RemoteIp = "10.0.1.3";
        using HttpResponseMessage notJson = await raw.PostAsync("/hub/v1/pair", "not json");
        raw.RemoteIp = "10.0.1.4";
        using HttpResponseMessage missing = await raw.PostAsync("/hub/v1/pair", "{}");

        foreach (HttpResponseMessage response in new[] { hubKind, noName, notJson, missing })
        {
            Assert.Equal("bad_request", await EnrollmentKit.ErrorCodeAsync(response, HttpStatusCode.BadRequest));
        }

        // None of this used up the code.
        using HttpResponseMessage paired = await rig.PairRequestAsync(code, remoteIp: "10.0.1.5");
        _ = await HubAssert.JsonAsync(paired);
    }

    [Fact]
    public async Task Creating_a_pairing_code_checks_the_scope_the_tags_and_the_name()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        (string Body, string Path)[] cases =
        [
            ("{\"scope\":\"everything\"}", "/scope"),
            ("{\"scope\":\"tags\"}", "/tags"),
            ("{\"scope\":\"tags\",\"tags\":[]}", "/tags"),
            ("{\"scope\":\"all\",\"tags\":[\"gaming\"]}", "/tags"),
            ("{\"scope\":\"tags\",\"tags\":[\"Not Valid!\"]}", "/tags"),
            ("{\"name\":\"" + new string('x', 65) + "\"}", "/name"),
        ];

        foreach ((string body, string path) in cases)
        {
            using HttpResponseMessage response = await rig.Hub.Admin.PostAsync("/api/v1/fleet-pairings", body);

            JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
            Assert.Equal(path, problem["errors"]![0]!["path"]!.GetValue<string>());
        }
    }

    // ------------------------------------------------------------------
    // Tokens
    // ------------------------------------------------------------------

    [Fact]
    public async Task Fleet_tokens_are_listed_with_their_scope_and_use_and_never_with_their_secret()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        PairedToken everything = await rig.PairAsync(name: "Home");
        PairedToken tagged = await rig.PairAsync("tags", ["gaming"], "Test PC");

        using HttpResponseMessage before = await rig.Hub.Admin.GetAsync("/api/v1/fleet-tokens");
        string text = await before.Content.ReadAsStringAsync();
        JsonNode list = await HubAssert.JsonAsync(before);
        JsonNode first = list["tokens"]!.AsArray().Single(token => token!["id"]!.GetValue<string>() == tagged.Id)!;

        Assert.Equal(2, list["tokens"]!.AsArray().Count);
        Assert.Equal("tags", first["scope"]!["type"]!.GetValue<string>());
        Assert.Equal(["gaming"], first["scope"]!["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.Equal(tagged.Secret[..6], first["prefix"]!.GetValue<string>());
        Assert.True(first["active"]!.GetValue<bool>());
        Assert.Equal(0, first["connections"]!.GetValue<int>());
        Assert.Null(first["lastUsedAt"]);
        Assert.DoesNotContain(tagged.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(everything.Secret, text, StringComparison.Ordinal);

        await rig.ConnectClientAsync(tagged);

        using HttpResponseMessage after = await rig.Hub.Admin.GetAsync("/api/v1/fleet-tokens");
        JsonNode used = (await HubAssert.JsonAsync(after))["tokens"]!.AsArray().Single(token => token!["id"]!.GetValue<string>() == tagged.Id)!;
        Assert.Equal(1, used["connections"]!.GetValue<int>());
        Assert.NotNull(used["lastUsedAt"]);
    }

    [Fact]
    public async Task The_hub_stores_only_the_hash_of_a_fleet_token()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        PairedToken token = await rig.PairAsync();

        string? stored = rig.Hub.Service<HubDatabase>().Query(connection =>
            HubSql.Scalar(connection, null, "SELECT token_hash FROM fleet_tokens WHERE id = @id;", ("@id", token.Id)) as string);
        long copies = rig.Hub.Service<HubDatabase>().Query(connection =>
            (long)HubSql.Scalar(
                connection,
                null,
                "SELECT COUNT(*) FROM fleet_tokens WHERE token_hash = @secret OR token_prefix = @secret OR name = @secret OR client_name = @secret;",
                ("@secret", token.Secret))!);

        Assert.Equal(AgentSecrets.Hash(token.Secret), stored);
        Assert.Equal(0, copies);
    }

    [Fact]
    public async Task Revoking_a_token_closes_its_connections_and_refuses_it_with_revoked()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        PairedToken token = await rig.PairAsync();
        FleetClient client = await rig.ConnectClientAsync(token);
        PairedToken other = await rig.PairAsync();
        FleetClient bystander = await rig.ConnectClientAsync(other);

        using HttpResponseMessage revoke = await rig.Hub.Admin.DeleteAsync("/api/v1/fleet-tokens/" + token.Id);

        HubAssert.Status(revoke, HttpStatusCode.NoContent);
        Assert.Equal(4401, await client.Closed.WaitAsync(TimeSpan.FromSeconds(10)));
        (HttpStatusCode status, string code) = await rig.OpenLinkWithoutUpgradeAsync(token.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("revoked", code);

        // The other token is not touched.
        JsonElement agents = await bystander.RequestAsync(FleetClient.Request("get_agents"));
        _ = FleetClient.DataOf(agents);
        Assert.False(bystander.Closed.IsCompleted);

        // The list keeps the token, as revoked.
        using HttpResponseMessage list = await rig.Hub.Admin.GetAsync("/api/v1/fleet-tokens");
        JsonNode listed = (await HubAssert.JsonAsync(list))["tokens"]!.AsArray().Single(candidate => candidate!["id"]!.GetValue<string>() == token.Id)!;
        Assert.False(listed["active"]!.GetValue<bool>());
        Assert.NotNull(listed["revokedAt"]);
    }

    [Fact]
    public async Task Revoking_a_token_that_does_not_exist_is_a_404()
    {
        await using FleetRig rig = await FleetRig.StartAsync();

        using HttpResponseMessage response = await rig.Hub.Admin.DeleteAsync("/api/v1/fleet-tokens/unknown-id");

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.NotFound, "unknown_token");
    }

    // ------------------------------------------------------------------
    // The upgrade request
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_fleet_link_refuses_a_missing_an_unknown_and_a_foreign_token_with_401()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string apiToken = await rig.Hub.CreateTokenAsync("not-for-the-fleet", "admin");
        string agentLookingToken = AgentSecrets.Create();

        foreach (string? token in new string?[] { null, "garbage", agentLookingToken, apiToken })
        {
            (HttpStatusCode status, string code) = await rig.OpenLinkWithoutUpgradeAsync(token);

            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("unauthorized", code);
        }
    }

    [Fact]
    public async Task A_token_that_is_accepted_still_needs_a_websocket_upgrade()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        PairedToken token = await rig.PairAsync();

        (HttpStatusCode status, string code) = await rig.OpenLinkWithoutUpgradeAsync(token.Secret);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", code);
    }

    [Fact]
    public async Task A_fleet_token_does_not_authenticate_the_management_api()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        PairedToken token = await rig.PairAsync();
        HubApiClient client = rig.Hub.CreateClient();
        client.BearerToken = token.Secret;

        using HttpResponseMessage hub = await client.GetAsync("/api/v1/hub");
        using HttpResponseMessage tokens = await client.GetAsync("/api/v1/fleet-tokens");

        _ = await HubAssert.ProblemAsync(hub, HttpStatusCode.Unauthorized, "unauthorized");
        _ = await HubAssert.ProblemAsync(tokens, HttpStatusCode.Unauthorized, "unauthorized");
    }

    // ------------------------------------------------------------------
    // The audit log
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pairing_and_the_changes_of_tokens_are_audited_and_the_log_holds_no_secret()
    {
        await using FleetRig rig = await FleetRig.StartAsync();
        string code = (await rig.CreatePairingAsync("tags", ["gaming"]))["code"]!.GetValue<string>();
        using HttpResponseMessage refused = await rig.PairRequestAsync(WrongCodeFor(code), name: "Intruder", remoteIp: "10.0.2.1");
        using HttpResponseMessage accepted = await rig.PairRequestAsync(code, name: "Home", remoteIp: "10.0.2.2");
        string secret = (await HubAssert.JsonAsync(accepted))["token"]!.GetValue<string>();
        string id = await rig.TokenIdAsync(secret);
        using HttpResponseMessage revoke = await rig.Hub.Admin.DeleteAsync("/api/v1/fleet-tokens/" + id);

        JsonArray created = await rig.AuditAsync("createFleetPairing");
        JsonArray pairings = await rig.AuditAsync("pairFleet");
        JsonArray revoked = await rig.AuditAsync("revokeFleetToken");

        Assert.Equal("success", Assert.Single(created)!["result"]!.GetValue<string>());
        Assert.Equal("admin", created[0]!["user"]!.GetValue<string>());
        Assert.Equal(2, pairings.Count);
        JsonNode success = pairings.Single(entry => entry!["result"]!.GetValue<string>() == "success")!;
        Assert.Equal("fleet-client:Home", success["user"]!.GetValue<string>());
        Assert.Equal("pairing_code", success["authMethod"]!.GetValue<string>());
        Assert.Equal("fleet-token:" + id, success["target"]!.GetValue<string>());
        JsonNode denied = pairings.Single(entry => entry!["result"]!.GetValue<string>() == "denied")!;
        Assert.Equal("anonymous", denied["user"]!.GetValue<string>());
        Assert.Equal("fleet-token:" + id, Assert.Single(revoked)!["target"]!.GetValue<string>());

        using HttpResponseMessage everything = await rig.Hub.Admin.GetAsync("/api/v1/audit?limit=500");
        Assert.DoesNotContain(secret, await everything.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join('\n', rig.Hub.Logs.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain("\"" + code + "\"", await everything.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
