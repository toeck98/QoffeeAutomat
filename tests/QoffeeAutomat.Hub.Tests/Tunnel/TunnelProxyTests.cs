using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Tests.Enrollment;
using QoffeeAutomat.Hub.Tests.Gateway;
using QoffeeAutomat.Hub.Tests.Security;
using QoffeeAutomat.Hub.Tunnel;

namespace QoffeeAutomat.Hub.Tests.Tunnel;

/// <summary>
/// The proxy <c>/api/v1/agents/{id}/agent-api/{rest}</c> of the hub (T912, PLAN.md S20) with an agent that the test plays by hand: what is forwarded, what is answered, and
/// the clear problem responses for an agent that is unknown, offline, without a tunnel, too slow or in error.
/// </summary>
[Trait("Category", "Tunnel")]
public sealed class TunnelProxyTests
{
    private const string AgentId = "scripted-pc";
    private const string Rev = "aaaaaaaaaaaa";

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private sealed class Rig(GatewayRig gateway, ScriptedAgentSocket socket, Task serving) : IAsyncDisposable
    {
        public GatewayRig Gateway { get; } = gateway;

        public ScriptedAgentSocket Socket { get; } = socket;

        public HubTestHost Hub => Gateway.Hub;

        public async ValueTask DisposeAsync()
        {
            Socket.SendClose();
            await serving.WaitAsync(Limit);
            await Gateway.DisposeAsync();
        }

        /// <summary>Plays the agent's answer to the next <c>mgmt_request</c> and returns the frame that the hub sent.</summary>
        public async Task<JsonElement> AnswerRequestAsync(int status, string? body = null)
        {
            JsonElement frame = await Socket.NextFrameOfAsync(HubMessageTypes.MgmtRequest);
            string data = body is null ? "{\"status\":" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" : "{\"status\":" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"body\":" + body + "}";
            Socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(frame), TunnelKit.Json(data)));
            return frame;
        }
    }

    private static async Task<Rig> StartAsync(bool announce = true, TimeSpan? requestTimeout = null)
    {
        GatewayRig gateway = await GatewayRig.StartAsync(TunnelKit.Options(requestTimeout: requestTimeout), withUsers: true);

        // The agent has to be known to the registry; its link is played by hand.
        TestToken token = await EnrollmentKit.CreateTokenAsync(gateway.Hub);
        _ = await EnrollmentKit.EnrollForCredentialAsync(gateway.Hub, token.Secret, AgentId);
        var socket = new ScriptedAgentSocket();
        Task serving = gateway.Gateway.ServeAsync(socket, ScriptedAgent.Record(AgentId), CancellationToken.None);
        socket.SendText(announce ? ScriptedAgent.Hello(AgentId, Rev, 1, 1, HubFeatures.MgmtTunnel) : ScriptedAgent.Hello(AgentId, Rev));
        _ = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        await TestWait.UntilAsync(() => gateway.Gateway.GetPresence(AgentId).Online, "the agent to be online");
        JsonElement manifest = await socket.NextFrameOfAsync(MessageTypes.GetManifest);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(manifest), ScriptedAgent.ManifestOf(AgentId, Rev, "cpu.load")));
        return new Rig(gateway, socket, serving);
    }

    private static string Url(string path) => TunnelKit.AgentApi(AgentId, path);

    private const string Problem403 = "{\"type\":\"about:blank\",\"title\":\"Forbidden\",\"status\":403,\"detail\":\"The role viewer may only read.\",\"code\":\"forbidden\"}";

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_reads_and_the_agent_receives_the_method_the_path_the_query_and_the_user_with_the_role()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Viewer.GetAsync(Url("commands?enabled=true&limit=50"));
        JsonElement frame = await rig.AnswerRequestAsync(200, "{\"items\":[{\"id\":\"set_level\"}]}");
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        Assert.Equal("GET", frame.GetProperty("method").GetString());
        Assert.Equal("/api/v1/commands", frame.GetProperty("path").GetString());
        Assert.Equal("true", frame.GetProperty("query").GetProperty("enabled").GetString());
        Assert.Equal("50", frame.GetProperty("query").GetProperty("limit").GetString());
        Assert.Equal(HubTestHost.ViewerName, frame.GetProperty("principal").GetProperty("user").GetString());
        Assert.Equal("viewer", frame.GetProperty("principal").GetProperty("role").GetString());
        Assert.False(frame.TryGetProperty("body", out _));
        JsonNode body = await HubAssert.JsonAsync(response);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("set_level", body["items"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_operator_can_post_a_run_and_the_body_and_the_answer_status_are_passed_on()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Operator.PostAsync(Url("commands/set_level/run"), "{\"vars\":{\"Level\":3},\"wait\":true}");
        JsonElement frame = await rig.AnswerRequestAsync(202, "{\"runId\":\"r1\",\"command\":\"set_level\",\"source\":\"hub\"}");
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        Assert.Equal("POST", frame.GetProperty("method").GetString());
        Assert.Equal("/api/v1/commands/set_level/run", frame.GetProperty("path").GetString());
        Assert.Equal(3, frame.GetProperty("body").GetProperty("vars").GetProperty("Level").GetInt32());
        Assert.True(frame.GetProperty("body").GetProperty("wait").GetBoolean());
        Assert.Equal(HubTestHost.OperatorName, frame.GetProperty("principal").GetProperty("user").GetString());
        Assert.Equal("operator", frame.GetProperty("principal").GetProperty("role").GetString());
        JsonNode body = await HubAssert.JsonAsync(response, HttpStatusCode.Accepted);
        Assert.Equal("r1", body["runId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_administrator_is_forwarded_with_the_role_admin()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Admin.SendAsync(HttpMethod.Put, Url("commands/set_level"), "{\"id\":\"set_level\"}");
        JsonElement frame = await rig.AnswerRequestAsync(200, "{}");
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        Assert.Equal("PUT", frame.GetProperty("method").GetString());
        Assert.Equal("admin", frame.GetProperty("principal").GetProperty("role").GetString());
        HubAssert.Status(response, HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_answer_of_the_agent_with_an_error_status_is_passed_on_as_the_problem_that_it_is()
    {
        await using Rig rig = await StartAsync();

        // The agent has the last word: it refuses an operator who calls a run endpoint when the agent denies it (the hub lets operators through).
        Task<HttpResponseMessage> call = rig.Hub.Operator.SendAsync(HttpMethod.Post, Url("commands/set_level/run"));
        _ = await rig.AnswerRequestAsync(403, Problem403);
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("The role viewer may only read.", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_response_without_a_body_stays_without_a_body()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Admin.SendAsync(HttpMethod.Delete, Url("commands/set_level"));
        _ = await rig.AnswerRequestAsync(204);
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        HubAssert.Status(response, HttpStatusCode.NoContent);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_viewer_cannot_write_and_nothing_reaches_the_agent()
    {
        await using Rig rig = await StartAsync();

        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using HttpResponseMessage response = await rig.Hub.Viewer.SendAsync(method, Url("commands/set_level/run"), method == HttpMethod.Delete ? null : "{}");
            _ = await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }

        // The first request that arrives at the agent is the one that follows, so none of the writes was forwarded.
        Task<HttpResponseMessage> call = rig.Hub.Viewer.GetAsync(Url("commands"));
        JsonElement frame = await rig.AnswerRequestAsync(200, "{}");
        using HttpResponseMessage read = await call.WaitAsync(Limit);
        Assert.Equal("GET", frame.GetProperty("method").GetString());
        HubAssert.Status(read, HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_account_of_the_agent_is_refused_on_the_hub_even_for_an_administrator()
    {
        await using Rig rig = await StartAsync();

        using HttpResponseMessage response = await rig.Hub.Admin.PostAsync(Url("auth/login"), "{}");

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("commands%2F..%2F..%2Fhealthz")]
    [InlineData("%2e%2e/healthz")]
    [InlineData("commands/%2e%2e/%2e%2e/healthz")]
    public async Task A_path_that_tries_to_leave_api_v1_is_a_bad_request(string rest)
    {
        await using Rig rig = await StartAsync();

        using HttpResponseMessage response = await rig.Hub.Admin.GetAsync(Url(rest));

        // Whatever the server does with the escapes, the path never reaches the agent: a bad request, or (for a path the router already normalised away from the route) not found.
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, "Got " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request()
    {
        await using Rig rig = await StartAsync();

        using HttpResponseMessage response = await rig.Hub.Operator.PostAsync(Url("commands/set_level/run"), "this is not json");

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task A_body_that_is_too_large_is_refused_before_it_is_sent()
    {
        await using Rig rig = await StartAsync();

        using HttpResponseMessage response = await rig.Hub.Operator.PostAsync(Url("commands/set_level/run"), "{\"x\":\"" + new string('a', TunnelEndpoints.MaxBodyBytes) + "\"}");

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    // ------------------------------------------------------------------
    // Failures, with their reasons
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_agent_is_not_found()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync(TunnelKit.Options(), withUsers: true);

        using HttpResponseMessage response = await rig.Hub.Viewer.GetAsync(TunnelKit.AgentApi("nobody", "commands"));

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.NotFound, HubErrorCodes.UnknownAgent);
    }

    [Fact]
    public async Task An_agent_that_is_not_connected_answers_503_with_agent_offline()
    {
        await using GatewayRig rig = await GatewayRig.StartAsync(TunnelKit.Options(), withUsers: true);
        TestToken token = await EnrollmentKit.CreateTokenAsync(rig.Hub);
        _ = await EnrollmentKit.EnrollForCredentialAsync(rig.Hub, token.Secret, "sleeping-pc");

        using HttpResponseMessage response = await rig.Hub.Viewer.GetAsync(TunnelKit.AgentApi("sleeping-pc", "commands"));

        JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.ServiceUnavailable, HubErrorCodes.AgentOffline);
        Assert.Contains("not connected", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_with_the_tunnel_switched_off_answers_403_with_tunnel_disabled_and_the_reason()
    {
        await using Rig rig = await StartAsync(announce: false);

        using HttpResponseMessage response = await rig.Hub.Viewer.GetAsync(Url("commands"));

        JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, TunnelProblemCodes.TunnelDisabled);
        Assert.Contains("switched off", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_of_the_agent_because_of_its_switch_is_reported_the_same_way()
    {
        // The switch was turned off a moment ago: the hub still believes in the feature and the agent refuses with forbidden.
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Viewer.GetAsync(Url("commands"));
        JsonElement frame = await rig.Socket.NextFrameOfAsync(HubMessageTypes.MgmtRequest);
        rig.Socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(frame), HubErrorCodes.Forbidden));
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, TunnelProblemCodes.TunnelDisabled);
    }

    [Fact]
    public async Task An_agent_that_does_not_answer_in_time_is_a_504()
    {
        await using Rig rig = await StartAsync(requestTimeout: TimeSpan.FromMilliseconds(300));

        using HttpResponseMessage response = await rig.Hub.Viewer.GetAsync(Url("commands"));

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.GatewayTimeout, ErrorCodes.Timeout);
    }

    [Theory]
    [InlineData(ErrorCodes.BadRequest, HttpStatusCode.BadRequest, "bad_request")]
    [InlineData(ErrorCodes.Busy, HttpStatusCode.ServiceUnavailable, "busy")]
    [InlineData(ErrorCodes.Internal, HttpStatusCode.BadGateway, "agent_error")]
    [InlineData("a_code_of_the_future", HttpStatusCode.BadGateway, "agent_error")]
    public async Task Other_errors_of_the_agent_link_get_their_own_status_and_code(string agentCode, HttpStatusCode status, string code)
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Viewer.GetAsync(Url("commands"));
        JsonElement frame = await rig.Socket.NextFrameOfAsync(HubMessageTypes.MgmtRequest);
        rig.Socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(frame), agentCode));
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        _ = await HubAssert.ProblemAsync(response, status, code);
    }

    [Fact]
    public async Task A_result_with_a_status_that_is_not_http_is_a_502()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Viewer.GetAsync(Url("commands"));
        _ = await rig.AnswerRequestAsync(999);
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.BadGateway, "agent_error");
    }

    [Fact]
    public async Task Calls_that_change_state_are_audited_with_the_user_the_agent_and_the_path()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Operator.PostAsync(Url("commands/set_level/run"), "{}");
        _ = await rig.AnswerRequestAsync(202, "{}");
        using HttpResponseMessage response = await call.WaitAsync(Limit);
        HubAssert.Status(response, HttpStatusCode.Accepted);

        using HttpResponseMessage audit = await rig.Hub.Admin.GetAsync("/api/v1/audit");
        JsonNode page = await HubAssert.JsonAsync(audit);
        JsonNode? entry = page["entries"]!.AsArray().FirstOrDefault(candidate => candidate!["action"]!.GetValue<string>() == "tunnelPost");
        Assert.NotNull(entry);
        Assert.Equal(HubTestHost.OperatorName, entry["user"]!.GetValue<string>());
        Assert.Equal("operator", entry["role"]!.GetValue<string>());
        Assert.Equal(AgentId, entry["agents"]![0]!.GetValue<string>());
        Assert.Contains("/api/v1/commands/set_level/run", entry["target"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("success", entry["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tunnel_needs_a_session_or_a_token()
    {
        await using Rig rig = await StartAsync();

        using HttpResponseMessage response = await rig.Hub.Anonymous.GetAsync(Url("commands"));

        HubAssert.Status(response, HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------
    // Event streams
    // ------------------------------------------------------------------

    private static async Task<JsonElement> AnswerOpenAsync(Rig rig, string sid)
    {
        JsonElement open = await rig.Socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamOpen);
        rig.Socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(open), TunnelKit.Json("{\"sid\":\"" + sid + "\"}")));
        return open;
    }

    private static string StreamEvent(string sid, string name, string data)
    {
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["t"] = HubMessageTypes.MgmtStreamData, ["sid"] = sid, ["event"] = name, ["data"] = data });
    }

    [Fact]
    public async Task A_get_that_accepts_event_streams_forwards_the_events_to_the_browser_as_they_arrive()
    {
        await using Rig rig = await StartAsync();

        Task<SseClient> opening = SseClient.OpenAsync(rig.Hub.Viewer, Url("runs/r1/stream?from=start"));
        JsonElement open = await AnswerOpenAsync(rig, "s1");
        await using SseClient browser = await opening.WaitAsync(Limit);

        Assert.Equal("text/event-stream", browser.MediaType);
        Assert.Equal("/api/v1/runs/r1/stream", open.GetProperty("path").GetString());
        Assert.Equal("start", open.GetProperty("query").GetProperty("from").GetString());
        Assert.Equal(HubTestHost.ViewerName, open.GetProperty("principal").GetProperty("user").GetString());

        // The first event arrives while the stream is open and the command (on the agent) is still running.
        rig.Socket.SendText(StreamEvent("s1", "output", "{\"stream\":\"stdout\",\"text\":\"line one\"}"));
        ReceivedEvent first = (await browser.NextAsync())!.Value;
        Assert.Equal("output", first.Name);
        Assert.Contains("line one", first.Data, StringComparison.Ordinal);

        rig.Socket.SendText(StreamEvent("s1", "output", "{\"stream\":\"stdout\",\"text\":\"line two\"}"));
        rig.Socket.SendText(StreamEvent("s1", "end", "{\"exit_code\":0}"));
        Assert.Contains("line two", (await browser.NextAsync())!.Value.Data, StringComparison.Ordinal);
        Assert.Equal("end", (await browser.NextAsync())!.Value.Name);

        rig.Socket.SendText("{\"t\":\"mgmt_stream_close\",\"sid\":\"s1\"}");
        ReceivedEvent closed = await browser.UntilAsync("tunnel_closed");
        Assert.Contains("completed", closed.Data, StringComparison.Ordinal);
        Assert.Null(await browser.NextAsync());
    }

    [Fact]
    public async Task Data_with_several_lines_stays_one_event()
    {
        await using Rig rig = await StartAsync();
        Task<SseClient> opening = SseClient.OpenAsync(rig.Hub.Viewer, Url("events"));
        _ = await AnswerOpenAsync(rig, "s1");
        await using SseClient browser = await opening.WaitAsync(Limit);

        rig.Socket.SendText(StreamEvent("s1", "log", "first\nsecond"));

        ReceivedEvent item = (await browser.NextAsync())!.Value;
        Assert.Equal("log", item.Name);
        Assert.Equal("first\nsecond", item.Data);
    }

    [Fact]
    public async Task When_the_browser_goes_away_the_agent_is_told_to_close_the_stream()
    {
        await using Rig rig = await StartAsync();
        Task<SseClient> opening = SseClient.OpenAsync(rig.Hub.Viewer, Url("runs/r1/stream"));
        _ = await AnswerOpenAsync(rig, "s9");
        SseClient browser = await opening.WaitAsync(Limit);

        await browser.DisposeAsync();

        JsonElement close = await rig.Socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamClose);
        Assert.Equal("s9", close.GetProperty("sid").GetString());
    }

    [Fact]
    public async Task When_the_agent_is_lost_the_browser_is_told_why_the_stream_ended()
    {
        GatewayRig gateway = await GatewayRig.StartAsync(TunnelKit.Options(), withUsers: true);
        TestToken token = await EnrollmentKit.CreateTokenAsync(gateway.Hub);
        _ = await EnrollmentKit.EnrollForCredentialAsync(gateway.Hub, token.Secret, AgentId);
        var socket = new ScriptedAgentSocket();
        Task serving = gateway.Gateway.ServeAsync(socket, ScriptedAgent.Record(AgentId), CancellationToken.None);
        socket.SendText(ScriptedAgent.Hello(AgentId, Rev, 1, 1, HubFeatures.MgmtTunnel));
        _ = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        await TestWait.UntilAsync(() => gateway.Gateway.GetPresence(AgentId).Online, "the agent to be online");
        await using GatewayRig owner = gateway;

        Task<SseClient> opening = SseClient.OpenAsync(gateway.Hub.Viewer, Url("events"));
        JsonElement open = await socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamOpen);
        socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(open), TunnelKit.Json("{\"sid\":\"s1\"}")));
        await using SseClient browser = await opening.WaitAsync(Limit);

        socket.SendClose();
        await serving.WaitAsync(Limit);

        ReceivedEvent closed = await browser.UntilAsync("tunnel_closed");
        Assert.Contains(StreamEndReasons.AgentOffline, closed.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_that_refuses_to_open_a_stream_gets_its_own_status_back()
    {
        await using Rig rig = await StartAsync();

        Task<HttpResponseMessage> call = rig.Hub.Viewer.SendAsync(HttpMethod.Get, Url("runs/unknown/stream"), null, "text/event-stream");
        JsonElement open = await rig.Socket.NextFrameOfAsync(HubMessageTypes.MgmtStreamOpen);
        rig.Socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(open), ErrorCodes.BadRequest).Replace("failed on purpose", "404: There is no run with this id.", StringComparison.Ordinal));
        using HttpResponseMessage response = await call.WaitAsync(Limit);

        JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.Equal("There is no run with this id.", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stream_of_an_agent_without_the_tunnel_is_refused_like_a_request()
    {
        await using Rig rig = await StartAsync(announce: false);

        using HttpResponseMessage response = await rig.Hub.Viewer.SendAsync(HttpMethod.Get, Url("events"), null, "text/event-stream");

        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.Forbidden, TunnelProblemCodes.TunnelDisabled);
    }
}
