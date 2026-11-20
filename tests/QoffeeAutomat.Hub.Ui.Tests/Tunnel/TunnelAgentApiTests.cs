using System.Net;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Hub.Ui.Tests.Support;
using QoffeeAutomat.Hub.Ui.Tunnel;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Tunnel;

/// <summary>
/// <c>TunnelAgentApi</c> (PLAN.md S20, D-033, T915): the <c>IAgentApi</c> of the component library over the tunnel of the hub. The calls are the ones
/// of the agent UI, so the command run and its live output work for a PC behind the hub as they do on the PC; the route, the anti-forgery header,
/// the errors of the hub and the sign-in are what is different.
/// </summary>
[Trait("Category", "HubUi")]
public sealed class TunnelAgentApiTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }
    }

    private (TunnelAgentApi Api, StubHandler Handler, AgentSelection Selection, FakeHubApi Hub) Create(
        Func<RecordedRequest, HttpResponseMessage> respond,
        string baseAddress = "https://hub.example/",
        string? agent = "pc-1")
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        _clients.Add(http);
        var selection = new AgentSelection();
        if (agent is not null)
        {
            selection.Select(agent);
        }

        var hub = new FakeHubApi();
        return (new TunnelAgentApi(http, selection, hub), handler, selection, hub);
    }

    private static string CommandListJson()
    {
        var command = new CommandDocument(
            DocumentSchema.CurrentVersion,
            "lock_screen",
            "Lock screen",
            true,
            true,
            RunAsValues.User,
            new BuiltinAction("session.lock"),
            "Locks the session.",
            Parameters: null,
            TimeoutSeconds: 60,
            Revision: 1,
            UpdatedAt: Now,
            Origin: DocumentOrigins.Local);
        return JsonSerializer.Serialize(new CommandList([command]), ApiDocumentJsonContext.Default.CommandList);
    }

    [Fact]
    public async Task A_read_goes_through_the_tunnel_route_of_the_selected_agent_without_the_anti_forgery_header()
    {
        (TunnelAgentApi api, StubHandler handler, _, _) = Create(_ => StubHandler.Json(HttpStatusCode.OK, CommandListJson()));

        CommandList list = await api.GetCommandsAsync();

        Assert.Equal("lock_screen", Assert.Single(list.Commands).Id);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/agents/pc-1/agent-api/commands", request.Uri.AbsolutePath);
        Assert.False(request.Headers.ContainsKey(ApiHeaders.CsrfName));
    }

    [Fact]
    public async Task The_agent_is_read_at_every_call_and_its_id_is_escaped()
    {
        (TunnelAgentApi api, StubHandler handler, AgentSelection selection, _) = Create(
            _ => StubHandler.Json(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"Not Found\",\"status\":404,\"code\":\"not_found\"}", "application/problem+json"),
            agent: "pc 1/x");

        // The answers are refusals, which is not what this test is about: the requests are.
        await Assert.ThrowsAsync<ApiException>(() => api.GetStatusAsync());
        selection.Select("pc-2");
        await Assert.ThrowsAsync<ApiException>(() => api.GetStatusAsync());

        Assert.Equal("/api/v1/agents/pc%201%2Fx/agent-api/status", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/v1/agents/pc-2/agent-api/status", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_command_is_run_through_the_tunnel_with_the_anti_forgery_header_and_its_variables()
    {
        (TunnelAgentApi api, StubHandler handler, _, _) = Create(
            _ => StubHandler.Json(HttpStatusCode.Accepted, "{\"runId\":\"run-1\",\"command\":\"lock_screen\",\"source\":\"hub\"}"));

        RunStarted started = await api.StartRunAsync(
            "lock_screen",
            new Dictionary<string, JsonElement> { ["seconds"] = JsonSerializer.SerializeToElement(5) });

        Assert.Equal("run-1", started.RunId);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/agents/pc-1/agent-api/commands/lock_screen/run", request.Uri.AbsolutePath);
        Assert.Equal("1", request.Headers[ApiHeaders.CsrfName]);
        Assert.Contains("\"seconds\":5", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_live_output_of_a_run_arrives_through_the_event_stream_of_the_tunnel()
    {
        string end = JsonSerializer.Serialize(new RunEndEvent("run-1", true, 0, null, null, 12, false), ApiJsonContext.Default.RunEndEvent);
        string output = JsonSerializer.Serialize(new RunOutputEvent("stdout", "hello\n", Now), ApiJsonContext.Default.RunOutputEvent);
        string body = "event: output\ndata: " + output + "\n\n"
            + "event: end\ndata: " + end + "\n\n"
            + "event: tunnel_closed\ndata: {\"reason\":\"completed\"}\n\n";
        (TunnelAgentApi api, StubHandler handler, _, _) = Create(_ => StubHandler.EventStream(body));

        var items = new List<RunStreamItem>();
        await foreach (RunStreamItem item in api.StreamRunAsync("run-1"))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
        Assert.Equal("hello\n", items[0].Output!.Text);
        Assert.True(items[1].End!.Succeeded);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/agents/pc-1/agent-api/runs/run-1/stream", request.Uri.AbsolutePath);
        Assert.Contains("text/event-stream", request.Accept, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(503, "agent_offline")]
    [InlineData(403, "tunnel_disabled")]
    [InlineData(404, "unknown_agent")]
    [InlineData(504, "timeout")]
    public async Task The_problems_of_the_tunnel_become_api_exceptions_with_their_code(int status, string code)
    {
        string problem = "{\"type\":\"about:blank\",\"title\":\"x\",\"status\":" + status + ",\"detail\":\"Detail.\",\"code\":\"" + code + "\"}";
        (TunnelAgentApi api, _, _, _) = Create(_ => StubHandler.Json((HttpStatusCode)status, problem, "application/problem+json"));

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() => api.GetCommandsAsync());

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task A_call_without_a_selected_agent_fails_before_anything_is_sent()
    {
        (TunnelAgentApi api, StubHandler handler, _, _) = Create(_ => StubHandler.Json(HttpStatusCode.OK, "{}"), agent: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetCommandsAsync());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_sign_in_is_the_one_of_the_hub_and_the_agent_is_never_asked_for_it()
    {
        (TunnelAgentApi api, StubHandler handler, _, FakeHubApi hub) = Create(_ => StubHandler.Json(HttpStatusCode.OK, "{}"));
        hub.Me = Samples.Session("operator", "bob");

        AuthMeResponse? me = await api.GetMeAsync();
        SetupState? setup = await api.GetSetupStateAsync();
        hub.Me = null;
        AuthMeResponse? gone = await api.GetMeAsync();
        await api.LogOutAsync();

        Assert.Equal("bob", me!.Name);
        Assert.Null(setup);
        Assert.Null(gone);
        Assert.Equal(1, hub.LogOutCalls);
        Assert.Empty(handler.Requests);
        await Assert.ThrowsAsync<NotSupportedException>(() => api.LogInAsync(new LoginRequest("pw")));
        await Assert.ThrowsAsync<NotSupportedException>(() => api.SetUpAsync(new SetupRequest("token", "pw")));
    }

    [Fact]
    public async Task The_route_stays_below_a_path_prefix_such_as_home_assistant_ingress()
    {
        (TunnelAgentApi api, StubHandler handler, _, _) = Create(
            _ => StubHandler.Json(HttpStatusCode.OK, CommandListJson()),
            baseAddress: "https://ha.example/api/hassio_ingress/Zk3x/");

        await api.GetCommandsAsync();

        Assert.Equal("/api/hassio_ingress/Zk3x/api/v1/agents/pc-1/agent-api/commands", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public void The_route_of_an_agent_is_a_relative_address_below_the_api()
    {
        Assert.Equal("api/v1/agents/pc-1/agent-api/", TunnelAgentApi.RouteOf("pc-1"));
        Assert.Equal("api/v1/agents/a%2Fb/agent-api/", TunnelAgentApi.RouteOf("a/b"));
        Assert.Throws<ArgumentException>(() => TunnelAgentApi.RouteOf(string.Empty));
    }
}
