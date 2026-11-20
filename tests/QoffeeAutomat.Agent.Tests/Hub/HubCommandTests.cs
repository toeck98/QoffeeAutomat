using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Tests.Link;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Tests.Hub;

/// <summary>
/// The command-line verbs <c>hub join</c>, <c>hub leave</c> and <c>hub status</c> and the channel behind them (PLAN.md S20, T905): the verbs talk to the running agent
/// through a channel whose secret only administrators can read, and never print or send the enrollment string anywhere but to the loopback address of the agent.
/// </summary>
[Trait("Category", "HubLink")]
public sealed class HubCommandTests
{
    private const string Secret = "channel-secret-for-the-test";
    private const string Enrollment = "qahub1.enrollment-with-a-secret-token";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static async Task<string> WriteChannelFileAsync(string directory, string address = "http://127.0.0.1:8480/")
    {
        string path = Path.Combine(directory, HubAdminChannel.FileName);
        var channel = new LocalChannel(Secret);
        channel.SetBaseAddress(new Uri(address));
        await channel.WriteFileAsync(path, files: null, CancellationToken.None);
        return path;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] args, string? file, HttpMessageHandler? handler, string input = "")
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await HubCommand.RunAsync(CommandLine.Parse(args), output, error, new StringReader(input), file, handler);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Hub_status_asks_the_agent_with_the_secret_of_the_channel_and_prints_the_state()
    {
        using var directory = new TempDataDirectory();
        string file = await WriteChannelFileAsync(directory.Path);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"state\":\"connected\",\"managementTunnel\":true,\"hubId\":\"h1\",\"hubName\":\"Home hub\",\"url\":\"https://hub.example.org\"}"));

        (int code, string output, _) = await RunAsync(["hub", "status"], file, handler);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("State: connected", output, StringComparison.Ordinal);
        Assert.Contains("Home hub", output, StringComparison.Ordinal);
        Assert.Contains("Management tunnel: on", output, StringComparison.Ordinal);
        (HttpMethod method, string path, string? authorization, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Equal(HubAdminChannel.BasePath, path);
        Assert.Equal("Bearer " + Secret, authorization);
    }

    [Fact]
    public async Task Hub_join_can_read_the_string_from_standard_input_and_never_prints_it()
    {
        using var directory = new TempDataDirectory();
        string file = await WriteChannelFileAsync(directory.Path);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"state\":\"pending\",\"managementTunnel\":true,\"hubName\":\"Home hub\"}"));

        (int code, string output, string error) = await RunAsync(["hub", "join", "-"], file, handler, Enrollment + "\n");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("approve", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Enrollment, output + error, StringComparison.Ordinal);
        (HttpMethod method, string path, _, string body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(HubAdminChannel.JoinPath, path);
        Assert.Contains(Enrollment, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hub_leave_sends_the_choice_for_the_documents()
    {
        using var directory = new TempDataDirectory();
        string file = await WriteChannelFileAsync(directory.Path);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{\"state\":\"not_joined\",\"managementTunnel\":true}"));

        (int code, string output, _) = await RunAsync(["hub", "leave", "--documents", "convert"], file, handler);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("Left the hub", output, StringComparison.Ordinal);
        Assert.Contains("\"documents\":\"convert\"", Assert.Single(handler.Requests).Body, StringComparison.Ordinal);
        Assert.Equal(HubAdminChannel.LeavePath, handler.Requests[0].Path);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ExitCodes.NoPermission)]
    [InlineData(HttpStatusCode.Forbidden, ExitCodes.NoPermission)]
    [InlineData(HttpStatusCode.BadRequest, ExitCodes.Usage)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ExitCodes.Usage)]
    [InlineData(HttpStatusCode.Conflict, ExitCodes.Failure)]
    [InlineData(HttpStatusCode.BadGateway, ExitCodes.Failure)]
    [InlineData(HttpStatusCode.InternalServerError, ExitCodes.Failure)]
    public async Task The_answers_of_the_agent_become_exit_codes(HttpStatusCode status, int expected)
    {
        using var directory = new TempDataDirectory();
        string file = await WriteChannelFileAsync(directory.Path);
        var handler = new StubHandler(_ => Json(status, "{\"error\":\"nope\"}"));

        (int code, _, string error) = await RunAsync(["hub", "join", Enrollment], file, handler);

        Assert.Equal(expected, code);
        Assert.DoesNotContain(Enrollment, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_running_agent_or_a_readable_channel_file_the_verb_reports_that_no_agent_was_found()
    {
        using var directory = new TempDataDirectory();

        (int code, _, string error) = await RunAsync(["hub", "status"], Path.Combine(directory.Path, "missing.json"), handler: null);

        Assert.Equal(ExitCodes.Unavailable, code);
        Assert.Contains("administrator", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_secret_is_never_sent_to_an_address_that_is_not_the_loopback()
    {
        using var directory = new TempDataDirectory();
        string file = await WriteChannelFileAsync(directory.Path, "http://hub.example.org:8480/");
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));

        (int code, _, string error) = await RunAsync(["hub", "status"], file, handler);

        Assert.Equal(ExitCodes.Failure, code);
        Assert.Empty(handler.Requests);
        Assert.Contains("loopback", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_join_without_a_string_is_a_usage_error()
    {
        (int code, _, string error) = await RunAsync(["hub", "join", "-"], file: null, handler: null, input: "\n");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("enrollment string", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_endpoints_of_the_channel_need_its_own_secret_and_a_loopback_caller()
    {
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubAdminChannel channel = host.Agent.Services.GetRequiredService<HubAdminChannel>();
        string emitSecret = host.Agent.Services.GetRequiredService<LocalChannel>().Token;
        HttpClient client = host.Agent.CreateApiClient();

        async Task<HttpStatusCode> GetAsync(string? bearer, string remote)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HubAdminChannel.BasePath);
            request.Headers.Add(TestAgent.RemoteIpHeader, remote);
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            using HttpResponseMessage response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await GetAsync(channel.Channel.Token, "127.0.0.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(null, "127.0.0.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync("wrong", "127.0.0.1"));

        // The secret of the channel of emit and state is in the environment of every command; it must not open the hub channel.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(emitSecret, "127.0.0.1"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(channel.Channel.Token, "192.0.2.10"));
    }

    [Fact]
    public async Task The_channel_joins_and_leaves_the_hub_of_the_running_agent()
    {
        await using FakeHub hub = await FakeHub.StartAsync();
        await using LinkTestHost host = HubLinkRig.NewAgent();
        HubAdminChannel channel = host.Agent.Services.GetRequiredService<HubAdminChannel>();
        using var directory = new TempDataDirectory();
        string file = Path.Combine(directory.Path, HubAdminChannel.FileName);
        channel.Channel.SetBaseAddress(new Uri("http://127.0.0.1:8480/"));
        await channel.Channel.WriteFileAsync(file, files: null, CancellationToken.None);

        // The handler plays the loopback caller: the test server has no socket, so the address that the agent sees comes from a header.
        using var handler = new LoopbackHandler(host.Agent.Server.CreateHandler());

        (int joinCode, string joinOutput, string joinError) = await RunAsync(["hub", "join", hub.EnrollmentString()], file, handler);
        await using LinkTestClient peer = await hub.WaitForAgentAsync();
        (int statusCode, string statusOutput, _) = await RunAsync(["hub", "status"], file, handler);

        Assert.Equal(ExitCodes.Success, joinCode);
        Assert.Contains("Joined the hub", joinOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(hub.ValidToken, joinOutput + joinError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, statusCode);
        Assert.Contains("State:", statusOutput, StringComparison.Ordinal);

        (int leaveCode, _, _) = await RunAsync(["hub", "leave"], file, handler);

        Assert.Equal(ExitCodes.Success, leaveCode);
        await HubLinkRig.UntilStatusAsync(HubLinkRig.ServiceOf(host), HubLinkStates.NotJoined);
    }

    private sealed class LoopbackHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(TestAgent.RemoteIpHeader, "127.0.0.1");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
