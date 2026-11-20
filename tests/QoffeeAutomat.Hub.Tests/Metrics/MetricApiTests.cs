using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>
/// The metric endpoints of the hub API (T910): <c>GET /api/v1/agents/{id}/metrics</c> and <c>GET /api/v1/metrics/query</c>, through the whole hub with its sign-in, roles and
/// anti-forgery rules. The data is written to the store directly; the clock of the host is <see cref="MetricKit.T0"/>.
/// </summary>
[Trait("Category", "Metrics")]
public sealed class MetricApiTests
{
    private static readonly DateTimeOffset T0 = MetricKit.T0;

    private static Task<HubTestHost> StartAsync(HubMetricsOptions? options = null)
    {
        return HubTestHost.StartAsync(
            withUsers: true,
            configure: options is null
                ? null
                : services =>
                {
                    services.RemoveAll<HubMetricsOptions>();
                    services.AddSingleton(options);
                });
    }

    private static async Task SeedAsync(HubTestHost host, params MetricSample[] samples)
    {
        HubDatabase database = host.Service<HubDatabase>();
        foreach (string agent in samples.Select(sample => sample.AgentId).Distinct(StringComparer.Ordinal))
        {
            MetricKit.InsertAgent(database, agent);
        }

        _ = await host.Service<IMetricStore>().AppendAsync(samples, CancellationToken.None);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Iso(DateTimeOffset time) => Escape(time.ToString("O", CultureInfo.InvariantCulture));

    private static async Task<JsonNode> QueryAsync(HubTestHost host, string query)
    {
        using HttpResponseMessage response = await host.Viewer.GetAsync("/api/v1/metrics/query?" + query);
        return await HubAssert.JsonAsync(response);
    }

    private static async Task AssertInvalidAsync(HubTestHost host, string query)
    {
        using HttpResponseMessage response = await host.Viewer.GetAsync("/api/v1/metrics/query?" + query);
        _ = await HubAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "invalid_query");
    }

    private static DateTimeOffset Time(JsonNode? node) => DateTimeOffset.Parse(node!.GetValue<string>(), CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_viewer_reads_the_newest_value_of_every_metric_of_an_agent()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(
            host,
            MetricKit.Num("a1", "memory.used", T0.AddMinutes(-3), 7, "GB"),
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-2), 10, "%"),
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-1), 42.5, "%"),
            MetricKit.Flag("a1", "network.up", T0.AddMinutes(-1), true),
            MetricKit.Txt("a1", "session.state", T0.AddMinutes(-5), "locked"));

        using HttpResponseMessage response = await host.Viewer.GetAsync("/api/v1/agents/a1/metrics");
        JsonNode body = await HubAssert.JsonAsync(response);

        Assert.Equal("a1", body["agentId"]!.GetValue<string>());
        JsonArray metrics = body["metrics"]!.AsArray();
        Assert.Equal(["cpu.usage", "memory.used", "network.up", "session.state"], metrics.Select(metric => metric!["metricId"]!.GetValue<string>()));

        JsonNode cpu = metrics[0]!;
        Assert.Equal("number", cpu["valueType"]!.GetValue<string>());
        Assert.Equal("%", cpu["unit"]!.GetValue<string>());
        Assert.Equal(42.5, cpu["value"]!.GetValue<double>());
        Assert.Equal(T0.AddMinutes(-1), Time(cpu["ts"]));
        Assert.Null(cpu["text"]);

        Assert.Equal(1d, metrics[2]!["value"]!.GetValue<double>());
        Assert.Null(metrics[2]!["unit"]);
        Assert.Equal("locked", metrics[3]!["text"]!.GetValue<string>());
        Assert.Null(metrics[3]!["value"]);
    }

    [Fact]
    public async Task An_agent_without_stored_metrics_has_an_empty_list_and_an_unknown_agent_is_404()
    {
        await using HubTestHost host = await StartAsync();
        MetricKit.InsertAgent(host.Service<HubDatabase>(), "quiet");

        using (HttpResponseMessage response = await host.Viewer.GetAsync("/api/v1/agents/quiet/metrics"))
        {
            JsonNode body = await HubAssert.JsonAsync(response);
            Assert.Empty(body["metrics"]!.AsArray());
        }

        using (HttpResponseMessage response = await host.Viewer.GetAsync("/api/v1/agents/ghost/metrics"))
        {
            _ = await HubAssert.ProblemAsync(response, HttpStatusCode.NotFound, "unknown_agent");
        }
    }

    [Fact]
    public async Task Without_a_sign_in_the_metric_endpoints_answer_401()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(host, MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-1), 1));

        using (HttpResponseMessage latest = await host.Anonymous.GetAsync("/api/v1/agents/a1/metrics"))
        {
            HubAssert.Status(latest, HttpStatusCode.Unauthorized);
        }

        using (HttpResponseMessage query = await host.Anonymous.GetAsync("/api/v1/metrics/query?agent=a1"))
        {
            HubAssert.Status(query, HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task An_api_token_of_the_viewer_role_may_query()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(host, MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-1), 1));
        string secret = await host.CreateTokenAsync("grafana", HubRoles.Viewer);
        HubApiClient client = host.CreateClient();
        client.BearerToken = secret;

        using HttpResponseMessage response = await client.GetAsync("/api/v1/metrics/query?agent=a1");

        JsonNode body = await HubAssert.JsonAsync(response);
        Assert.Single(body["series"]!.AsArray());
    }

    [Fact]
    public async Task A_query_without_a_range_returns_the_raw_samples_of_the_last_hour()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(
            host,
            MetricKit.Num("a1", "cpu.usage", T0.AddHours(-2), 1, "%"),
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-50), 2, "%"),
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-10), 3, "%"));

        JsonNode body = await QueryAsync(host, "agent=a1");

        Assert.Equal("raw", body["resolution"]!.GetValue<string>());
        Assert.Equal(T0.AddHours(-1), Time(body["from"]));
        Assert.Equal(T0, Time(body["to"]));
        JsonNode series = Assert.Single(body["series"]!.AsArray())!;
        Assert.Equal("a1", series["agentId"]!.GetValue<string>());
        Assert.Equal("cpu.usage", series["metricId"]!.GetValue<string>());
        Assert.Equal("number", series["valueType"]!.GetValue<string>());
        Assert.Equal("%", series["unit"]!.GetValue<string>());
        Assert.Equal("raw", series["resolution"]!.GetValue<string>());
        Assert.False(series["truncated"]!.GetValue<bool>());
        JsonArray points = series["points"]!.AsArray();
        Assert.Equal([2d, 3d], points.Select(point => point!["value"]!.GetValue<double>()));
        Assert.Equal(T0.AddMinutes(-50), Time(points[0]!["ts"]));
        Assert.Null(points[0]!["min"]);
    }

    [Fact]
    public async Task The_resolution_follows_from_the_range_unless_the_request_names_one()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(
            host,
            MetricKit.Num("a1", "cpu.usage", T0.AddDays(-2).AddMinutes(1), 10),
            MetricKit.Num("a1", "cpu.usage", T0.AddDays(-2).AddMinutes(2), 30),
            MetricKit.Num("a1", "cpu.usage", T0.AddDays(-9).AddMinutes(1), 50));
        _ = await host.Service<IMetricStore>().RollUpAsync(T0, CancellationToken.None);
        string from3Days = "agent=a1&from=" + Iso(T0.AddDays(-3)) + "&to=" + Iso(T0);
        string from10Days = "agent=a1&from=" + Iso(T0.AddDays(-10)) + "&to=" + Iso(T0);

        // Three days: five minute buckets.
        JsonNode fiveMinutes = await QueryAsync(host, from3Days);
        Assert.Equal("5m", fiveMinutes["resolution"]!.GetValue<string>());
        JsonNode bucket = Assert.Single(Assert.Single(fiveMinutes["series"]!.AsArray())!["points"]!.AsArray())!;
        Assert.Equal(T0.AddDays(-2), Time(bucket["ts"]));
        Assert.Equal(10d, bucket["min"]!.GetValue<double>());
        Assert.Equal(20d, bucket["avg"]!.GetValue<double>());
        Assert.Equal(30d, bucket["max"]!.GetValue<double>());
        Assert.Equal(2, bucket["count"]!.GetValue<int>());
        Assert.Null(bucket["value"]);

        // Ten days: hour buckets, one per hour with samples.
        JsonNode hours = await QueryAsync(host, from10Days);
        Assert.Equal("1h", hours["resolution"]!.GetValue<string>());
        Assert.Equal(2, Assert.Single(hours["series"]!.AsArray())!["points"]!.AsArray().Count);

        // The request may ask for another resolution.
        JsonNode raw = await QueryAsync(host, from3Days + "&resolution=raw");
        Assert.Equal("raw", raw["resolution"]!.GetValue<string>());
        Assert.Equal([10d, 30d], Assert.Single(raw["series"]!.AsArray())!["points"]!.AsArray().Select(point => point!["value"]!.GetValue<double>()));
        JsonNode explicitHours = await QueryAsync(host, from3Days + "&resolution=1h");
        Assert.Equal("1h", explicitHours["resolution"]!.GetValue<string>());
        JsonNode auto = await QueryAsync(host, from3Days + "&resolution=auto");
        Assert.Equal("5m", auto["resolution"]!.GetValue<string>());
    }

    [Fact]
    public async Task Text_metrics_are_returned_as_the_change_log_at_every_resolution()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(
            host,
            MetricKit.Txt("a1", "session.state", T0.AddDays(-2), "active"),
            MetricKit.Txt("a1", "session.state", T0.AddDays(-1), "locked"),
            MetricKit.Num("a1", "cpu.usage", T0.AddDays(-1), 5));
        _ = await host.Service<IMetricStore>().RollUpAsync(T0, CancellationToken.None);

        JsonNode body = await QueryAsync(host, "agent=a1&from=" + Iso(T0.AddDays(-3)) + "&to=" + Iso(T0));

        Assert.Equal("5m", body["resolution"]!.GetValue<string>());
        JsonArray series = body["series"]!.AsArray();
        Assert.Equal(["cpu.usage", "session.state"], series.Select(item => item!["metricId"]!.GetValue<string>()));
        Assert.Equal("5m", series[0]!["resolution"]!.GetValue<string>());
        JsonNode state = series[1]!;
        Assert.Equal("changes", state["resolution"]!.GetValue<string>());
        Assert.Equal(["active", "locked"], state["points"]!.AsArray().Select(point => point!["text"]!.GetValue<string>()));
        Assert.Null(state["points"]![0]!["value"]);
    }

    [Fact]
    public async Task Several_agents_and_metric_patterns_may_be_named_by_repeating_or_separating_the_parameters()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(
            host,
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-5), 1),
            MetricKit.Num("a1", "cpu.temperature", T0.AddMinutes(-5), 2),
            MetricKit.Num("a1", "memory.used", T0.AddMinutes(-5), 3),
            MetricKit.Num("a2", "cpu.usage", T0.AddMinutes(-5), 4),
            MetricKit.Num("a3", "cpu.usage", T0.AddMinutes(-5), 5));
        (string AgentId, string MetricId)[] expected = [("a1", "cpu.temperature"), ("a1", "cpu.usage"), ("a2", "cpu.usage")];

        foreach (string query in new[]
        {
            "agent=a2&agent=a1&metric=cpu.*",
            "agent=a2,a1&metric=cpu.*",
            "agent=a1&agent=a2&agent=a1&metric=cpu.usage&metric=cpu.temperature",
            "agent=" + Escape("a1, a2") + "&metric=" + Escape("cpu.*"),
        })
        {
            JsonNode body = await QueryAsync(host, query);
            Assert.Equal(
                expected,
                body["series"]!.AsArray().Select(item => (item!["agentId"]!.GetValue<string>(), item["metricId"]!.GetValue<string>())));
        }

        // Every metric of the agents by default.
        JsonNode all = await QueryAsync(host, "agent=a1");
        Assert.Equal(3, all["series"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_series_with_more_points_than_maxPoints_is_cut_and_marked()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(host, [.. Enumerable.Range(1, 5).Select(index => MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-index * 5), index))]);

        JsonNode body = await QueryAsync(host, "agent=a1&maxPoints=2");

        JsonNode series = Assert.Single(body["series"]!.AsArray())!;
        Assert.True(series["truncated"]!.GetValue<bool>());
        Assert.Equal([5d, 4d], series["points"]!.AsArray().Select(point => point!["value"]!.GetValue<double>()));
    }

    [Fact]
    public async Task A_time_with_an_offset_is_understood_and_a_time_without_one_is_utc()
    {
        await using HubTestHost host = await StartAsync();
        MetricKit.InsertAgent(host.Service<HubDatabase>(), "a1");

        JsonNode withOffset = await QueryAsync(host, "agent=a1&from=" + Escape("2026-11-18T10:00:00+01:00") + "&to=" + Escape("2026-11-18T11:00:00+01:00"));
        Assert.Equal(T0, Time(withOffset["from"]));
        Assert.Equal(T0.AddHours(1), Time(withOffset["to"]));

        JsonNode naive = await QueryAsync(host, "agent=a1&from=2026-11-18T08:00:00&to=2026-11-18T08:30:00");
        Assert.Equal(T0.AddHours(-1), Time(naive["from"]));
        Assert.Equal(T0.AddMinutes(-30), Time(naive["to"]));
    }

    [Fact]
    public async Task A_query_that_is_not_valid_is_a_400_with_the_code_invalid_query()
    {
        await using HubTestHost host = await StartAsync();
        MetricKit.InsertAgent(host.Service<HubDatabase>(), "a1");
        string manyAgents = string.Join('&', Enumerable.Range(0, MetricEndpoints.MaxAgents + 1).Select(index => "agent=a" + index.ToString(CultureInfo.InvariantCulture)));
        string manyMetrics = string.Join('&', Enumerable.Range(0, MetricEndpoints.MaxMetricPatterns + 1).Select(index => "metric=m" + index.ToString(CultureInfo.InvariantCulture)));

        foreach (string query in new[]
        {
            string.Empty,
            "metric=cpu.*",
            manyAgents,
            "agent=a1&" + manyMetrics,
            "agent=a1&metric=" + Escape("cp*u"),
            "agent=a1&metric=" + Escape("*cpu"),
            "agent=a1&from=yesterday",
            "agent=a1&to=tomorrow",
            "agent=a1&from=" + Iso(T0) + "&to=" + Iso(T0.AddHours(-1)),
            "agent=a1&from=" + Iso(T0) + "&to=" + Iso(T0),
            "agent=a1&maxPoints=0",
            "agent=a1&maxPoints=-1",
            "agent=a1&maxPoints=abc",
            "agent=a1&maxPoints=5001",
            "agent=a1&resolution=weekly",
            "agent=a1&resolution=5M",
        })
        {
            await AssertInvalidAsync(host, query);
        }
    }

    [Fact]
    public async Task An_agent_that_does_not_exist_is_a_404_even_when_others_exist()
    {
        await using HubTestHost host = await StartAsync();
        MetricKit.InsertAgent(host.Service<HubDatabase>(), "a1");

        using HttpResponseMessage response = await host.Viewer.GetAsync("/api/v1/metrics/query?agent=a1&agent=ghost");

        JsonNode problem = await HubAssert.ProblemAsync(response, HttpStatusCode.NotFound, "unknown_agent");
        Assert.Contains("ghost", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_that_would_read_too_many_series_is_refused()
    {
        await using HubTestHost host = await StartAsync(new HubMetricsOptions { MaxQuerySeries = 2 });
        await SeedAsync(
            host,
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-1), 1),
            MetricKit.Num("a1", "memory.used", T0.AddMinutes(-1), 1),
            MetricKit.Num("a1", "disk.free", T0.AddMinutes(-1), 1));

        await AssertInvalidAsync(host, "agent=a1");

        // Fewer series are fine.
        JsonNode body = await QueryAsync(host, "agent=a1&metric=cpu.*&metric=memory.used");
        Assert.Equal(2, body["series"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_limit_of_points_per_query_follows_the_options()
    {
        await using HubTestHost host = await StartAsync(new HubMetricsOptions { MaxQueryPoints = 3 });
        await SeedAsync(host, [.. Enumerable.Range(1, 6).Select(index => MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-index), index))]);

        await AssertInvalidAsync(host, "agent=a1&maxPoints=4");
        JsonNode body = await QueryAsync(host, "agent=a1");

        JsonNode series = Assert.Single(body["series"]!.AsArray())!;
        Assert.True(series["truncated"]!.GetValue<bool>());
        Assert.Equal(3, series["points"]!.AsArray().Count);
    }

    [Fact]
    public async Task Removing_an_agent_removes_its_stored_metrics()
    {
        await using HubTestHost host = await StartAsync();
        await SeedAsync(
            host,
            MetricKit.Num("a1", "cpu.usage", T0.AddMinutes(-1), 1),
            MetricKit.Txt("a1", "session.state", T0.AddMinutes(-1), "active"),
            MetricKit.Num("a2", "cpu.usage", T0.AddMinutes(-1), 2));
        IMetricStore store = host.Service<IMetricStore>();
        Assert.Equal(3, (await store.GetStatsAsync(CancellationToken.None)).Series);

        using (HttpResponseMessage removed = await host.Admin.DeleteAsync("/api/v1/agents/a1"))
        {
            Assert.True(removed.IsSuccessStatusCode, "Removing the agent answered " + ((int)removed.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
        }

        Assert.Equal(1, (await store.GetStatsAsync(CancellationToken.None)).Series);
        using (HttpResponseMessage gone = await host.Viewer.GetAsync("/api/v1/agents/a1/metrics"))
        {
            _ = await HubAssert.ProblemAsync(gone, HttpStatusCode.NotFound, "unknown_agent");
        }

        Assert.Single(await store.GetLatestAsync("a2", CancellationToken.None));
    }
}
