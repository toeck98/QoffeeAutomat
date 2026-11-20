using System.Net;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Actions;

/// <summary>The calls of the fleet actions over HTTP (T917): addresses, anti-forgery header, camel-case JSON, the 202 of a start and the refusals of the hub.</summary>
[Trait("Category", "FleetActions")]
public sealed class FleetActionsClientTests : IDisposable
{
    private const string ActionJson =
        "{\"id\":\"a1\",\"kind\":\"bulk_run\",\"state\":\"running\",\"user\":\"alice\",\"createdAt\":\"2026-11-19T12:00:00+00:00\",\"command\":\"lock\",\"tags\":[]," +
        "\"counts\":{\"pending\":1,\"running\":0,\"success\":0,\"failure\":0,\"offline\":0,\"skipped\":0,\"cancelled\":0}," +
        "\"items\":[{\"agentId\":\"pc-1\",\"agentName\":\"PC 1\",\"state\":\"pending\"}],\"updated\":[]}";

    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private HubApiClient Client(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://hub.example/") };
        _clients.Add(http);
        return new HubApiClient(http);
    }

    [Fact]
    public async Task A_bulk_run_is_posted_with_the_anti_forgery_header_and_camel_case_json_and_the_accepted_action_is_read()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Accepted, ActionJson));
        HubApiClient client = Client(handler);

        FleetActionInfo action = await client.StartBulkRunAsync(new StartBulkRunRequest("lock", null, ["pc-1"], null, 3));

        Assert.Equal("a1", action.Id);
        Assert.Equal(FleetActionKinds.BulkRun, action.Kind);
        Assert.Equal(FleetActionStates.Running, action.State);
        Assert.Equal("pc-1", Assert.Single(action.Items).AgentId);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/fleet-actions/bulk-runs", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"command\":\"lock\",\"agents\":[\"pc-1\"],\"parallelism\":3}", request.Body);
    }

    [Fact]
    public async Task An_update_is_posted_to_its_own_address()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.Accepted, ActionJson));
        HubApiClient client = Client(handler);

        _ = await client.StartFleetUpdateAsync(new StartFleetUpdateRequest(["pc-1", "pc-2"], null, "2.0.0", 300));

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/fleet-actions/updates", request.Uri.AbsolutePath);
        Assert.Equal("{\"agents\":[\"pc-1\",\"pc-2\"],\"targetVersion\":\"2.0.0\",\"timeoutSeconds\":300}", request.Body);
    }

    [Fact]
    public async Task Reading_goes_to_the_versioned_api_without_the_anti_forgery_header()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("fleet-versions", StringComparison.Ordinal)
            ? StubHandler.Json(HttpStatusCode.OK, "{\"latestVersion\":\"2.0.0\",\"agents\":[{\"agentId\":\"pc-1\",\"agentName\":\"PC 1\",\"online\":true,\"version\":\"1.0.0\",\"tags\":[],\"state\":\"outdated\",\"canUpdate\":true}]}")
            : StubHandler.Json(HttpStatusCode.OK, "{\"actions\":[" + ActionJson + "]}"));
        HubApiClient client = Client(handler);

        FleetVersionOverview versions = await client.GetFleetVersionsAsync();
        FleetActionList actions = await client.GetFleetActionsAsync();

        Assert.Equal("2.0.0", versions.LatestVersion);
        Assert.Equal("outdated", Assert.Single(versions.Agents).State);
        Assert.Equal("a1", Assert.Single(actions.Actions).Id);
        Assert.Equal(["/api/v1/fleet-versions", "/api/v1/fleet-actions"], handler.Requests.Select(request => request.Uri.AbsolutePath).ToArray());
        Assert.All(handler.Requests, request => Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName)));
    }

    [Fact]
    public async Task A_cancel_posts_to_the_action_with_its_id_escaped()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, ActionJson));
        HubApiClient client = Client(handler);

        _ = await client.CancelFleetActionAsync("a 1");

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/fleet-actions/a%201/cancel", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
    }

    [Fact]
    public async Task A_refusal_of_the_hub_becomes_an_exception_with_the_code()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            HttpStatusCode.Conflict,
            "{\"type\":\"about:blank\",\"title\":\"Conflict\",\"status\":409,\"detail\":\"An update of agents is running already.\",\"code\":\"update_in_progress\"}",
            "application/problem+json"));
        HubApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.StartFleetUpdateAsync(new StartFleetUpdateRequest(["pc-1"])));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("update_in_progress", exception.Code);
    }
}
