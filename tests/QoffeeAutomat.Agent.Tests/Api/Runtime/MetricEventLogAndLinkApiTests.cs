using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Api.Runtime;

/// <summary>The metric stream and history, the events, the log tail and the pairing endpoints of the management API (T214).</summary>
[Trait("Category", "ApiRuntime")]
public sealed class MetricEventLogAndLinkApiTests
{
    [Fact]
    public async Task The_metric_stream_is_fed_by_the_engine_as_a_subscriber_and_ends_the_demand_when_it_closes()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        MetricEngine engine = host.Service<MetricEngine>();
        string id = SimulatedMetricProvider.CpuId;

        await using (ServerEventReader stream = await ServerEventReader.OpenAsync(host, "/api/v1/metrics/stream?metrics=" + id))
        {
            Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
            ServerEvent snapshot = await stream.ReadAsync();
            Assert.Equal("snapshot", snapshot.Name);
            Assert.True(snapshot.Data["snapshot"]!.GetValue<bool>());
            Assert.NotNull(snapshot.Data["timestamp"]);
            Assert.True(snapshot.Data["values"]!.AsObject().ContainsKey(id));

            // While somebody is subscribed the metric is sampled: the catalog shows a value.
            await TestWait.UntilAsync(() => engine.TryGetCurrentValue(id, out _), "a sampled value of the metric");
            ServerEvent update = await stream.ReadAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("update", update.Name);
            Assert.False(update.Data["snapshot"]!.GetValue<bool>());
            Assert.True(update.Data["values"]!.AsObject().ContainsKey(id));
        }

        // The history keeps what was sampled.
        using HttpResponseMessage history = await host.GetAsync("/api/v1/metrics/history?metrics=" + id);
        JsonNode body = await ApiAssert.JsonAsync(history);
        JsonNode series = Assert.Single(body["series"]!.AsArray())!;
        Assert.Equal(id, series["id"]!.GetValue<string>());
        Assert.True(series["points"]!.AsArray().Count >= 1);
        Assert.NotNull(series["points"]![0]!["timestamp"]);

        // With the stream closed nobody wants the metric any more: sampling stops, so the history stops growing.
        await Task.Delay(300);
        int count = engine.GetHistory(id).Count;
        await Task.Delay(2500);
        Assert.Equal(count, engine.GetHistory(id).Count);
    }

    [Fact]
    public async Task The_metric_history_can_be_limited_and_refuses_unknown_metrics_and_missing_ids()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage none = await host.GetAsync("/api/v1/metrics/history");
        using HttpResponseMessage unknown = await host.GetAsync("/api/v1/metrics/history?metrics=no.such.metric");
        using HttpResponseMessage badSeconds = await host.GetAsync("/api/v1/metrics/history?metrics=" + SimulatedMetricProvider.CpuId + "&seconds=0");
        using HttpResponseMessage future = await host.GetAsync("/api/v1/metrics/history?metrics=" + SimulatedMetricProvider.CpuId + "&since=2999-01-01T00:00:00Z");

        await ApiAssert.ProblemAsync(none, HttpStatusCode.BadRequest, "invalid_query");
        await ApiAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_metric");
        await ApiAssert.ProblemAsync(badSeconds, HttpStatusCode.BadRequest, "invalid_query");
        JsonNode body = await ApiAssert.JsonAsync(future);
        Assert.Empty(Assert.Single(body["series"]!.AsArray())!["points"]!.AsArray());
    }

    [Fact]
    public async Task Events_are_listed_newest_first_and_can_be_filtered_by_type()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        IEventBus bus = host.Service<IEventBus>();
        await bus.PublishAsync(EventTypes.SessionLocked);
        await bus.PublishAsync(EventTypes.UserLoggedIn, EventData.Create(("user", "tim")));

        using HttpResponseMessage all = await host.GetAsync("/api/v1/events?limit=100");
        JsonNode body = await ApiAssert.JsonAsync(all);
        string[] types = [.. body["events"]!.AsArray().Select(item => item!["type"]!.GetValue<string>())];
        using HttpResponseMessage filtered = await host.GetAsync("/api/v1/events?type=user_logged_in");
        JsonNode login = Assert.Single((await ApiAssert.JsonAsync(filtered))["events"]!.AsArray())!;
        using HttpResponseMessage tooMany = await host.GetAsync("/api/v1/events?limit=100000");

        Assert.True(Array.IndexOf(types, "user_logged_in") < Array.IndexOf(types, "session_locked"), "The newest event comes first.");
        Assert.Equal("tim", login["data"]!["user"]!.GetValue<string>());
        Assert.True(login["id"]!.GetValue<long>() > 0);
        await ApiAssert.ProblemAsync(tooMany, HttpStatusCode.BadRequest, "invalid_query");
    }

    [Fact]
    public async Task The_log_tail_returns_the_newest_entries_at_or_above_the_level_oldest_first()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string logs = Path.Combine(host.DataPath, "logs");
        Directory.CreateDirectory(logs);
        await WriteLogAsync(Path.Combine(logs, "agent-20200101.log"), [Line("2020-01-01T10:00:00.000Z", "Information", "an old information"), Line("2020-01-01T10:00:01.000Z", "Warning", "an old warning")]);
        await WriteLogAsync(Path.Combine(logs, "agent-20200102.1.log"), [Line("2020-01-02T09:00:00.000Z", "Error", "a rolled error")]);
        await WriteLogAsync(Path.Combine(logs, "agent-20200102.log"), [Line("2020-01-02T10:00:00.000Z", "Debug", "a debug line"), Line("2020-01-02T10:00:05.000Z", "Critical", "a critical line"), "this line is not json"]);

        using HttpResponseMessage warnings = await host.GetAsync("/api/v1/logs?level=warning&limit=1000");
        JsonNode body = await ApiAssert.JsonAsync(warnings);
        string[] messages = [.. body["entries"]!.AsArray().Select(entry => entry!["message"]!.GetValue<string>())];
        using HttpResponseMessage debug = await host.GetAsync("/api/v1/logs?level=debug&limit=1000");
        string[] withDebug = [.. (await ApiAssert.JsonAsync(debug))["entries"]!.AsArray().Select(entry => entry!["message"]!.GetValue<string>())];
        using HttpResponseMessage invalidLevel = await host.GetAsync("/api/v1/logs?level=loud");
        using HttpResponseMessage invalidLimit = await host.GetAsync("/api/v1/logs?limit=0");

        string[] expectedOrder = ["an old warning", "a rolled error", "a critical line"];
        Assert.Equal(expectedOrder, messages.Where(message => expectedOrder.Contains(message)));
        Assert.DoesNotContain("an old information", messages);
        Assert.DoesNotContain("a debug line", messages);
        Assert.Contains("a debug line", withDebug);
        JsonNode critical = body["entries"]!.AsArray().Last(entry => entry!["message"]!.GetValue<string>() == "a critical line")!;
        Assert.Equal("Critical", critical["level"]!.GetValue<string>());
        Assert.Equal("Test", critical["category"]!.GetValue<string>());
        await ApiAssert.ProblemAsync(invalidLevel, HttpStatusCode.BadRequest, "invalid_query");
        await ApiAssert.ProblemAsync(invalidLimit, HttpStatusCode.BadRequest, "invalid_query");
    }

    [Fact]
    public async Task The_log_tail_honours_the_limit_and_keeps_the_newest_entries()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string logs = Path.Combine(host.DataPath, "logs");
        Directory.CreateDirectory(logs);
        var lines = new List<string>();
        for (int index = 0; index < 20; index++)
        {
            lines.Add(Line("2030-01-01T10:00:" + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".000Z", "Error", "entry " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // A date in the future makes this the newest file, ahead of whatever the agent logs today.
        await WriteLogAsync(Path.Combine(logs, "agent-20300101.log"), lines);

        using HttpResponseMessage response = await host.GetAsync("/api/v1/logs?level=error&limit=3");
        JsonNode body = await ApiAssert.JsonAsync(response);

        string[] messages = [.. body["entries"]!.AsArray().Select(entry => entry!["message"]!.GetValue<string>())];
        string[] expected = ["entry 17", "entry 18", "entry 19"];
        Assert.Equal(expected, messages);
    }

    [Fact]
    public async Task Pairing_can_be_started_read_and_cancelled_and_the_paired_clients_are_listed_and_revoked()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage none = await host.GetAsync("/api/v1/link/pairing");
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        using HttpResponseMessage started = await host.PostAsync("/api/v1/link/pairing");
        JsonNode pairing = await ApiAssert.JsonAsync(started);
        string code = pairing["code"]!.GetValue<string>();
        Assert.Equal(6, code.Length);
        Assert.False(string.IsNullOrEmpty(pairing["fingerprint"]!.GetValue<string>()));
        using HttpResponseMessage active = await host.GetAsync("/api/v1/link/pairing");
        Assert.Equal(code, (await ApiAssert.JsonAsync(active))["code"]!.GetValue<string>());

        PairResult paired = await host.Service<PairingService>().PairAsync(code, new PairClientInfo("tool", "API test"), CancellationToken.None);
        Assert.True(paired.Succeeded);
        using HttpResponseMessage clients = await host.GetAsync("/api/v1/link/clients");
        JsonNode client = Assert.Single((await ApiAssert.JsonAsync(clients))["clients"]!.AsArray())!;
        Assert.Equal("API test", client["name"]!.GetValue<string>());
        Assert.Equal("tool", client["kind"]!.GetValue<string>());
        Assert.DoesNotContain(paired.Token!, client.ToJsonString(), StringComparison.Ordinal);

        using HttpResponseMessage status = await host.GetAsync("/api/v1/status");
        Assert.Equal(1, (await ApiAssert.JsonAsync(status))["link"]!["pairedClients"]!.GetValue<int>());

        using HttpResponseMessage revoked = await host.DeleteAsync("/api/v1/link/clients/" + client["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using HttpResponseMessage empty = await host.GetAsync("/api/v1/link/clients");
        Assert.Empty((await ApiAssert.JsonAsync(empty))["clients"]!.AsArray());
        using HttpResponseMessage cancel = await host.DeleteAsync("/api/v1/link/pairing");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
    }

    /// <summary>A log line as <c>RollingFileLoggerProvider</c> writes it, escaped the way its writer escapes.</summary>
    private static string Line(string timestamp, string level, string message)
    {
        return "{\"ts\":" + JsonSerializer.Serialize(timestamp) + ",\"level\":" + JsonSerializer.Serialize(level)
            + ",\"category\":\"Test\",\"message\":" + JsonSerializer.Serialize(message) + "}";
    }

    private static Task WriteLogAsync(string path, IEnumerable<string> lines)
    {
        return File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
