using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>
/// The values of the sensors of type <c>state</c> through the agent (T714): <c>PUT</c> and <c>GET /api/v1/sensors/{id}/value</c>
/// with a session, the authenticated local channel behind the CLI verb <c>state</c>, and the verb itself.
/// </summary>
[Trait("Category", "StateSensor")]
public sealed class StateSensorApiTests
{
    private const string LoopbackAddress = "127.0.0.1";

    private static string StateSensor(string id, string valueType = "number", string? initial = null, bool persist = false, string? options = null)
    {
        string kind = valueType == "bool" ? "binary_sensor" : "sensor";
        string initialPart = initial is null ? string.Empty : ",\"initialValue\":" + initial;
        string persistPart = persist ? ",\"persist\":true" : string.Empty;
        string optionsPart = options is null ? string.Empty : ",\"options\":" + options;
        return $$"""
            {"id":"{{id}}","name":"{{id}}","enabled":true,"exposeToHa":true,"kind":"{{kind}}","valueType":"{{valueType}}","source":{"type":"state"{{initialPart}}{{persistPart}}}{{optionsPart}}}
            """;
    }

    private static async Task CreateAsync(ApiTestHost host, string json)
    {
        using HttpResponseMessage created = await host.PostAsync("/api/v1/sensors", json);
        await ApiAssert.JsonAsync(created, HttpStatusCode.Created);
    }

    // The management API.

    [Fact]
    public async Task A_value_is_set_and_read_back_through_the_api()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level", initial: "1"));

        using HttpResponseMessage initial = await host.GetAsync("/api/v1/sensors/level/value");
        JsonNode initialBody = await ApiAssert.JsonAsync(initial);
        Assert.Equal("level", initialBody["id"]!.GetValue<string>());
        Assert.Equal(1, initialBody["value"]!.GetValue<double>());
        Assert.Null(initialBody["updatedAt"]);

        using HttpResponseMessage set = await host.PutAsync("/api/v1/sensors/level/value", "{\"value\":42}");
        JsonNode setBody = await ApiAssert.JsonAsync(set);
        Assert.Equal(42, setBody["value"]!.GetValue<double>());
        Assert.NotNull(setBody["updatedAt"]);

        // A string is read the way the command line gives it.
        using HttpResponseMessage text = await host.PutAsync("/api/v1/sensors/level/value", "{\"value\":\"43.5\"}");
        Assert.Equal(43.5, (await ApiAssert.JsonAsync(text))["value"]!.GetValue<double>());

        using HttpResponseMessage read = await host.GetAsync("/api/v1/sensors/level/value");
        JsonNode readBody = await ApiAssert.JsonAsync(read);
        Assert.Equal(43.5, readBody["value"]!.GetValue<double>());
        Assert.NotNull(readBody["updatedAt"]);
    }

    [Fact]
    public async Task A_sensor_without_an_initial_value_has_no_value_until_one_is_set()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        using HttpResponseMessage read = await host.GetAsync("/api/v1/sensors/level/value");

        JsonNode body = await ApiAssert.JsonAsync(read);
        Assert.Null(body["value"]);
        Assert.Null(body["updatedAt"]);
    }

    [Fact]
    public async Task A_new_value_reaches_the_subscribers_of_the_metric_at_once()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        MetricEngine metrics = host.Service<MetricEngine>();
        await CreateAsync(host, StateSensor("mode", "enum", initial: "\"Normal\"", options: "[\"Normal\",\"Eco\"]"));
        await TestWait.UntilAsync(() => metrics.Catalog.Any(entry => entry.Id == "custom.mode"), "the sensor in the metric catalog");
        Assert.Equal(0, metrics.Catalog.Single(entry => entry.Id == "custom.mode").IntervalSeconds);
        using MetricSubscription subscription = metrics.Subscribe(MetricSelection.From(["custom.mode"]));
        object? latest = null;
        bool Latest(string expected)
        {
            while (subscription.Updates.TryRead(out MetricUpdate? update))
            {
                if (update.Values.TryGetValue("custom.mode", out object? value))
                {
                    latest = value;
                }
            }

            return Equals(latest, expected);
        }

        await TestWait.UntilAsync(() => Latest("Normal"), "the initial value in the snapshot");

        using HttpResponseMessage set = await host.PutAsync("/api/v1/sensors/mode/value", "{\"value\":\"eco\"}");
        await ApiAssert.JsonAsync(set);

        await TestWait.UntilAsync(() => Latest("Eco"), "the new value in an update");
    }

    [Fact]
    public async Task An_undeclared_sensor_and_a_sensor_of_another_type_are_refused()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage created = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor("app_running", "no-such-process"));
        await ApiAssert.JsonAsync(created, HttpStatusCode.Created);

        using HttpResponseMessage unknown = await host.PutAsync("/api/v1/sensors/nope/value", "{\"value\":1}");
        using HttpResponseMessage unknownRead = await host.GetAsync("/api/v1/sensors/nope/value");
        using HttpResponseMessage otherType = await host.PutAsync("/api/v1/sensors/app_running/value", "{\"value\":true}");
        using HttpResponseMessage otherTypeRead = await host.GetAsync("/api/v1/sensors/app_running/value");

        JsonNode problem = await ApiAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_sensor");
        Assert.Contains("'nope'", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        await ApiAssert.ProblemAsync(unknownRead, HttpStatusCode.NotFound, "unknown_sensor");
        JsonNode conflict = await ApiAssert.ProblemAsync(otherType, HttpStatusCode.Conflict, "not_state_sensor");
        Assert.Contains("process_running", conflict["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        await ApiAssert.ProblemAsync(otherTypeRead, HttpStatusCode.Conflict, "not_state_sensor");
    }

    [Theory]
    [InlineData("number", "{\"value\":\"warm\"}")]
    [InlineData("number", "{\"value\":true}")]
    [InlineData("number", "{}")]
    [InlineData("number", "{\"value\":{\"a\":1}}")]
    [InlineData("bool", "{\"value\":\"maybe\"}")]
    [InlineData("bool", "{\"value\":5}")]
    [InlineData("enum", "{\"value\":\"Turbo\"}")]
    public async Task A_value_that_does_not_fit_is_refused_with_422_at_value_and_nothing_changes(string valueType, string body)
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("s", valueType, options: valueType == "enum" ? "[\"Normal\",\"Eco\"]" : null));

        using HttpResponseMessage set = await host.PutAsync("/api/v1/sensors/s/value", body);

        JsonNode problem = await ApiAssert.ProblemAsync(set, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal(["/value"], ApiAssert.ErrorPaths(problem));
        using HttpResponseMessage read = await host.GetAsync("/api/v1/sensors/s/value");
        Assert.Null((await ApiAssert.JsonAsync(read))["value"]);
    }

    [Fact]
    public async Task The_message_of_a_refused_value_names_the_sensor_and_the_value()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        using HttpResponseMessage set = await host.PutAsync("/api/v1/sensors/level/value", "{\"value\":\"warm\"}");

        JsonNode problem = await ApiAssert.ProblemAsync(set, HttpStatusCode.UnprocessableEntity, "validation_failed");
        string message = problem["errors"]![0]!["message"]!.GetValue<string>();
        Assert.Contains("level", message, StringComparison.Ordinal);
        Assert.Contains("warm", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        using HttpResponseMessage set = await host.PutAsync("/api/v1/sensors/level/value", "not json");

        await ApiAssert.ProblemAsync(set, HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task The_value_endpoints_need_a_session()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        using HttpResponseMessage set = await host.SendAsync(HttpMethod.Put, "/api/v1/sensors/level/value", "{\"value\":1}", anonymous: true);
        using HttpResponseMessage read = await host.SendAsync(HttpMethod.Get, "/api/v1/sensors/level/value", anonymous: true);

        Assert.Equal(HttpStatusCode.Unauthorized, set.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [Fact]
    public async Task A_persisted_value_is_kept_in_the_data_directory_and_a_plain_one_is_not()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("kept", persist: true));
        await CreateAsync(host, StateSensor("lost"));

        using HttpResponseMessage lost = await host.PutAsync("/api/v1/sensors/lost/value", "{\"value\":1}");
        await ApiAssert.JsonAsync(lost);
        string file = Path.Combine(host.Service<IAppPaths>().DataDirectory, "state-values.json");
        Assert.False(File.Exists(file));

        using HttpResponseMessage kept = await host.PutAsync("/api/v1/sensors/kept/value", "{\"value\":7}");
        await ApiAssert.JsonAsync(kept);

        JsonNode content = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.Equal(7, content["values"]!["kept"]!["value"]!.GetValue<double>());
        Assert.Null(content["values"]!["lost"]);
    }

    // The local channel behind the CLI verb.

    private static HttpRequestMessage LocalRequest(HttpMethod method, string id, string? token, string? remoteIp = LoopbackAddress, string? json = null)
    {
        var request = new HttpRequestMessage(method, LocalChannelNames.StatePath + "/" + id);
        if (remoteIp is not null)
        {
            request.Headers.Add(TestAgent.RemoteIpHeader, remoteIp);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task The_local_channel_sets_and_reads_a_value_with_its_token_and_the_api_sees_it()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));
        string token = host.Service<LocalChannel>().Token;

        using HttpRequestMessage setRequest = LocalRequest(HttpMethod.Put, "level", token, json: "{\"value\":\"12\"}");
        using HttpResponseMessage set = await host.Client.SendAsync(setRequest);
        Assert.Equal(12, (await ApiAssert.JsonAsync(set))["value"]!.GetValue<double>());

        using HttpRequestMessage getRequest = LocalRequest(HttpMethod.Get, "level", token);
        using HttpResponseMessage read = await host.Client.SendAsync(getRequest);
        Assert.Equal(12, (await ApiAssert.JsonAsync(read))["value"]!.GetValue<double>());
        using HttpResponseMessage viaApi = await host.GetAsync("/api/v1/sensors/level/value");
        Assert.Equal(12, (await ApiAssert.JsonAsync(viaApi))["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task The_local_channel_refuses_a_missing_or_wrong_token_and_a_caller_from_another_address()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));
        string token = host.Service<LocalChannel>().Token;

        using HttpRequestMessage withoutToken = LocalRequest(HttpMethod.Put, "level", null, json: "{\"value\":1}");
        using HttpRequestMessage wrongToken = LocalRequest(HttpMethod.Put, "level", "not-the-token", json: "{\"value\":1}");
        using HttpRequestMessage elsewhere = LocalRequest(HttpMethod.Put, "level", token, remoteIp: "192.0.2.10", json: "{\"value\":1}");
        using HttpRequestMessage readWithoutToken = LocalRequest(HttpMethod.Get, "level", null);

        using HttpResponseMessage first = await host.Client.SendAsync(withoutToken);
        using HttpResponseMessage second = await host.Client.SendAsync(wrongToken);
        using HttpResponseMessage third = await host.Client.SendAsync(elsewhere);
        using HttpResponseMessage fourth = await host.Client.SendAsync(readWithoutToken);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fourth.StatusCode);
        using HttpResponseMessage read = await host.GetAsync("/api/v1/sensors/level/value");
        Assert.Null((await ApiAssert.JsonAsync(read))["value"]);
    }

    [Fact]
    public async Task The_local_channel_answers_with_a_status_that_tells_what_is_wrong()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));
        using HttpResponseMessage process = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor("app_running", "no-such-process"));
        await ApiAssert.JsonAsync(process, HttpStatusCode.Created);
        string token = host.Service<LocalChannel>().Token;

        using HttpRequestMessage unknown = LocalRequest(HttpMethod.Put, "nope", token, json: "{\"value\":1}");
        using HttpRequestMessage otherType = LocalRequest(HttpMethod.Put, "app_running", token, json: "{\"value\":1}");
        using HttpRequestMessage invalid = LocalRequest(HttpMethod.Put, "level", token, json: "{\"value\":\"warm\"}");
        using HttpRequestMessage noValue = LocalRequest(HttpMethod.Put, "level", token, json: "{}");

        using HttpResponseMessage unknownAnswer = await host.Client.SendAsync(unknown);
        using HttpResponseMessage otherTypeAnswer = await host.Client.SendAsync(otherType);
        using HttpResponseMessage invalidAnswer = await host.Client.SendAsync(invalid);
        using HttpResponseMessage noValueAnswer = await host.Client.SendAsync(noValue);

        Assert.Equal(HttpStatusCode.NotFound, unknownAnswer.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherTypeAnswer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidAnswer.StatusCode);
        Assert.Contains("warm", await invalidAnswer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, noValueAnswer.StatusCode);
    }

    // The CLI verb.

    private static Func<string, string?> ChannelVariables(ApiTestHost host, string? token = null)
    {
        string secret = token ?? host.Service<LocalChannel>().Token;
        return name => name switch
        {
            LocalChannelNames.UrlVariable => "http://localhost/",
            LocalChannelNames.TokenVariable => secret,
            _ => null,
        };
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(ApiTestHost host, Func<string, string?> environment, params string[] args)
    {
        AgentOptions options = CommandLine.Parse(["state", .. args]);
        Assert.Null(options.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var handler = new LoopbackHandler(host.Agent.Server.CreateHandler());
        int code = await StateCommand.RunAsync(options, output, error, environment, handler: handler);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task The_verb_sets_a_value_that_state_get_prints_back_and_a_script_can_read()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("mode", "enum", options: "[\"Normal\",\"Eco\"]"));

        (int setCode, string setOutput, string setError) = await RunAsync(host, ChannelVariables(host), "set", "mode", "eco");
        (int getCode, string getOutput, string getError) = await RunAsync(host, ChannelVariables(host), "get", "mode");

        Assert.Equal(ExitCodes.Success, setCode);
        Assert.Contains("Sensor 'mode' set to Eco", setOutput, StringComparison.Ordinal);
        Assert.Empty(setError);
        Assert.Equal(ExitCodes.Success, getCode);
        Assert.Equal("Eco", getOutput.Trim());
        Assert.Empty(getError);
    }

    [Fact]
    public async Task The_verb_prints_a_bool_and_a_number_the_way_a_script_expects()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("flag", "bool"));
        await CreateAsync(host, StateSensor("level"));

        await RunAsync(host, ChannelVariables(host), "set", "flag", "on");
        await RunAsync(host, ChannelVariables(host), "set", "level", "-5");
        (_, string flag, _) = await RunAsync(host, ChannelVariables(host), "get", "flag");
        (_, string level, _) = await RunAsync(host, ChannelVariables(host), "get", "level");

        Assert.Equal("true", flag.Trim());
        Assert.Equal("-5", level.Trim());
    }

    [Fact]
    public async Task Getting_a_sensor_without_a_value_prints_nothing_to_stdout_and_says_so_on_stderr()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        (int code, string output, string error) = await RunAsync(host, ChannelVariables(host), "get", "level");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(output);
        Assert.Contains("no value", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_reports_an_undeclared_sensor_and_an_invalid_value_with_the_usage_exit_code()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        (int unknownCode, _, string unknownError) = await RunAsync(host, ChannelVariables(host), "set", "nope", "1");
        (int invalidCode, _, string invalidError) = await RunAsync(host, ChannelVariables(host), "set", "level", "warm");
        (int getCode, _, string getError) = await RunAsync(host, ChannelVariables(host), "get", "nope");

        Assert.Equal(ExitCodes.Usage, unknownCode);
        Assert.Contains("'nope'", unknownError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, invalidCode);
        Assert.Contains("warm", invalidError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, getCode);
        Assert.Contains("'nope'", getError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_with_a_wrong_secret_is_refused_with_the_no_permission_exit_code()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await CreateAsync(host, StateSensor("level"));

        (int code, _, string error) = await RunAsync(host, ChannelVariables(host, token: "wrong"), "set", "level", "1");

        Assert.Equal(ExitCodes.NoPermission, code);
        Assert.Contains("no access", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_without_a_running_agent_is_unavailable()
    {
        AgentOptions options = CommandLine.Parse(["state", "get", "level"]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        string missing = Path.Combine(Path.GetTempPath(), "qoffeeautomat-no-such-" + Guid.NewGuid().ToString("N"), "local-channel.json");

        int code = await StateCommand.RunAsync(options, output, error, _ => null, channelFile: missing);

        Assert.Equal(ExitCodes.Unavailable, code);
        Assert.Contains("No running agent", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_never_sends_the_secret_to_an_address_that_is_not_loopback()
    {
        AgentOptions options = CommandLine.Parse(["state", "set", "level", "1"]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Func<string, string?> remote = name => name switch
        {
            LocalChannelNames.UrlVariable => "http://192.0.2.10:8480/",
            LocalChannelNames.TokenVariable => "secret",
            _ => null,
        };

        int code = await StateCommand.RunAsync(options, output, error, remote);

        Assert.Equal(ExitCodes.Failure, code);
        Assert.Contains("not a loopback address", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Makes the requests of the in-process server come from the loopback address, like the real command line does.</summary>
    private sealed class LoopbackHandler : DelegatingHandler
    {
        public LoopbackHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(TestAgent.RemoteIpHeader, LoopbackAddress);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>The verb <c>state</c> on the command line (T714).</summary>
[Trait("Category", "StateSensor")]
public sealed class StateCommandLineTests
{
    [Fact]
    public void State_set_takes_an_id_and_a_value()
    {
        AgentOptions options = CommandLine.Parse(["state", "set", "pc_mode", "Eco"]);

        Assert.Null(options.Error);
        Assert.Equal(AgentVerb.State, options.Verb);
        Assert.Equal(StateAction.Set, options.StateAction);
        Assert.Equal("pc_mode", options.StateSensorId);
        Assert.Equal("Eco", options.StateValue);
    }

    [Fact]
    public void State_get_takes_an_id()
    {
        AgentOptions options = CommandLine.Parse(["state", "get", "pc_mode"]);

        Assert.Null(options.Error);
        Assert.Equal(StateAction.Get, options.StateAction);
        Assert.Equal("pc_mode", options.StateSensorId);
        Assert.Null(options.StateValue);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("-0.5")]
    [InlineData("-")]
    public void A_value_may_start_with_a_single_dash(string value)
    {
        AgentOptions options = CommandLine.Parse(["state", "set", "level", value]);

        Assert.Null(options.Error);
        Assert.Equal(value, options.StateValue);
    }

    [Fact]
    public void Everything_after_a_double_dash_is_a_value()
    {
        AgentOptions options = CommandLine.Parse(["state", "set", "note", "--", "--not-an-option"]);

        Assert.Null(options.Error);
        Assert.Equal("--not-an-option", options.StateValue);
    }

    [Fact]
    public void A_value_with_spaces_is_one_argument()
    {
        AgentOptions options = CommandLine.Parse(["state", "set", "note", "two words", "--data-dir", "/tmp/x"]);

        Assert.Null(options.Error);
        Assert.Equal("two words", options.StateValue);
        Assert.Equal("/tmp/x", options.DataDir);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("state", "toggle", "x")]
    [InlineData("state", "set")]
    [InlineData("state", "set", "id")]
    [InlineData("state", "get")]
    [InlineData("state", "get", "id", "extra")]
    [InlineData("state", "set", "id", "value", "extra")]
    public void An_incomplete_or_wrong_state_command_is_a_usage_error(params string[] args)
    {
        AgentOptions options = CommandLine.Parse(args);

        Assert.NotNull(options.Error);
    }

    [Fact]
    public void The_usage_text_lists_the_verb()
    {
        Assert.Contains("state set <id> <value>", CommandLine.Usage, StringComparison.Ordinal);
        Assert.Contains("state get <id>", CommandLine.Usage, StringComparison.Ordinal);
    }
}
