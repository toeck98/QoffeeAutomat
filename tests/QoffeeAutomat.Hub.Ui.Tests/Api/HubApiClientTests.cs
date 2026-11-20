using System.Net;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Api;

/// <summary>The HTTP implementation of <c>IHubApi</c> (PLAN.md S08, S09 item 1, S20): cookies and anti-forgery header, camel-case JSON, problem details, prefix-safe addresses.</summary>
[Trait("Category", "HubUi")]
public sealed class HubApiClientTests : IDisposable
{
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private HubApiClient Client(StubHandler handler, string baseAddress = "https://hub.example/")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        _clients.Add(http);
        return new HubApiClient(http);
    }

    [Fact]
    public async Task A_read_goes_to_the_versioned_api_without_the_anti_forgery_header()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"agents\":[]}"));
        HubApiClient client = Client(handler);

        AgentList list = await client.GetAgentsAsync();

        Assert.Empty(list.Agents);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/agents", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task A_request_that_changes_state_carries_the_anti_forgery_header_and_camel_case_json()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"authenticated\":true,\"name\":\"alice\",\"role\":\"admin\",\"method\":\"session\"}"));
        HubApiClient client = Client(handler);

        HubSessionInfo me = await client.LogInAsync(new HubLoginRequest("alice", "secret"));

        Assert.Equal("admin", me.Role);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/auth/login", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Equal("{\"username\":\"alice\",\"password\":\"secret\"}", request.Body);
    }

    [Fact]
    public async Task The_tags_of_an_agent_are_sent_in_a_patch_with_the_id_escaped()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, Json(Samples.Agent("pc-1"))));
        HubApiClient client = Client(handler);

        await client.UpdateAgentTagsAsync("pc 1", ["office", "lab"]);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/v1/agents/pc%201", request.Uri.AbsolutePath);
        Assert.Equal("{\"tags\":[\"office\",\"lab\"]}", request.Body);
    }

    [Fact]
    public async Task A_refusal_becomes_an_api_exception_with_the_problem_of_the_hub()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            HttpStatusCode.Forbidden,
            "{\"type\":\"about:blank\",\"title\":\"Forbidden\",\"status\":403,\"detail\":\"Your role may not do this.\",\"code\":\"forbidden\"}",
            "application/problem+json"));
        HubApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetUsersAsync());

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("forbidden", exception.Code);
        Assert.Equal("Your role may not do this.", exception.Problem!.Detail);
    }

    [Fact]
    public async Task No_session_is_null_for_the_account_and_an_unavailable_setup_is_null_for_the_setup_state()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
            ? StubHandler.Json(HttpStatusCode.Unauthorized, "{\"type\":\"about:blank\",\"title\":\"x\",\"status\":401,\"code\":\"unauthorized\"}", "application/problem+json")
            : StubHandler.Json(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"x\",\"status\":404,\"code\":\"setup_unavailable\"}", "application/problem+json"));
        HubApiClient client = Client(handler);

        Assert.Null(await client.GetMeAsync());
        Assert.Null(await client.GetSetupStateAsync());
    }

    [Fact]
    public async Task An_unreachable_hub_is_a_network_error()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("down"));
        HubApiClient client = Client(handler);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => client.GetAgentsAsync());

        Assert.True(exception.IsNetworkError);
    }

    [Fact]
    public async Task Every_address_stays_below_a_path_prefix_such_as_home_assistant_ingress()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{\"tokens\":[]}"));
        HubApiClient client = Client(handler, "https://ha.example/api/hassio_ingress/Zk3x/");

        await client.GetEnrollmentTokensAsync();
        await client.GetFleetTokensAsync();

        Assert.Equal("/api/hassio_ingress/Zk3x/api/v1/enrollment-tokens", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/hassio_ingress/Zk3x/api/v1/fleet-tokens", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public void The_query_of_the_audit_log_leaves_out_what_is_empty_and_escapes_the_rest()
    {
        Assert.Equal("?limit=100", HubApiClient.AuditQueryString(new AuditQueryParameters()));
        Assert.Equal(
            "?user=token%3Aci&action=createUser&result=denied&agent=pc-1&before=42&limit=50",
            HubApiClient.AuditQueryString(new AuditQueryParameters(" token:ci ", "createUser", "denied", "pc-1", 42, 50)));
        Assert.Equal("?limit=10", HubApiClient.AuditQueryString(new AuditQueryParameters(" ", string.Empty, null, null, null, 10)));
    }

    private static string Json(AgentSummary agent) => System.Text.Json.JsonSerializer.Serialize(agent, HubUiJsonContext.Default.AgentSummary);
}
