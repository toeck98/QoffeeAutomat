using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Tests.Api;
using QoffeeAutomat.Agent.Tests.Link;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Hub.ConfigSync;

/// <summary>The policy of the hub on the agent (PLAN.md S20, D-029, T908): the lock of the local configuration, the read-only hub documents and the denial of direct links.</summary>
[Trait("Category", "ConfigSync")]
public sealed class PolicyEnforcementTests : IDisposable
{
    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    private static async Task ApplyAsync(IConfigStore store, long generation, PolicyInfo policy, params HubDocument[] documents)
    {
        HubApplyResult result = await store.Hub.ApplyAsync(HubDocs.Generation(generation, policy, documents), CancellationToken.None);
        Assert.True(result.Applied, result.Error);
    }

    [Fact]
    public async Task With_the_local_configuration_locked_every_write_to_commands_sensors_scripts_and_metric_overrides_is_rejected()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await ApplyAsync(host.Service<IConfigStore>(), 1, HubDocs.Locked, HubDocs.Command("hub_cmd"));
        const string locked = "local_config_locked";

        await Refused(host, HttpMethod.Post, "/api/v1/commands", ApiSamples.Command("mine"), locked);
        await Refused(host, HttpMethod.Put, "/api/v1/commands/mine", ApiSamples.Command("mine"), locked);
        await Refused(host, HttpMethod.Patch, "/api/v1/commands/mine", "{\"enabled\":false}", locked);
        await Refused(host, HttpMethod.Delete, "/api/v1/commands/mine", null, locked);
        await Refused(host, HttpMethod.Post, "/api/v1/sensors", ApiSamples.Sensor("mine_sensor", "tool"), locked);
        await Refused(host, HttpMethod.Put, "/api/v1/sensors/mine_sensor", ApiSamples.Sensor("mine_sensor", "tool"), locked);
        await Refused(host, HttpMethod.Delete, "/api/v1/sensors/mine_sensor", null, locked);
        await Refused(host, HttpMethod.Put, "/api/v1/scripts/mine.ps1", "{\"content\":\"x\"}", locked);
        await Refused(host, HttpMethod.Delete, "/api/v1/scripts/mine.ps1", null, locked);
        await Refused(host, HttpMethod.Put, "/api/v1/metrics/cpu.usage", "{\"intervalSeconds\":5}", locked);
        await Refused(host, HttpMethod.Delete, "/api/v1/metrics/cpu.usage", null, locked);
        await Refused(host, HttpMethod.Post, "/api/v1/config/revisions/command/mine/1/restore", null, locked);
        await Refused(host, HttpMethod.Post, "/api/v1/templates/anything/instantiate", null, locked);

        // Nothing was written, whatever the endpoint.
        Assert.Empty(host.Service<IConfigStore>().Local.List<CommandDocument>());
        Assert.Empty(host.Service<IConfigStore>().Local.List<SensorDocument>());

        // Viewing stays possible, and so do the hub endpoints (leaving the hub is one of them).
        using HttpResponseMessage listed = await host.GetAsync("/api/v1/commands");
        JsonNode list = await ApiAssert.JsonAsync(listed);
        Assert.Contains(list["commands"]!.AsArray(), command => command!["id"]!.GetValue<string>() == "hub_cmd");
        using HttpResponseMessage hub = await host.GetAsync("/api/v1/hub");
        JsonNode status = await ApiAssert.JsonAsync(hub);
        Assert.True(status["localConfigLocked"]!.GetValue<bool>());
        Assert.Equal(1, status["configGeneration"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_hub_document_is_read_only_through_the_api_and_shows_the_origin_hub()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await ApplyAsync(
            host.Service<IConfigStore>(),
            1,
            HubDocs.Open,
            HubDocs.Command("hub_cmd"),
            HubDocs.Sensor("hub_sensor"),
            HubDocs.Script("hub.ps1", "text\n"),
            HubDocs.MetricOverrides());
        const string readOnly = "hub_document";

        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/hub_cmd");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal("hub", command["origin"]!.GetValue<string>());

        await Refused(host, HttpMethod.Put, "/api/v1/commands/hub_cmd", ApiSamples.Command("hub_cmd"), readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Patch, "/api/v1/commands/hub_cmd", "{\"enabled\":false}", readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Delete, "/api/v1/commands/hub_cmd", null, readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Put, "/api/v1/sensors/hub_sensor", ApiSamples.Sensor("hub_sensor", "tool"), readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Delete, "/api/v1/sensors/hub_sensor", null, readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Put, "/api/v1/scripts/hub.ps1", "{\"content\":\"changed\"}", readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Delete, "/api/v1/scripts/hub.ps1", null, readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Put, "/api/v1/metrics/cpu.usage", "{\"intervalSeconds\":5}", readOnly, HttpStatusCode.Conflict);
        await Refused(host, HttpMethod.Delete, "/api/v1/metrics/cpu.usage", null, readOnly, HttpStatusCode.Conflict);

        // The hub documents are unchanged.
        IConfigStore store = host.Service<IConfigStore>();
        Assert.Equal("Hub command", store.Effective.GetDocument<CommandDocument>("hub_cmd")!.Name);
        Assert.Equal("text\n", (await store.Effective.GetScriptAsync("hub.ps1", CancellationToken.None))!.Content);

        // Everything that is not a hub document can still be created, because the policy allows local configuration.
        using HttpResponseMessage created = await host.PostAsync("/api/v1/commands", ApiSamples.Command("mine"));
        await ApiAssert.JsonAsync(created, HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_hub_config_endpoint_reports_the_policy_the_documents_the_shadowed_local_documents_and_the_names_of_the_variables()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        IConfigStore store = host.Service<IConfigStore>();
        await HubDocs.SaveLocalCommandAsync(store, "shared", "Local version");
        await ApplyAsync(
            store,
            7,
            new PolicyInfo(LocalConfigPolicies.Allow, DirectLinkPolicies.Deny),
            HubDocs.Command("shared", "Hub version"),
            HubDocs.Variables("site", "SITE_NAME", "secret value"));

        using HttpResponseMessage response = await host.GetAsync("/api/v1/hub/config");
        string body = await response.Content.ReadAsStringAsync();
        JsonNode state = JsonNode.Parse(body)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(state["active"]!.GetValue<bool>());
        Assert.Equal(7, state["generation"]!.GetValue<long>());
        Assert.Equal("allow", state["policy"]!["localConfig"]!.GetValue<string>());
        Assert.Equal("deny", state["policy"]!["directLink"]!.GetValue<string>());
        Assert.False(state["localConfigLocked"]!.GetValue<bool>());
        Assert.True(state["directLinkDenied"]!.GetValue<bool>());
        Assert.Contains(state["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == "shared" && item["state"]!.GetValue<string>() == "active");
        JsonNode shadowed = Assert.Single(state["shadowed"]!.AsArray())!;
        Assert.Equal("shared", shadowed["id"]!.GetValue<string>());
        Assert.Equal("hub_document", shadowed["reason"]!.GetValue<string>());
        Assert.Equal("SITE_NAME", Assert.Single(state["variables"]!.AsArray())!.GetValue<string>());

        // The values of the variables are not shown.
        Assert.DoesNotContain("secret value", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_hub_the_hub_config_endpoint_reports_nothing_and_nothing_is_locked()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/v1/hub/config");
        JsonNode state = await ApiAssert.JsonAsync(response);

        Assert.False(state["active"]!.GetValue<bool>());
        Assert.Equal(0, state["generation"]!.GetValue<long>());
        Assert.False(state["localConfigLocked"]!.GetValue<bool>());
        Assert.False(state["directLinkDenied"]!.GetValue<bool>());
        Assert.Empty(state["items"]!.AsArray());
        Assert.Empty(state["shadowed"]!.AsArray());
        using HttpResponseMessage created = await host.PostAsync("/api/v1/commands", ApiSamples.Command("mine"));
        await ApiAssert.JsonAsync(created, HttpStatusCode.Created);
    }

    [Fact]
    public async Task With_direct_links_denied_pairing_is_refused_and_connected_direct_clients_are_closed_and_an_allow_brings_pairing_back()
    {
        await using LinkTestHost host = new();
        using HttpClient api = host.Agent.CreateApiClient();
        using HttpClient link = host.Agent.CreateApiClient(https: true);
        string cookie = await host.Agent.SetUpAsync(api);
        (string token, _) = await host.PairAsync();
        await using LinkTestClient direct = await LinkTestClient.ConnectAndGreetAsync(host.Agent, token);
        string openCode = host.Pairing.StartPairing().Code;

        await ApplyAsync(host.Config, 1, HubDocs.DirectDenied);

        // The direct client is closed, with the reason in the close frame, and an open pairing code is cancelled.
        int? closeCode = await direct.WaitForCloseAsync();
        Assert.Equal((int?)CloseCodes.Normal, closeCode);
        Assert.Equal("direct_link_denied", direct.CloseDescription);
        Assert.Null(host.Pairing.ActivePairing());

        // New pairing is refused wherever it starts: in the UI, with the code that was shown, and with a token that was valid before.
        using HttpResponseMessage started = await TestAgent.SendAsync(api, HttpMethod.Post, "/api/v1/link/pairing", cookie);
        await ApiAssert.ProblemAsync(started, HttpStatusCode.Forbidden, "direct_link_denied");
        string pairBody = "{\"code\":\"" + openCode + "\",\"client\":{\"kind\":\"homeassistant\",\"name\":\"Late\"}}";
        using HttpResponseMessage paired = await TestAgent.SendAsync(link, HttpMethod.Post, ProtocolInfo.PairPath, csrf: false, json: pairBody);
        await ApiAssert.ProblemAsync(paired, HttpStatusCode.Forbidden, "direct_link_denied");
        using var connect = new HttpRequestMessage(HttpMethod.Get, ProtocolInfo.LinkPath);
        connect.Headers.Add("Authorization", "Bearer " + token);
        using HttpResponseMessage refused = await link.SendAsync(connect);
        await ApiAssert.ProblemAsync(refused, HttpStatusCode.Forbidden, "direct_link_denied");
        Assert.Single(await host.Pairing.ListClientsAsync(CancellationToken.None));

        // The hub allows direct links again: pairing works.
        await ApplyAsync(host.Config, 2, HubDocs.Open);
        using HttpResponseMessage again = await TestAgent.SendAsync(api, HttpMethod.Post, "/api/v1/link/pairing", cookie);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task The_zeroconf_advertisement_is_withdrawn_while_the_hub_denies_direct_links_and_comes_back_with_the_allow()
    {
        await using var agent = new TestAgent(_data.Path);
        SimulatedServiceAdvertiser recorder = agent.Services.GetRequiredService<SimulatedPlatform>().Advertiser;
        IConfigStore store = agent.Services.GetRequiredService<IConfigStore>();
        await TestWait.UntilAsync(() => recorder.Current is not null, "the announcement");

        await ApplyAsync(store, 1, HubDocs.DirectDenied);
        await TestWait.UntilAsync(() => recorder.Current is null, "the advertisement to be withdrawn");

        await ApplyAsync(store, 2, HubDocs.Open);
        await TestWait.UntilAsync(() => recorder.Current is not null, "the advertisement to come back");
    }

    [Fact]
    public async Task The_policy_holds_after_a_restart_while_the_hub_is_unreachable()
    {
        await using (var agent = new TestAgent(_data.Path))
        {
            await agent.Services.GetRequiredService<HubStateStore>().SaveAsync(
                new HubStoredState(HubPhases.Enrolled, "https://127.0.0.1:1", "hub-key", null, "hub-1", "Hub", Credential: "cred"),
                CancellationToken.None);
            await ApplyAsync(agent.Services.GetRequiredService<IConfigStore>(), 4, new PolicyInfo(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny), HubDocs.Command("hub_cmd"));
        }

        // The agent starts again: nothing connects to a hub, and the last applied generation is in force.
        await using var restarted = new TestAgent(_data.Path);
        IConfigStore store = restarted.Services.GetRequiredService<IConfigStore>();
        using HttpClient api = restarted.CreateApiClient();
        string cookie = await restarted.SetUpAsync(api);

        Assert.Equal(4, store.Hub.State.Generation);
        Assert.True(store.Hub.State.LocalConfigLocked);
        using HttpResponseMessage write = await TestAgent.SendAsync(api, HttpMethod.Post, "/api/v1/commands", cookie, json: ApiSamples.Command("mine"));
        await ApiAssert.ProblemAsync(write, HttpStatusCode.Forbidden, "local_config_locked");
    }

    private static async Task Refused(ApiTestHost host, HttpMethod method, string path, string? json, string code, HttpStatusCode status = HttpStatusCode.Forbidden)
    {
        using HttpResponseMessage response = await host.SendAsync(method, path, json);
        await ApiAssert.ProblemAsync(response, status, code);
    }
}
