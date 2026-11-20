using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Metrics.Prometheus;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>
/// An agent gateway whose presence, manifest, live state and reconnect count the test sets; everything else does nothing. It is also the
/// <see cref="IGatewayStatistics"/> of the host, so the exporter sees the counter that the test chose.
/// </summary>
internal sealed class StateGateway : IAgentGateway, IGatewayStatistics
{
    private readonly Dictionary<string, AgentPresence> _presence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentStateSnapshot> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedManifest> _manifests = new(StringComparer.Ordinal);

    public long LinkReconnects { get; set; }

    /// <summary>Sets what the gateway knows about an agent: its presence, a manifest with the given metrics and a state with the given values.</summary>
    public void Set(string agentId, bool online, params (string Id, string ValueType, object? Value)[] metrics)
    {
        _presence[agentId] = new AgentPresence(agentId, online, online ? MetricKit.T0 : null, MetricKit.T0, PowerStates.Unknown, null, null, [], null);
        _manifests[agentId] = MetricKit.Manifest(agentId, [.. metrics.Select(metric => MetricKit.Metric(metric.Id, metric.ValueType))]);
        _state[agentId] = new AgentStateSnapshot(
            MetricKit.T0.ToUnixTimeMilliseconds(),
            metrics.ToDictionary(metric => metric.Id, metric => MetricKit.Json(metric.Value), StringComparer.Ordinal),
            MetricKit.T0);
    }

    /// <summary>Declares an agent online without any manifest or state, as right after the link opened.</summary>
    public void SetOnlyPresent(string agentId)
    {
        _presence[agentId] = new AgentPresence(agentId, true, MetricKit.T0, MetricKit.T0, PowerStates.Unknown, null, null, [], null);
    }

    public IReadOnlyList<AgentPresence> GetAllPresence() => [.. _presence.Values.OrderBy(presence => presence.AgentId, StringComparer.Ordinal)];

    public AgentPresence GetPresence(string agentId) => _presence.TryGetValue(agentId, out AgentPresence? presence)
        ? presence
        : new AgentPresence(agentId, false, null, null, PowerStates.Unknown, null, null, [], null);

    public CachedManifest? GetManifest(string agentId) => _manifests.GetValueOrDefault(agentId);

    public AgentStateSnapshot? GetState(string agentId) => _state.GetValueOrDefault(agentId);

    public Task<IReadOnlyList<StoredAgentEvent>> GetEventsAsync(string agentId, int limit, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<StoredAgentEvent>>([]);
    }

    public IMetricSubscription SubscribeMetrics(string agentId, MetricDemand demand) => new FakeSubscription(agentId, demand);

    public IDisposable Observe(IAgentGatewayObserver observer) => new Nothing();

    public Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(AgentRunOutcome.Failure(HubErrorCodes.AgentOffline, "No agent in this test."));
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The Prometheus export of the hub (T911): <c>GET /metrics</c> through the whole hub, authenticated by a read-only API token and parsed by a strict parser of the text
/// exposition format.
/// </summary>
[Trait("Category", "Prometheus")]
public sealed class PrometheusTests
{
    private const string Number = MetricValueTypes.Number;
    private const string Bool = MetricValueTypes.Bool;
    private const string TextType = MetricValueTypes.Text;

    private static async Task<HubTestHost> StartAsync(StateGateway gateway)
    {
        return await HubTestHost.StartAsync(
            withUsers: true,
            configure: services =>
            {
                services.RemoveAll<IAgentGateway>();
                services.RemoveAll<IGatewayStatistics>();
                services.AddSingleton<IAgentGateway>(gateway);
                services.AddSingleton<IGatewayStatistics>(gateway);
            });
    }

    private static void AddAgent(HubTestHost host, string id, string name, string status = "enrolled")
    {
        host.Service<HubDatabase>().Query(connection => HubSql.Execute(
            connection,
            null,
            "INSERT INTO agents (id, name, hostname, os, arch, version, status, enrolled_at) VALUES (@id, @name, 'HOST', 'windows', 'x64', '1.0.0', @status, 1);",
            ("@id", id),
            ("@name", name),
            ("@status", status)));
    }

    private static async Task<HubApiClient> ScraperAsync(HubTestHost host, string role = HubRoles.Viewer)
    {
        HubApiClient client = host.CreateClient();
        client.BearerToken = await host.CreateTokenAsync("prometheus-" + role, role);
        return client;
    }

    private static async Task<ParsedExposition> ScrapeAsync(HubApiClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return PrometheusTextParser.Parse(await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------
    // Authentication: a read-only token, nothing else
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_request_without_a_token_gets_401()
    {
        await using HubTestHost host = await StartAsync(new StateGateway());

        using HttpResponseMessage response = await host.Anonymous.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("qoffeeautomat_", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_that_is_unknown_or_malformed_gets_401()
    {
        await using HubTestHost host = await StartAsync(new StateGateway());

        foreach (string secret in new[] { "qht_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "not a token" })
        {
            HubApiClient client = host.CreateClient();
            client.BearerToken = secret;
            using HttpResponseMessage response = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_signed_in_session_without_a_token_gets_401()
    {
        await using HubTestHost host = await StartAsync(new StateGateway());

        foreach (HubApiClient client in new[] { host.Admin, host.Operator, host.Viewer })
        {
            using HttpResponseMessage response = await client.GetAsync("/metrics");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(HubRoles.Operator)]
    [InlineData(HubRoles.Admin)]
    public async Task A_token_of_a_more_powerful_role_gets_403(string role)
    {
        await using HubTestHost host = await StartAsync(new StateGateway());

        using HttpResponseMessage response = await (await ScraperAsync(host, role)).GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("qoffeeautomat_", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_viewer_token_reads_the_export_without_the_anti_forgery_header()
    {
        await using HubTestHost host = await StartAsync(new StateGateway());
        HubApiClient client = await ScraperAsync(host);
        client.SendCsrf = false;

        using HttpResponseMessage response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // The document
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_export_has_the_content_type_of_the_text_format_and_is_not_cached()
    {
        await using HubTestHost host = await StartAsync(new StateGateway());

        using HttpResponseMessage response = await (await ScraperAsync(host)).GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("version=0.0.4", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task A_hub_without_agents_still_exports_a_valid_document_with_its_health()
    {
        await using HubTestHost host = await StartAsync(new StateGateway());

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        Assert.Empty(export.Of("qoffeeautomat_agent_up"));
        Assert.Equal(0, export.Single("qoffeeautomat_hub_agents_online").Value);
        Assert.Equal(0, export.Single("qoffeeautomat_hub_agents", ("status", "enrolled")).Value);
        Assert.Equal(1, export.Single("qoffeeautomat_hub_info").Value);
    }

    [Fact]
    public async Task The_hub_health_metrics_count_agents_and_the_database()
    {
        var gateway = new StateGateway { LinkReconnects = 4 };
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        AddAgent(host, "a2", "Laptop");
        AddAgent(host, "a3", "Waiting", status: "pending");
        gateway.Set("a1", online: true);
        gateway.Set("a2", online: false);

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        Assert.Equal(1, export.Single("qoffeeautomat_hub_agents_online").Value);
        Assert.Equal(2, export.Single("qoffeeautomat_hub_agents", ("status", "enrolled")).Value);
        Assert.Equal(1, export.Single("qoffeeautomat_hub_agents", ("status", "pending")).Value);
        Assert.Equal(0, export.Single("qoffeeautomat_hub_agents", ("status", "rejected")).Value);
        Assert.Equal(4, export.Single("qoffeeautomat_hub_link_reconnects_total").Value);
        Assert.Equal("counter", export.Types["qoffeeautomat_hub_link_reconnects_total"]);
        Assert.Equal("counter", export.Types["qoffeeautomat_hub_metric_samples_dropped_total"]);
        Assert.Equal(1, export.Single("qoffeeautomat_agent_up", ("agent_id", "a1"), ("agent", "Desktop")).Value);
        Assert.Equal(0, export.Single("qoffeeautomat_agent_up", ("agent_id", "a2")).Value);
        Assert.DoesNotContain(export.Of("qoffeeautomat_agent_up"), sample => sample.Labels["agent_id"] == "a3");
        Assert.True(export.Single("qoffeeautomat_hub_database_size_bytes").Value > 0);
        Assert.Equal(0, export.Single("qoffeeautomat_hub_metric_series").Value);
        Assert.Equal(0, export.Single("qoffeeautomat_hub_metric_rows", ("table", "raw")).Value);
        Assert.Equal(4, export.Of("qoffeeautomat_hub_metric_rows").Count());
        Assert.False(string.IsNullOrEmpty(export.Single("qoffeeautomat_hub_info").Labels["version"]));
    }

    [Fact]
    public async Task The_latest_numeric_and_boolean_values_of_an_online_agent_are_exported_with_labels()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        gateway.Set(
            "a1",
            online: true,
            ("cpu.usage", Number, 42.5),
            ("memory.used", Number, 7),
            ("network.up", Bool, true),
            ("screen.locked", Bool, false),
            ("session.state", TextType, "locked"),
            ("disk.free", Number, null));

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        Assert.Equal(42.5, export.Single("qoffeeautomat_cpu_usage", ("agent_id", "a1"), ("agent", "Desktop")).Value);
        Assert.Equal(7, export.Single("qoffeeautomat_memory_used").Value);
        Assert.Equal(1, export.Single("qoffeeautomat_network_up").Value);
        Assert.Equal(0, export.Single("qoffeeautomat_screen_locked").Value);
        Assert.Equal("gauge", export.Types["qoffeeautomat_cpu_usage"]);
        Assert.Empty(export.Of("qoffeeautomat_session_state"));
        Assert.Empty(export.Of("qoffeeautomat_disk_free"));
    }

    [Fact]
    public async Task Each_scrape_shows_the_newest_value()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        HubApiClient client = await ScraperAsync(host);

        gateway.Set("a1", online: true, ("cpu.usage", Number, 10));
        Assert.Equal(10, (await ScrapeAsync(client)).Single("qoffeeautomat_cpu_usage").Value);

        gateway.Set("a1", online: true, ("cpu.usage", Number, 55));
        Assert.Equal(55, (await ScrapeAsync(client)).Single("qoffeeautomat_cpu_usage").Value);
    }

    [Fact]
    public async Task A_value_whose_type_differs_from_the_manifest_is_not_exported()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        gateway.Set("a1", online: true, ("cpu.usage", Number, "high"), ("network.up", Bool, 1), ("memory.used", Number, 3));

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        Assert.Empty(export.Of("qoffeeautomat_cpu_usage"));
        Assert.Empty(export.Of("qoffeeautomat_network_up"));
        Assert.Equal(3, export.Single("qoffeeautomat_memory_used").Value);
    }

    [Fact]
    public async Task An_offline_agent_exports_no_stale_values()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        AddAgent(host, "a2", "Laptop");
        gateway.Set("a1", online: true, ("cpu.usage", Number, 10));
        gateway.Set("a2", online: false, ("cpu.usage", Number, 99));

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        ParsedSample cpu = Assert.Single(export.Of("qoffeeautomat_cpu_usage"));
        Assert.Equal("a1", cpu.Labels["agent_id"]);
        Assert.Equal(10, cpu.Value);
        Assert.Equal(0, export.Single("qoffeeautomat_agent_up", ("agent_id", "a2")).Value);
    }

    [Fact]
    public async Task An_agent_that_goes_offline_drops_its_values_from_the_next_scrape()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        HubApiClient client = await ScraperAsync(host);
        gateway.Set("a1", online: true, ("cpu.usage", Number, 10));
        Assert.Single((await ScrapeAsync(client)).Of("qoffeeautomat_cpu_usage"));

        gateway.Set("a1", online: false, ("cpu.usage", Number, 10));
        ParsedExposition export = await ScrapeAsync(client);

        Assert.Empty(export.Of("qoffeeautomat_cpu_usage"));
        Assert.Equal(0, export.Single("qoffeeautomat_agent_up").Value);
    }

    [Fact]
    public async Task An_agent_that_is_not_enrolled_and_an_agent_without_manifest_or_state_export_no_values()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Pending", status: "pending");
        AddAgent(host, "a2", "Silent");
        gateway.Set("a1", online: true, ("cpu.usage", Number, 10));
        gateway.SetOnlyPresent("a2");

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        Assert.Empty(export.Of("qoffeeautomat_cpu_usage"));
        Assert.Equal(1, export.Single("qoffeeautomat_hub_agents_online").Value);
        Assert.Equal(1, export.Single("qoffeeautomat_agent_up", ("agent_id", "a2")).Value);
    }

    [Fact]
    public async Task Metric_ids_that_map_to_one_name_or_to_a_hub_name_never_produce_a_series_twice()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        gateway.Set("a1", online: true, ("a.b", Number, 1), ("a_b", Number, 2), ("a-b", Number, 3), ("hub.agents_online", Number, 99), ("agent.up", Number, 77));

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        // The first id in ordinal order wins: "a-b" sorts before "a.b" and "a_b".
        Assert.Equal(3, Assert.Single(export.Of("qoffeeautomat_a_b")).Value);
        Assert.Equal(1, export.Single("qoffeeautomat_hub_agents_online").Value);
        Assert.Equal(1, export.Single("qoffeeautomat_agent_up").Value);
    }

    [Fact]
    public async Task Two_agents_share_one_family_and_each_family_is_written_once()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        AddAgent(host, "a2", "Laptop");
        gateway.Set("a1", online: true, ("cpu.usage", Number, 1), ("memory.used", Number, 2));
        gateway.Set("a2", online: true, ("cpu.usage", Number, 3));

        using HttpResponseMessage response = await (await ScraperAsync(host)).GetAsync("/metrics");
        string text = await response.Content.ReadAsStringAsync();
        ParsedExposition export = PrometheusTextParser.Parse(text);

        Assert.EndsWith("\n", text);
        Assert.Equal(2, export.Of("qoffeeautomat_cpu_usage").Count());
        Assert.Equal(1, CountOccurrences(text, "# TYPE qoffeeautomat_cpu_usage "));
        foreach (string family in export.Samples.Select(sample => sample.Name).Distinct(StringComparer.Ordinal))
        {
            Assert.True(export.Types.ContainsKey(family), family + " has no TYPE");
            Assert.True(export.Help.ContainsKey(family), family + " has no HELP");
        }
    }

    [Fact]
    public async Task Names_with_quotes_backslashes_and_line_feeds_are_escaped_and_parse_back()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        const string name = "Wohn \"Zimmer\" \\ PC\nzweite Zeile # ö";
        AddAgent(host, "a1", name);
        gateway.Set("a1", online: true, ("cpu.usage", Number, 1));

        ParsedExposition export = await ScrapeAsync(await ScraperAsync(host));

        Assert.Equal(name, export.Single("qoffeeautomat_cpu_usage").Labels["agent"]);
        Assert.Equal(name, export.Single("qoffeeautomat_agent_up").Labels["agent"]);
    }

    [Fact]
    public async Task The_export_contains_no_secret()
    {
        var gateway = new StateGateway();
        await using HubTestHost host = await StartAsync(gateway);
        AddAgent(host, "a1", "Desktop");
        gateway.Set("a1", online: true, ("cpu.usage", Number, 1));
        HubApiClient client = await ScraperAsync(host);
        string secret = client.BearerToken!;

        using HttpResponseMessage response = await client.GetAsync("/metrics");
        string text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiTokenService.SecretPrefix, text, StringComparison.Ordinal);
        Assert.DoesNotContain(HubTestHost.StrongPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", text, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>The names, escaping and writer of the Prometheus text format, and the strict parser that the export tests rely on.</summary>
[Trait("Category", "Prometheus")]
public sealed class PrometheusTextTests
{
    [Theory]
    [InlineData("cpu.usage", "qoffeeautomat_cpu_usage")]
    [InlineData("disk.c.free", "qoffeeautomat_disk_c_free")]
    [InlineData("a-b c/d", "qoffeeautomat_a_b_c_d")]
    [InlineData("already_ok", "qoffeeautomat_already_ok")]
    [InlineData("ä.1", "qoffeeautomat___1")]
    public void A_metric_id_becomes_a_valid_name(string id, string expected)
    {
        string name = PrometheusText.MetricName(id);

        Assert.Equal(expected, name);
        Assert.Matches("^[a-zA-Z_:][a-zA-Z0-9_:]*$", name);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("a\tb\u007f", "a b ")]
    [InlineData("\\\"\n", "\\\\\\\"\\n")]
    [InlineData("ö😀", "ö😀")]
    public void Label_values_are_escaped(string raw, string expected)
    {
        Assert.Equal(expected, PrometheusText.EscapeLabelValue(raw));
    }

    [Fact]
    public void An_unpaired_surrogate_in_a_label_value_becomes_the_replacement_character()
    {
        Assert.Equal("a\uFFFDb", PrometheusText.EscapeLabelValue("a\uD800b"));
    }

    [Fact]
    public void Help_texts_keep_quotes_and_escape_backslash_and_line_feed()
    {
        Assert.Equal("say \"hi\"\\\\\\n", PrometheusText.EscapeHelp("say \"hi\"\\\n"));
    }

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(42.5, "42.5")]
    [InlineData(-1d, "-1")]
    [InlineData(1234567890123d, "1234567890123")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "+Inf")]
    [InlineData(double.NegativeInfinity, "-Inf")]
    public void Values_use_the_invariant_format(double value, string expected)
    {
        Assert.Equal(expected, PrometheusText.FormatValue(value));
    }

    [Fact]
    public void The_writer_produces_help_type_and_samples_that_parse_back()
    {
        var writer = new PrometheusTextWriter();
        Assert.True(writer.Family("x_total", "counter", "Help\nline"));
        writer.Sample("x_total", 3, ("a", "1\"2"), ("b", "\\"));
        Assert.True(writer.Family("y", "gauge", "plain"));
        writer.Sample("y", 0.5);

        ParsedExposition parsed = PrometheusTextParser.Parse(writer.ToString());

        Assert.Equal("# HELP x_total Help\\nline\n# TYPE x_total counter\nx_total{a=\"1\\\"2\",b=\"\\\\\"} 3\n# HELP y plain\n# TYPE y gauge\ny 0.5\n", writer.ToString());
        Assert.Equal("1\"2", parsed.Single("x_total").Labels["a"]);
        Assert.Equal("\\", parsed.Single("x_total").Labels["b"]);
        Assert.Equal(0.5, parsed.Single("y").Value);
        Assert.Equal("counter", parsed.Types["x_total"]);
    }

    [Fact]
    public void A_family_is_started_once()
    {
        var writer = new PrometheusTextWriter();

        Assert.False(writer.Has("dup"));
        Assert.True(writer.Family("dup", "gauge", "first"));
        Assert.True(writer.Has("dup"));
        Assert.False(writer.Family("dup", "gauge", "second"));
        Assert.DoesNotContain("second", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_bytes_are_the_utf8_of_the_text()
    {
        var writer = new PrometheusTextWriter();
        _ = writer.Family("n", "gauge", "h");
        writer.Sample("n", 1, ("name", "Wohnzimmer ö"));

        byte[] bytes = writer.ToBytes();

        Assert.Equal(writer.ToString(), Encoding.UTF8.GetString(bytes));
        Assert.Equal((byte)'#', bytes[0]);
    }

    [Theory]
    [InlineData("a 1")]
    [InlineData("a{x=\"1\"} 1\n# TYPE a gauge\n")]
    [InlineData("a 1\nb 1\na 2\n")]
    [InlineData("a{x=\"1\"} 1\na{x=\"1\"} 2\n")]
    [InlineData("a{x=1} 1\n")]
    [InlineData("a{x=\"\\q\"} 1\n")]
    [InlineData("a{x=\"1\" 1\n")]
    [InlineData("a one\n")]
    [InlineData("1a 1\n")]
    [InlineData("# TYPE a gauge\n# TYPE a gauge\na 1\n")]
    [InlineData("# TYPE a bogus\na 1\n")]
    [InlineData("a 1\r\n")]
    public void The_parser_rejects_malformed_expositions(string text)
    {
        _ = Assert.Throws<FormatException>(() => PrometheusTextParser.Parse(text));
    }

    [Fact]
    public void The_parser_accepts_a_valid_exposition_with_special_values_and_timestamps()
    {
        ParsedExposition parsed = PrometheusTextParser.Parse("# HELP a Text\n# TYPE a gauge\na{x=\"1\"} +Inf 1700000000000\na{x=\"2\"} NaN\n# plain comment\n");

        Assert.Equal(double.PositiveInfinity, parsed.Single("a", ("x", "1")).Value);
        Assert.True(double.IsNaN(parsed.Single("a", ("x", "2")).Value));
    }
}
