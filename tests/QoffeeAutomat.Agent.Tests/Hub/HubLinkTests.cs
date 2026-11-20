using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Agent.Tests.Link;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Tests.Hub;

/// <summary>Helpers shared by the hub-link tests: an agent whose outbound link dials quickly, and waiting for the state that <c>/status</c> reports.</summary>
internal static class HubLinkRig
{
    public const string RunLevel3 = "\"command\":\"" + LinkTestHost.CommandId + "\",\"vars\":{\"Level\":3}";

    /// <summary>Delays short enough for a test and without jitter, so that the number of attempts is predictable.</summary>
    public static OutboundOptions FastOptions() => new()
    {
        BackoffMin = TimeSpan.FromMilliseconds(20),
        BackoffMax = TimeSpan.FromMilliseconds(80),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Random = () => 1.0,
    };

    /// <summary>The agent with the simulated platform, the fast timings and a pending enrollment that is asked again after a few milliseconds.</summary>
    public static LinkTestHost NewAgent()
    {
        return new LinkTestHost(configure: services =>
        {
            services.RemoveAll<OutboundOptions>();
            services.AddSingleton(FastOptions());
            services.RemoveAll<HubLinkOptions>();
            services.AddSingleton(new HubLinkOptions { PendingDelayScale = 0.01 });
        });
    }

    public static HubLinkManager ManagerOf(LinkTestHost host) => host.Agent.Services.GetRequiredService<HubLinkManager>();

    public static HubLinkService ServiceOf(LinkTestHost host) => host.Agent.Services.GetRequiredService<HubLinkService>();

    /// <summary>Joins the hub, waits for the connection and answers <c>hello</c> as the hub does.</summary>
    public static async Task<LinkTestClient> JoinAndGreetAsync(FakeHub hub, LinkTestHost host)
    {
        HubJoinResult joined = await ManagerOf(host).JoinAsync(hub.EnrollmentString(), CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
        LinkTestClient peer = await hub.WaitForAgentAsync();
        await peer.SendAsync(FakeHub.HubClientHello());
        return peer;
    }

    /// <summary>Waits until <c>/status</c> reports a state of the hub link.</summary>
    public static async Task<HubLinkStatus> UntilStatusAsync(HubLinkService service, string state)
    {
        DateTime deadline = DateTime.UtcNow + TestWait.DefaultTimeout;
        HubLinkStatus status = await service.GetStatusAsync(CancellationToken.None);
        while (!string.Equals(status.State, state, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the hub state '" + state + "'; it is '" + status.State + "' (" + status.Detail + ").");
            }

            await Task.Delay(20);
            status = await service.GetStatusAsync(CancellationToken.None);
        }

        return status;
    }

    /// <summary>A SHA-256 fingerprint that belongs to no certificate.</summary>
    public static string WrongFingerprint() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Checks that the number of requests that reached the hub does not change for a short while.</summary>
    public static async Task AssertNoMoreDialsAsync(FakeHub hub)
    {
        int dials = hub.Presented.Length;
        await Task.Delay(300);
        Assert.Equal(dials, hub.Presented.Length);
    }
}

/// <summary>
/// The agent side of the hub link (T905, PLAN.md S20): the agent enrolls with a token, dials the hub, serves it like any QoffeeLink client, reconnects after the
/// hub restarted, accepts only the pinned certificate, and obeys <c>credential_rotate</c>, <c>unenroll</c> and a revoked credential.
/// </summary>
[Trait("Category", "HubLink")]
public sealed class HubLinkTests
{
    [Fact]
    public async Task The_agent_enrolls_connects_and_serves_the_hub_like_any_other_link_client()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await host.AddCommandAsync();
        HubLinkManager manager = HubLinkRig.ManagerOf(host);

        HubJoinResult joined = await manager.JoinAsync(hub.EnrollmentString(), CancellationToken.None);

        Assert.True(joined.Joined, joined.Error);
        Assert.False(joined.Pending);
        Assert.Equal(1, hub.EnrollCalls);
        HubStoredState state = (await manager.GetStateAsync(CancellationToken.None))!;
        Assert.Equal(HubPhases.Enrolled, state.Phase);
        Assert.Equal(hub.LastCredential, state.Credential);
        Assert.Equal(FakeHub.HubId, state.HubId);
        Assert.Equal(FakeHub.HubName, state.HubName);
        Assert.Equal(hub.SigningKey, state.PublicKey);
        Assert.Null(state.Token);
        Assert.True(FingerprintsAreEqual(hub.Fingerprint, state.Fingerprint));

        await using LinkTestClient peer = await hub.WaitForAgentAsync();

        // The agent speaks first, no matter who dialled (S07), and announces the management tunnel while the local switch allows it.
        JsonElement hello = peer.Frames[0];
        Assert.Equal(MessageTypes.Hello, LinkTestClient.TypeOf(hello));
        Assert.Equal(host.ManifestSource.Current.Rev, hello.GetProperty("manifest_rev").GetString());
        Assert.Contains(HubFeatures.MgmtTunnel, hello.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
        await peer.SendAsync(FakeHub.HubClientHello());

        JsonElement manifest = await peer.RequestAsync(MessageTypes.GetManifest);
        Assert.True(manifest.GetProperty("ok").GetBoolean());
        Assert.Contains(
            manifest.GetProperty("data").GetProperty("commands").EnumerateArray(),
            command => command.GetProperty("id").GetString() == LinkTestHost.CommandId);

        JsonElement subscribed = await peer.RequestAsync(MessageTypes.Subscribe, "\"metrics\":\"*\"");
        Assert.True(subscribed.GetProperty("ok").GetBoolean());
        JsonElement snapshot = await peer.WaitForTypeAsync(MessageTypes.State);
        Assert.NotEmpty(LinkTestClient.MetricsOf(snapshot));

        JsonElement ran = await peer.RequestAsync(MessageTypes.Run, HubLinkRig.RunLevel3);
        Assert.True(ran.GetProperty("ok").GetBoolean());
        Assert.Equal(0, ran.GetProperty("data").GetProperty("exit_code").GetInt32());
        AssertLaunchedWith(host, ["--level", "3"]);

        // Nothing went through the inbound side, and the hub saw exactly one request with the credential it issued.
        Assert.Empty(await host.Pairing.ListClientsAsync(CancellationToken.None));
        Assert.Equal(hub.LastCredential, Assert.Single(hub.Presented));
        HubLinkStatus status = await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Connected);
        Assert.Equal(FakeHub.HubId, status.HubId);
        Assert.Empty(peer.Problems);
    }

    [Fact]
    public async Task A_token_that_needs_approval_leaves_the_agent_pending_until_the_hub_accepts_it()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        hub.RequireApproval = true;
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubLinkManager manager = HubLinkRig.ManagerOf(host);

        HubJoinResult joined = await manager.JoinAsync(hub.EnrollmentString(), CancellationToken.None);

        Assert.True(joined.Joined, joined.Error);
        Assert.True(joined.Pending);
        HubLinkStatus pending = await HubLinkRig.ServiceOf(host).GetStatusAsync(CancellationToken.None);
        Assert.Equal(HubLinkStates.Pending, pending.State);
        HubStoredState waiting = (await manager.GetStateAsync(CancellationToken.None))!;
        Assert.Null(waiting.Credential);
        Assert.NotNull(waiting.Token);
        Assert.Equal(0, hub.Accepted);

        hub.Approve();

        await using LinkTestClient peer = await hub.WaitForAgentAsync();
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Connected);
        HubStoredState enrolled = (await manager.GetStateAsync(CancellationToken.None))!;
        Assert.Equal(HubPhases.Enrolled, enrolled.Phase);
        Assert.Equal(hub.LastCredential, enrolled.Credential);
        Assert.Null(enrolled.Token);
    }

    [Fact]
    public async Task A_token_that_the_hub_does_not_accept_or_an_agent_that_it_rejected_is_not_joined()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubLinkManager manager = HubLinkRig.ManagerOf(host);

        string enrollment = hub.EnrollmentString();
        hub.ValidToken = FakeHub.NewSecret();
        HubJoinResult wrongToken = await manager.JoinAsync(enrollment, CancellationToken.None);
        hub.RejectEnrollment = true;
        HubJoinResult rejected = await manager.JoinAsync(hub.EnrollmentString(), CancellationToken.None);

        Assert.False(wrongToken.Joined);
        Assert.Equal(HubJoinFailure.Refused, wrongToken.Failure);
        Assert.False(rejected.Joined);
        Assert.Equal(HubJoinFailure.Refused, rejected.Failure);
        Assert.Null(await manager.GetStateAsync(CancellationToken.None));
        Assert.Equal(0, hub.Accepted);
        Assert.DoesNotContain(hub.ValidToken, rejected.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_has_joined_a_hub_cannot_join_another_one_without_leaving()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await HubLinkRig.JoinAndGreetAsync(hub, host);

        HubJoinResult again = await HubLinkRig.ManagerOf(host).JoinAsync(hub.EnrollmentString(), CancellationToken.None);

        Assert.False(again.Joined);
        Assert.Equal(HubJoinFailure.AlreadyJoined, again.Failure);
        Assert.Equal(1, hub.EnrollCalls);
    }

    [Fact]
    public async Task The_agent_dials_again_with_the_same_credential_after_the_hub_restarted()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient first = await HubLinkRig.JoinAndGreetAsync(hub, host);
        string credential = hub.LastCredential!;

        await hub.RestartAsync();

        await using LinkTestClient second = await hub.WaitForAgentAsync();
        Assert.Equal(MessageTypes.Hello, LinkTestClient.TypeOf(second.Frames[0]));
        await second.SendAsync(FakeHub.HubClientHello());
        JsonElement manifest = await second.RequestAsync(MessageTypes.GetManifest);
        Assert.True(manifest.GetProperty("ok").GetBoolean());
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Connected);
        Assert.Equal(2, hub.Accepted);
        Assert.All(hub.Presented, presented => Assert.Equal(credential, presented));
        Assert.Equal(1, hub.EnrollCalls);
    }

    [Fact]
    public async Task The_agent_keeps_trying_while_the_hub_is_down_and_comes_back_when_it_is_up()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient first = await HubLinkRig.JoinAndGreetAsync(hub, host);

        await hub.StopAsync();
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Disconnected);
        await Task.Delay(200);
        await hub.StartAgainAsync();

        await using LinkTestClient second = await hub.WaitForAgentAsync();
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Connected);
        Assert.NotNull(await HubLinkRig.ManagerOf(host).GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_hub_certificate_that_does_not_match_the_pinned_fingerprint_is_refused_at_enrollment()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubLinkManager manager = HubLinkRig.ManagerOf(host);

        HubJoinResult joined = await manager.JoinAsync(hub.EnrollmentString(fingerprint: HubLinkRig.WrongFingerprint()), CancellationToken.None);

        Assert.False(joined.Joined);
        Assert.Equal(HubJoinFailure.Unreachable, joined.Failure);
        Assert.Contains("fingerprint", joined.Error, StringComparison.OrdinalIgnoreCase);

        // The TLS handshake failed: no request, and in particular no token, reached the hub.
        Assert.Equal(0, hub.EnrollCalls);
        Assert.Null(await manager.GetStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_hub_certificate_that_does_not_match_the_pinned_fingerprint_is_refused_on_the_link()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubStateStore store = host.Agent.Services.GetRequiredService<HubStateStore>();
        await using LinkTestClient first = await HubLinkRig.JoinAndGreetAsync(hub, host);
        Assert.Equal(1, hub.Accepted);

        // Somebody else answers on the address of the hub: its certificate is not the pinned one.
        _ = await store.UpdateAsync(current => current is null ? null : current with { Fingerprint = HubLinkRig.WrongFingerprint() }, CancellationToken.None);
        await first.CloseAsync();

        HubLinkStatus status = await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Disconnected);
        await Task.Delay(300);
        Assert.Equal(1, hub.Accepted);
        status = await HubLinkRig.ServiceOf(host).GetStatusAsync(CancellationToken.None);
        Assert.Equal(HubLinkStates.Disconnected, status.State);
        Assert.Contains("certificate", status.Detail, StringComparison.OrdinalIgnoreCase);

        // The agent still holds its credential: the certificate is the problem, not the credential, and a revocation is the only reason to delete it.
        Assert.NotNull((await store.LoadAsync(CancellationToken.None))!.Credential);
    }

    [Fact]
    public async Task A_hub_without_a_pinned_fingerprint_is_checked_by_the_system_and_a_self_signed_certificate_fails()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();

        HubJoinResult joined = await HubLinkRig.ManagerOf(host).JoinAsync(hub.EnrollmentString(pin: false), CancellationToken.None);

        Assert.False(joined.Joined);
        Assert.Equal(HubJoinFailure.Unreachable, joined.Failure);
        Assert.Equal(0, hub.EnrollCalls);
    }

    [Fact]
    public async Task A_rotated_credential_is_stored_before_the_agent_answers_and_is_used_on_the_next_connection()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient first = await HubLinkRig.JoinAndGreetAsync(hub, host);
        HubLinkManager manager = HubLinkRig.ManagerOf(host);
        string rotated = FakeHub.NewSecret();

        JsonElement answer = await first.RequestAsync(HubMessageTypes.CredentialRotate, "\"credential\":\"" + rotated + "\"");

        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(rotated, (await manager.GetStateAsync(CancellationToken.None))!.Credential);
        Assert.True(first.IsOpen);

        // The hub accepts the new credential, then the link drops; the agent dials with the new one.
        hub.AddCredential(rotated);
        await first.CloseAsync();
        await using LinkTestClient second = await hub.WaitForAgentAsync();
        Assert.Equal(rotated, hub.Presented[^1]);
        Assert.Empty(first.Problems);
    }

    [Fact]
    public async Task A_credential_that_is_not_valid_is_not_stored_and_the_agent_says_so()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await HubLinkRig.JoinAndGreetAsync(hub, host);
        HubLinkManager manager = HubLinkRig.ManagerOf(host);
        string before = hub.LastCredential!;

        JsonElement answer = await peer.RequestAsync(HubMessageTypes.CredentialRotate, "\"credential\":\"short\"");

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(before, (await manager.GetStateAsync(CancellationToken.None))!.Credential);
        Assert.True(peer.IsOpen);
    }

    [Fact]
    public async Task Unenroll_deletes_the_credential_and_the_hub_state_and_the_agent_stops_dialling()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await HubLinkRig.JoinAndGreetAsync(hub, host);
        ISecretStore secrets = host.Agent.Services.GetRequiredService<ISecretStore>();
        string credential = hub.LastCredential!;

        await peer.SendAsync("{\"t\":\"unenroll\",\"reason\":\"removed by test\"}");

        int? closeStatus = await peer.WaitForCloseAsync();
        Assert.True(closeStatus is null or CloseCodes.Normal, $"Unexpected close status: {closeStatus}");
        HubLinkStatus status = await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.NotJoined);
        Assert.Contains("removed", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await HubLinkRig.ManagerOf(host).GetStateAsync(CancellationToken.None));
        Assert.Null(await secrets.GetStringAsync(HubStateStore.StateSecret, CancellationToken.None));
        await HubLinkRig.AssertNoMoreDialsAsync(hub);
        Assert.DoesNotContain(credential, status.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_revoked_answer_the_agent_deletes_its_credential_and_stops_connecting()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient first = await HubLinkRig.JoinAndGreetAsync(hub, host);
        ISecretStore secrets = host.Agent.Services.GetRequiredService<ISecretStore>();
        HubLinkManager manager = HubLinkRig.ManagerOf(host);
        string credential = hub.LastCredential!;

        // An administrator removed the agent while the link was down: the next dial is answered with 401 and the code revoked.
        hub.Revoke(credential);
        await first.CloseAsync();

        HubLinkStatus status = await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Revoked);
        HubStoredState state = (await manager.GetStateAsync(CancellationToken.None))!;
        Assert.Equal(HubPhases.Revoked, state.Phase);
        Assert.Null(state.Credential);
        Assert.Null(state.Token);
        Assert.DoesNotContain(credential, await secrets.GetStringAsync(HubStateStore.StateSecret, CancellationToken.None) ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(status.Detail);
        Assert.Equal(1, hub.Accepted);
        await HubLinkRig.AssertNoMoreDialsAsync(hub);

        // Joining again with a new token is possible and brings the agent back.
        hub.ValidToken = FakeHub.NewSecret();
        HubJoinResult joined = await manager.JoinAsync(hub.EnrollmentString(), CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);
        await using LinkTestClient second = await hub.WaitForAgentAsync();
        Assert.Equal(hub.LastCredential, hub.Presented[^1]);
        Assert.NotEqual(credential, hub.LastCredential);
    }

    [Fact]
    public async Task A_credential_that_the_hub_does_not_know_is_not_deleted_because_only_a_revocation_may_delete_it()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient first = await HubLinkRig.JoinAndGreetAsync(hub, host);
        string credential = hub.LastCredential!;

        // The hub lost its database: the credential is unknown (401 unauthorized), not revoked.
        hub.Revoke(credential);
        hub.ForgetRevocations();
        await first.CloseAsync();

        await TestWait.UntilAsync(() => hub.Presented.Length >= 3, "the agent to try again");
        HubStoredState state = (await HubLinkRig.ManagerOf(host).GetStateAsync(CancellationToken.None))!;
        Assert.Equal(HubPhases.Enrolled, state.Phase);
        Assert.Equal(credential, state.Credential);
    }

    [Fact]
    public async Task Leaving_closes_the_link_deletes_the_credential_and_the_agent_does_not_dial_again()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        await using LinkTestClient peer = await HubLinkRig.JoinAndGreetAsync(hub, host);
        HubLinkManager manager = HubLinkRig.ManagerOf(host);

        Assert.True(await manager.LeaveAsync(null, CancellationToken.None));

        await peer.WaitForCloseAsync();
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.NotJoined);
        Assert.Null(await manager.GetStateAsync(CancellationToken.None));
        await HubLinkRig.AssertNoMoreDialsAsync(hub);
        Assert.False(await manager.LeaveAsync(null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.LeaveAsync("keep", CancellationToken.None));
    }

    [Fact]
    public async Task The_management_tunnel_is_announced_only_while_the_local_switch_allows_it()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubLinkManager manager = HubLinkRig.ManagerOf(host);
        await manager.SetManagementTunnelAsync(false, CancellationToken.None);

        await using LinkTestClient withoutTunnel = await HubLinkRig.JoinAndGreetAsync(hub, host);

        JsonElement hello = withoutTunnel.Frames[0];
        Assert.False(hello.TryGetProperty("features", out JsonElement features) && features.EnumerateArray().Any(feature => feature.GetString() == HubFeatures.MgmtTunnel));
        JsonElement refused = await withoutTunnel.RequestAsync(HubMessageTypes.MgmtRequest, "\"method\":\"GET\",\"path\":\"/api/v1/status\"");
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(HubErrorCodes.Forbidden, LinkTestClient.ErrorCodeOf(refused));

        // Switching it on renews the connection, and the next hello announces the feature.
        await manager.SetManagementTunnelAsync(true, CancellationToken.None);
        await using LinkTestClient withTunnel = await hub.WaitForAgentAsync();
        Assert.Contains(HubFeatures.MgmtTunnel, withTunnel.Frames[0].GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
    }

    [Fact]
    public async Task The_status_and_the_management_api_report_and_change_the_hub_link()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HttpClient client = host.Agent.CreateApiClient(https: true);
        string cookie = await host.Agent.SetUpAsync(client);

        using (HttpResponseMessage before = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/status", cookie))
        {
            Assert.Equal(HubLinkStates.NotJoined, await HubStateOfAsync(before));
        }

        using (HttpResponseMessage join = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/hub/join", cookie, json: "{\"enrollment\":\"" + hub.EnrollmentString() + "\"}"))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, join.StatusCode);
            string body = await join.Content.ReadAsStringAsync();
            Assert.DoesNotContain(hub.ValidToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(hub.LastCredential!, body, StringComparison.Ordinal);
        }

        await using LinkTestClient peer = await hub.WaitForAgentAsync();
        await peer.SendAsync(FakeHub.HubClientHello());
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.Connected);
        using (HttpResponseMessage status = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/status", cookie))
        {
            Assert.Equal(HubLinkStates.Connected, await HubStateOfAsync(status));
        }

        using (HttpResponseMessage broken = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/hub/join", cookie, json: "{\"enrollment\":\"qahub1.not-valid\"}"))
        {
            // A string that is not valid is refused, and the string, which would hold a token, is not echoed.
            Assert.NotEqual(System.Net.HttpStatusCode.OK, broken.StatusCode);
            Assert.DoesNotContain("qahub1.not-valid", await broken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (HttpResponseMessage leave = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/hub/leave", cookie, json: "{}"))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, leave.StatusCode);
        }

        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.NotJoined);
    }

    private static async Task<string> HubStateOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("hub").GetProperty("state").GetString()!;
    }

    private static bool FingerprintsAreEqual(string fingerprint, string? stored)
    {
        return stored is not null && QoffeeAutomat.Core.Security.CertificateFingerprint.AreEqual(fingerprint, stored);
    }

    private static void AssertLaunchedWith(LinkTestHost host, string[] expected)
    {
        QoffeeAutomat.Core.Platform.ProcessLaunchRequest launch = Assert.Single(host.Launcher.Launches);
        Assert.Equal(expected, launch.Arguments.ToArray());
    }
}
