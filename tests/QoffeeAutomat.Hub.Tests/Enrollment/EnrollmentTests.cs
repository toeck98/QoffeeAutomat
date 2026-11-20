using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Enrollment;

/// <summary>
/// Enrollment tokens and <c>POST /hub/v1/enroll</c> (T904): expiry and maximum uses, approval, credentials stored as hashes, replacing a credential,
/// rate limiting and the audit log.
/// </summary>
[Trait("Category", "Enrollment")]
public sealed partial class EnrollmentTests
{
    private const string AnyValidToken = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex CredentialPattern();

    [Fact]
    public async Task A_token_is_created_with_an_enrollment_string_that_names_the_hub_its_key_and_its_certificate()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        TestToken token = await EnrollmentKit.CreateTokenAsync(host, "office", maxUses: 3, tags: ["Office", "lab"]);

        EnrollmentPayload payload = EnrollmentString.Decode(token.EnrollmentString);
        using HttpResponseMessage hubResponse = await host.Admin.GetAsync("/api/v1/hub");
        JsonNode hub = await HubAssert.JsonAsync(hubResponse);
        Assert.StartsWith("qahub1.", token.EnrollmentString, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://localhost"), payload.Url);
        Assert.Equal(hub["signingKey"]!.GetValue<string>(), payload.Key);
        Assert.Equal(hub["fingerprint"]!.GetValue<string>(), payload.Fp);
        Assert.Matches(@"^[A-Za-z0-9_-]{43}$", payload.Token);

        using HttpResponseMessage listResponse = await host.Admin.GetAsync("/api/v1/enrollment-tokens");
        string listJson = await listResponse.Content.ReadAsStringAsync();
        JsonNode listed = (await HubAssert.JsonAsync(listResponse))["tokens"]![0]!;
        Assert.Equal("office", listed["name"]!.GetValue<string>());
        Assert.Equal(3, listed["maxUses"]!.GetValue<int>());
        Assert.Equal(0, listed["uses"]!.GetValue<int>());
        Assert.True(listed["active"]!.GetValue<bool>());
        Assert.Equal(["lab", "office"], listed["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.DoesNotContain(payload.Token, listJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_enrollment_string_uses_the_address_that_the_administrator_gives()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        using HttpResponseMessage response = await host.Admin.PostAsync(
            "/api/v1/enrollment-tokens",
            HubApiClient.Json(("name", "behind-proxy"), ("hubUrl", "https://hub.example.org:8490/")));

        JsonNode created = await HubAssert.JsonAsync(response, HttpStatusCode.Created);
        EnrollmentPayload payload = EnrollmentString.Decode(created["enrollmentString"]!.GetValue<string>());
        Assert.Equal("hub.example.org", payload.Url.Host);
        Assert.Equal(8490, payload.Url.Port);
    }

    [Theory]
    [InlineData("""{"name":""}""", "/name")]
    [InlineData("""{"name":"x","maxUses":0}""", "/maxUses")]
    [InlineData("""{"name":"x","maxUses":10001}""", "/maxUses")]
    [InlineData("""{"name":"x","tags":["bad tag"]}""", "/tags")]
    [InlineData("""{"name":"x","expiresAt":"2020-01-01T00:00:00Z"}""", "/expiresAt")]
    [InlineData("""{"name":"x","hubUrl":"http://hub.example.org"}""", "/hubUrl")]
    [InlineData("""{"name":"x","hubUrl":"ftp://hub.example.org"}""", "/hubUrl")]
    public async Task A_token_with_invalid_settings_is_refused(string body, string path)
    {
        await using HubTestHost host = await HubTestHost.StartAsync();

        using HttpResponseMessage response = await host.Admin.PostAsync("/api/v1/enrollment-tokens", body);

        JsonNode problem = await HubAssert.ProblemAsync(response, (HttpStatusCode)422, "validation_failed");
        Assert.Equal(path, problem["errors"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Enrolling_issues_a_credential_and_the_hub_keeps_only_its_hash()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, tags: ["lab"]);

        using HttpResponseMessage response = await EnrollmentKit.EnrollAsync(host, token.Secret, "agent-1");

        JsonNode body = await HubAssert.JsonAsync(response);
        Assert.Equal("enrolled", body["state"]!.GetValue<string>());
        string credential = body["credential"]!.GetValue<string>();
        Assert.Matches(CredentialPattern(), credential);
        Assert.False(string.IsNullOrEmpty(body["hub"]!["id"]!.GetValue<string>()));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        string stored = EnrollmentKit.StoredCredentialHash("agent-1", host);
        Assert.Equal(64, stored.Length);
        Assert.NotEqual(credential, stored);

        JsonNode agent = await EnrollmentKit.GetAgentAsync(host, "agent-1");
        Assert.Equal("enrolled", agent["status"]!.GetValue<string>());
        Assert.Equal("Gaming-PC", agent["name"]!.GetValue<string>());
        Assert.Equal("GAMING-PC", agent["hostname"]!.GetValue<string>());
        Assert.Equal("windows", agent["os"]!.GetValue<string>());
        Assert.Equal("1.0.0", agent["version"]!.GetValue<string>());
        Assert.Equal(["lab"], agent["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        Assert.NotNull(agent["enrolledAt"]);
        Assert.Null(agent["lastSeenAt"]);
        Assert.DoesNotContain(credential, agent.ToJsonString(), StringComparison.Ordinal);

        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, credential));
        Assert.NotNull((await EnrollmentKit.GetAgentAsync(host, "agent-1"))["lastSeenAt"]);
    }

    [Fact]
    public async Task A_token_cannot_be_used_after_it_has_expired()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, expiresAt: host.Clock.GetUtcNow().AddHours(1));

        host.Clock.Advance(TimeSpan.FromMinutes(59));
        string credential = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "early");
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        using HttpResponseMessage late = await EnrollmentKit.EnrollAsync(host, token.Secret, "late");

        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(late, HttpStatusCode.Unauthorized));
        using HttpResponseMessage missing = await host.Admin.GetAsync("/api/v1/agents/late");
        await HubAssert.ProblemAsync(missing, HttpStatusCode.NotFound, "unknown_agent");
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, credential));
    }

    [Fact]
    public async Task A_token_cannot_be_used_beyond_its_maximum_number_of_uses()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 2);

        await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "one");
        await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "two");
        using HttpResponseMessage third = await EnrollmentKit.EnrollAsync(host, token.Secret, "three");

        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(third, HttpStatusCode.Unauthorized));
        using HttpResponseMessage listResponse = await host.Admin.GetAsync("/api/v1/enrollment-tokens");
        JsonNode listed = (await HubAssert.JsonAsync(listResponse))["tokens"]![0]!;
        Assert.Equal(2, listed["uses"]!.GetValue<int>());
        Assert.False(listed["active"]!.GetValue<bool>());
        Assert.NotNull(listed["lastUsedAt"]);
    }

    [Fact]
    public async Task A_token_that_is_unknown_or_revoked_enrolls_nobody()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 5);
        string early = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "before-revoke");

        using HttpResponseMessage revoke = await host.Admin.DeleteAsync("/api/v1/enrollment-tokens/" + token.Id);
        HubAssert.Status(revoke, HttpStatusCode.NoContent);
        using HttpResponseMessage revoked = await EnrollmentKit.EnrollAsync(host, token.Secret, "after-revoke");
        using HttpResponseMessage unknown = await EnrollmentKit.EnrollAsync(host, AnyValidToken, "stranger");

        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(revoked, HttpStatusCode.Unauthorized));
        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(unknown, HttpStatusCode.Unauthorized));

        // Revoking the token ends enrollments, not the agents that it enrolled.
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, early));
    }

    [Fact]
    public async Task An_agent_of_an_approval_token_cannot_open_the_agent_link_before_it_is_approved()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, approval: true);

        using HttpResponseMessage first = await EnrollmentKit.EnrollAsync(host, token.Secret, "waiting");
        JsonNode pending = await HubAssert.JsonAsync(first);

        Assert.Equal("pending", pending["state"]!.GetValue<string>());
        Assert.Equal(30, pending["retry_after"]!.GetValue<int>());
        Assert.Null(pending["credential"]);
        Assert.Equal("pending", (await EnrollmentKit.GetAgentAsync(host, "waiting"))["status"]!.GetValue<string>());
        Assert.Null(EnrollmentKit.StoredCredentialHashOrNull("waiting", host));

        // The agent asks again: still pending, still without a credential, and the link stays closed to every credential it could guess.
        using HttpResponseMessage again = await EnrollmentKit.EnrollAsync(host, token.Secret, "waiting");
        Assert.Equal("pending", (await HubAssert.JsonAsync(again))["state"]!.GetValue<string>());
        (HttpStatusCode status, string code) = await EnrollmentKit.OpenLinkAsync(host, AnyValidToken);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("unauthorized", code);
        (HttpStatusCode noCredential, _) = await EnrollmentKit.OpenLinkAsync(host, null);
        Assert.Equal(HttpStatusCode.Unauthorized, noCredential);

        // Approval alone does not open the link: the credential is handed over with the next enroll request.
        using HttpResponseMessage approve = await host.Admin.PostAsync("/api/v1/agents/waiting/approve");
        JsonNode approved = await HubAssert.JsonAsync(approve);
        Assert.Equal("approved", approved["status"]!.GetValue<string>());
        Assert.NotNull(approved["approvedAt"]);
        Assert.Null(EnrollmentKit.StoredCredentialHashOrNull("waiting", host));

        using HttpResponseMessage pickup = await EnrollmentKit.EnrollAsync(host, token.Secret, "waiting");
        JsonNode picked = await HubAssert.JsonAsync(pickup);
        Assert.Equal("enrolled", picked["state"]!.GetValue<string>());
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, picked["credential"]!.GetValue<string>()));
        Assert.Equal("enrolled", (await EnrollmentKit.GetAgentAsync(host, "waiting"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_pending_agent_that_asks_again_does_not_use_up_another_use_of_the_token()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 1, approval: true);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using HttpResponseMessage response = await EnrollmentKit.EnrollAsync(host, token.Secret, "patient");
            Assert.Equal("pending", (await HubAssert.JsonAsync(response))["state"]!.GetValue<string>());
        }

        using HttpResponseMessage other = await EnrollmentKit.EnrollAsync(host, token.Secret, "impatient");
        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(other, HttpStatusCode.Unauthorized));
        using HttpResponseMessage listResponse = await host.Admin.GetAsync("/api/v1/enrollment-tokens");
        Assert.Equal(1, (await HubAssert.JsonAsync(listResponse))["tokens"]![0]!["uses"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_rejected_agent_gets_no_credential_and_cannot_be_approved_afterwards()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 5, approval: true);
        using HttpResponseMessage first = await EnrollmentKit.EnrollAsync(host, token.Secret, "unwanted");
        await HubAssert.JsonAsync(first);

        using HttpResponseMessage reject = await host.Admin.PostAsync("/api/v1/agents/unwanted/reject");
        Assert.Equal("rejected", (await HubAssert.JsonAsync(reject))["status"]!.GetValue<string>());

        using HttpResponseMessage again = await EnrollmentKit.EnrollAsync(host, token.Secret, "unwanted");
        Assert.Equal("rejected", await EnrollmentKit.ErrorCodeAsync(again, HttpStatusCode.Forbidden));
        using HttpResponseMessage approve = await host.Admin.PostAsync("/api/v1/agents/unwanted/approve");
        await HubAssert.ProblemAsync(approve, HttpStatusCode.Conflict, "invalid_state");
        using HttpResponseMessage approveUnknown = await host.Admin.PostAsync("/api/v1/agents/nobody/approve");
        await HubAssert.ProblemAsync(approveUnknown, HttpStatusCode.NotFound, "unknown_agent");
        Assert.Null(EnrollmentKit.StoredCredentialHashOrNull("unwanted", host));
    }

    [Fact]
    public async Task Enrolling_the_same_agent_id_again_replaces_its_credential_only_with_a_valid_token()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 5, tags: ["lab"]);
        TestToken spent = await EnrollmentKit.CreateTokenAsync(host, "spent", maxUses: 1);
        string first = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "twice");
        await EnrollmentKit.EnrollForCredentialAsync(host, spent.Secret, "someone-else");

        // Without a valid token nothing changes: made up, used up and revoked tokens are all refused and the old credential keeps working.
        using HttpResponseMessage made = await EnrollmentKit.EnrollAsync(host, AnyValidToken, "twice");
        using HttpResponseMessage usedUp = await EnrollmentKit.EnrollAsync(host, spent.Secret, "twice");
        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(made, HttpStatusCode.Unauthorized));
        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(usedUp, HttpStatusCode.Unauthorized));
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, first));

        // A tag that an administrator set stays when the agent enrolls again.
        using HttpResponseMessage tagResponse = await host.Admin.PatchAsync("/api/v1/agents/twice", """{"tags":["gaming","lab"]}""");
        await HubAssert.JsonAsync(tagResponse);

        // With a valid token the credential is replaced and the old one is revoked.
        string second = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "twice");

        Assert.NotEqual(first, second);
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, second));
        Assert.Equal((HttpStatusCode.Unauthorized, "revoked"), await EnrollmentKit.OpenLinkAsync(host, first));
        JsonNode agent = await EnrollmentKit.GetAgentAsync(host, "twice");
        Assert.Equal(["gaming", "lab"], agent["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
        using HttpResponseMessage list = await host.Admin.GetAsync("/api/v1/agents");
        Assert.Equal(2, (await HubAssert.JsonAsync(list))["agents"]!.AsArray().Count);
    }

    [Fact]
    public async Task Enrolling_an_enrolled_agent_with_an_approval_token_makes_it_wait_again_and_ends_its_old_credential()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken plain = await EnrollmentKit.CreateTokenAsync(host, "plain");
        TestToken gated = await EnrollmentKit.CreateTokenAsync(host, "gated", approval: true);
        string old = await EnrollmentKit.EnrollForCredentialAsync(host, plain.Secret, "reset");

        using HttpResponseMessage response = await EnrollmentKit.EnrollAsync(host, gated.Secret, "reset");

        Assert.Equal("pending", (await HubAssert.JsonAsync(response))["state"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode.Unauthorized, "revoked"), await EnrollmentKit.OpenLinkAsync(host, old));
    }

    [Fact]
    public async Task Removing_an_agent_revokes_its_credential_and_later_connections_are_answered_with_401_revoked()
    {
        var notifier = new RecordingNotifier();
        await using HubTestHost host = await HubTestHost.StartAsync(configure: notifier.Register);
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 2);
        string credential = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "leaving");
        string other = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "staying");

        using HttpResponseMessage remove = await host.Admin.DeleteAsync("/api/v1/agents/leaving");

        HubAssert.Status(remove, HttpStatusCode.NoContent);
        Assert.Equal(("leaving", "removed"), Assert.Single(notifier.Unenrolls));
        Assert.Equal((HttpStatusCode.Unauthorized, "revoked"), await EnrollmentKit.OpenLinkAsync(host, credential));
        Assert.Equal((HttpStatusCode.Unauthorized, "unauthorized"), await EnrollmentKit.OpenLinkAsync(host, AnyValidToken));
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, other));
        using HttpResponseMessage gone = await host.Admin.GetAsync("/api/v1/agents/leaving");
        await HubAssert.ProblemAsync(gone, HttpStatusCode.NotFound, "unknown_agent");
        using HttpResponseMessage removeAgain = await host.Admin.DeleteAsync("/api/v1/agents/leaving");
        await HubAssert.ProblemAsync(removeAgain, HttpStatusCode.NotFound, "unknown_agent");

        // The agent may enroll again later with a valid token, which gives it a new, working credential.
        TestToken fresh = await EnrollmentKit.CreateTokenAsync(host, "fresh");
        string again = await EnrollmentKit.EnrollForCredentialAsync(host, fresh.Secret, "leaving");
        EnrollmentKit.AssertAccepted(await EnrollmentKit.OpenLinkAsync(host, again));
        Assert.Equal((HttpStatusCode.Unauthorized, "revoked"), await EnrollmentKit.OpenLinkAsync(host, credential));
    }

    [Fact]
    public async Task Malformed_enroll_requests_are_answered_with_400_and_the_error_object()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        string[] bodies =
        [
            "not json",
            "{}",
            """{"token":"short","agent":{"id":"a","name":"n","version":"1","os":"windows","arch":"x64","hostname":"h","macs":[]}}""",
            $$"""{"token":"{{AnyValidToken}}"}""",
            $$$"""{"token":"{{{AnyValidToken}}}","agent":{"id":"has space","name":"n","version":"1","os":"windows","arch":"x64","hostname":"h","macs":[]}}""",
            $$$"""{"token":"{{{AnyValidToken}}}","agent":{"id":"a","name":"","version":"1","os":"windows","arch":"x64","hostname":"h","macs":[]}}""",
        ];

        for (int index = 0; index < bodies.Length; index++)
        {
            // Every failure counts towards the lockout of its address, so each request comes from another one.
            using HttpResponseMessage response = await EnrollmentKit.EnrollAsync(
                host, AnyValidToken, "unused", "192.0.2." + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), bodies[index]);

            Assert.Equal("bad_request", await EnrollmentKit.ErrorCodeAsync(response, HttpStatusCode.BadRequest));
        }
    }

    [Fact]
    public async Task Failed_enroll_requests_are_rate_limited_per_address()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host, maxUses: 10);
        const string attacker = "203.0.113.7";

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage failed = await EnrollmentKit.EnrollAsync(host, AnyValidToken, "guess", attacker);
            Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(failed, HttpStatusCode.Unauthorized));
        }

        // Locked out: even the right token is refused from that address, with Retry-After; other addresses are not affected.
        using HttpResponseMessage locked = await EnrollmentKit.EnrollAsync(host, token.Secret, "guess", attacker);
        Assert.Equal("rate_limited", await EnrollmentKit.ErrorCodeAsync(locked, HttpStatusCode.TooManyRequests));
        Assert.True(locked.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retryAfter));
        Assert.True(int.Parse(retryAfter!.Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        using HttpResponseMessage elsewhere = await EnrollmentKit.EnrollAsync(host, token.Secret, "honest", "198.51.100.20");
        Assert.Equal("enrolled", (await HubAssert.JsonAsync(elsewhere))["state"]!.GetValue<string>());

        // The lockout ends after its duration.
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        using HttpResponseMessage later = await EnrollmentKit.EnrollAsync(host, token.Secret, "guess", attacker);
        Assert.Equal("enrolled", (await HubAssert.JsonAsync(later))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Enrollment_events_are_audited_and_the_audit_log_holds_no_secret()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        string credential = await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "audited");
        using HttpResponseMessage refused = await EnrollmentKit.EnrollAsync(host, AnyValidToken, "intruder");
        Assert.Equal("invalid_token", await EnrollmentKit.ErrorCodeAsync(refused, HttpStatusCode.Unauthorized));

        using HttpResponseMessage response = await host.Admin.GetAsync("/api/v1/audit");
        string json = await response.Content.ReadAsStringAsync();
        JsonArray entries = (await HubAssert.JsonAsync(response))["entries"]!.AsArray();

        JsonNode enrolled = Assert.Single(entries, entry => entry!["action"]!.GetValue<string>() == "enrollAgent" && entry["result"]!.GetValue<string>() == "success")!;
        Assert.Equal("agent:audited", enrolled["target"]!.GetValue<string>());
        Assert.Equal(["audited"], enrolled["agents"]!.AsArray().Select(agent => agent!.GetValue<string>()));
        Assert.Single(entries, entry => entry!["action"]!.GetValue<string>() == "enrollAgent" && entry["result"]!.GetValue<string>() == "denied");
        Assert.Single(entries, entry => entry!["action"]!.GetValue<string>() == "createEnrollmentToken");
        Assert.DoesNotContain(credential, json, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Secret, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_to_agents_and_tokens_are_audited_with_the_affected_agent()
    {
        await using HubTestHost host = await HubTestHost.StartAsync();
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "edited");

        using HttpResponseMessage tagResponse = await host.Admin.PatchAsync("/api/v1/agents/edited", """{"tags":["lab"]}""");
        await HubAssert.JsonAsync(tagResponse);
        using HttpResponseMessage remove = await host.Admin.DeleteAsync("/api/v1/agents/edited");
        HubAssert.Status(remove, HttpStatusCode.NoContent);

        using HttpResponseMessage response = await host.Admin.GetAsync("/api/v1/audit");
        JsonArray entries = (await HubAssert.JsonAsync(response))["entries"]!.AsArray();
        foreach (string action in new[] { "updateAgent", "removeAgent" })
        {
            JsonNode entry = Assert.Single(entries, candidate => candidate!["action"]!.GetValue<string>() == action)!;
            Assert.Equal("success", entry["result"]!.GetValue<string>());
            Assert.Equal(["edited"], entry["agents"]!.AsArray().Select(agent => agent!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task Tags_are_normalised_and_only_administrators_edit_them()
    {
        await using HubTestHost host = await HubTestHost.StartAsync(withUsers: true);
        TestToken token = await EnrollmentKit.CreateTokenAsync(host);
        await EnrollmentKit.EnrollForCredentialAsync(host, token.Secret, "tagged");

        using HttpResponseMessage edit = await host.Admin.PatchAsync("/api/v1/agents/tagged", """{"tags":["Lab"," lab ","b-2","a.b_c"]}""");
        JsonNode edited = await HubAssert.JsonAsync(edit);
        Assert.Equal(["a.b_c", "b-2", "lab"], edited["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));

        using HttpResponseMessage invalid = await host.Admin.PatchAsync("/api/v1/agents/tagged", """{"tags":["bad tag"]}""");
        using HttpResponseMessage missing = await host.Admin.PatchAsync("/api/v1/agents/tagged", "{}");
        using HttpResponseMessage unknown = await host.Admin.PatchAsync("/api/v1/agents/nobody", """{"tags":[]}""");
        await HubAssert.ProblemAsync(invalid, (HttpStatusCode)422, "validation_failed");
        await HubAssert.ProblemAsync(missing, (HttpStatusCode)422, "validation_failed");
        await HubAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_agent");

        using HttpResponseMessage operatorEdit = await host.Operator.PatchAsync("/api/v1/agents/tagged", """{"tags":[]}""");
        await HubAssert.ProblemAsync(operatorEdit, HttpStatusCode.Forbidden, "forbidden");
        using HttpResponseMessage viewerList = await host.Viewer.GetAsync("/api/v1/agents?status=enrolled");
        Assert.Single((await HubAssert.JsonAsync(viewerList))["agents"]!.AsArray());
        using HttpResponseMessage badStatus = await host.Viewer.GetAsync("/api/v1/agents?status=nonsense");
        await HubAssert.ProblemAsync(badStatus, (HttpStatusCode)422, "validation_failed");
        using HttpResponseMessage pendingList = await host.Viewer.GetAsync("/api/v1/agents?status=pending");
        Assert.Empty((await HubAssert.JsonAsync(pendingList))["agents"]!.AsArray());
    }

    [Theory]
    [InlineData("lab", true)]
    [InlineData("a", true)]
    [InlineData("0day", true)]
    [InlineData("gaming-pc.1_x", true)]
    [InlineData("", false)]
    [InlineData("-lab", false)]
    [InlineData("Lab", false)]
    [InlineData("a:b", false)]
    [InlineData("has space", false)]
    [InlineData("0123456789012345678901234567890123", false)]
    public void Tags_follow_the_rules_of_assignment_targets(string tag, bool valid)
    {
        Assert.Equal(valid, QoffeeAutomat.Hub.Agents.AgentTags.IsValid(tag));
    }
}
