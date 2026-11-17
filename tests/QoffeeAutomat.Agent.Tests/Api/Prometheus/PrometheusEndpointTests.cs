using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using QoffeeAutomat.Agent.Api.Prometheus;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Api.Prometheus;

/// <summary>
/// <c>GET /metrics</c> on the HTTPS listener of the whole agent (T709): off by default, refused without the separate read-only token,
/// valid exposition text with it, and the web UI page of the same address keeps working.
/// </summary>
[Trait("Category", "Prometheus")]
public sealed class PrometheusEndpointTests
{
    private const string ScraperAccept = "text/plain;version=0.0.4;q=0.5,*/*;q=0.1";
    private const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string OtherPc = "192.0.2.10";

    private static readonly string[] Simulated =
    [
        SimulatedMetricProvider.CpuId,
        SimulatedMetricProvider.ToggleId,
        SimulatedMetricProvider.ModeId,
    ];

    /// <summary>Switches the endpoint on for the metrics and creates the token.</summary>
    private static async Task<string> EnableAsync(ApiTestHost host, params string[] metrics)
    {
        string list = string.Join(',', metrics.Select(metric => "\"" + metric + "\""));
        using HttpResponseMessage saved = await host.PutAsync(
            "/api/v1/settings",
            "{\"schemaVersion\":1,\"name\":\"Test PC\",\"prometheus\":{\"enabled\":true,\"metrics\":[" + list + "]}}");
        await ApiAssert.JsonAsync(saved);
        return await CreateTokenAsync(host);
    }

    private static async Task<string> CreateTokenAsync(ApiTestHost host)
    {
        using HttpResponseMessage created = await host.PostAsync("/api/v1/prometheus/token");
        JsonNode body = await ApiAssert.JsonAsync(created);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        return body["token"]!.GetValue<string>();
    }

    /// <summary>Asks for <c>/metrics</c> the way a scraper does, on the HTTPS listener unless <paramref name="https"/> is false.</summary>
    private static async Task<HttpResponseMessage> ScrapeAsync(
        ApiTestHost host,
        string? token,
        bool https = true,
        string? remoteIp = null,
        string accept = ScraperAccept,
        string? cookie = null)
    {
        using HttpClient client = host.Agent.CreateApiClient(https);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (remoteIp is not null)
        {
            request.Headers.Add(TestAgent.RemoteIpHeader, remoteIp);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private static bool IsExposition(HttpResponseMessage response)
    {
        return string.Equals(response.Content.Headers.ContentType?.ToString(), PrometheusFormat.ContentType, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_endpoint_is_off_by_default_even_for_a_caller_with_a_token()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await CreateTokenAsync(host);

        using HttpResponseMessage response = await ScrapeAsync(host, token);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "prometheus_disabled");
        using HttpResponseMessage status = await host.GetAsync("/api/v1/prometheus");
        JsonNode body = await ApiAssert.JsonAsync(status);
        Assert.False(body["enabled"]!.GetValue<bool>());
        Assert.True(body["tokenSet"]!.GetValue<bool>());
        Assert.Empty(body["metrics"]!.AsArray());
    }

    [Fact]
    public async Task A_request_without_the_token_or_with_a_wrong_one_is_refused()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await EnableAsync(host, Simulated);

        using HttpResponseMessage none = await ScrapeAsync(host, null);
        using HttpResponseMessage wrong = await ScrapeAsync(host, "qapm_not-the-token");
        using HttpResponseMessage cookieOnly = await ScrapeAsync(host, null, cookie: host.Cookie);

        foreach (HttpResponseMessage response in new[] { none, wrong, cookieOnly })
        {
            await ApiAssert.ProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
            Assert.False(IsExposition(response));
        }
    }

    [Fact]
    public async Task A_request_with_a_valid_token_gets_valid_exposition_text_of_the_selected_numeric_and_boolean_metrics()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, Simulated);

        using HttpResponseMessage response = await ScrapeAsync(host, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(IsExposition(response), "Content-Type was " + response.Content.Headers.ContentType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        IReadOnlyList<ExpositionFamily> families = ExpositionParser.Parse(await response.Content.ReadAsStringAsync());

        // The enum metric sim.mode is selected but is text, so it is not exported.
        Assert.Equal(["qoffeeautomat_sim_cpu", "qoffeeautomat_sim_toggle"], families.Select(family => family.Name));
        Assert.All(families, family => Assert.Equal("gauge", family.Type));
        ExpositionSample cpu = Assert.Single(families[0].Samples);
        Assert.InRange(cpu.Value, 10, 90);
        Assert.Equal("Test PC", cpu.Labels["agent"]);
        Assert.False(string.IsNullOrEmpty(cpu.Labels["agent_id"]));
        Assert.True(Assert.Single(families[1].Samples).Value is 0.0 or 1.0);
    }

    [Fact]
    public async Task A_metric_that_is_not_selected_is_not_exported()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, SimulatedMetricProvider.CounterId);

        using HttpResponseMessage response = await ScrapeAsync(host, token);

        IReadOnlyList<ExpositionFamily> families = ExpositionParser.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["qoffeeautomat_sim_counter"], families.Select(family => family.Name));
    }

    [Fact]
    public async Task A_scrape_makes_the_exporter_a_subscriber_that_the_engine_samples_for()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, SimulatedMetricProvider.CpuId);
        PrometheusExporter exporter = host.Service<PrometheusExporter>();
        Assert.False(exporter.IsSampling);

        using HttpResponseMessage response = await ScrapeAsync(host, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(exporter.IsSampling);
    }

    [Fact]
    public async Task A_caller_from_another_pc_needs_only_the_token_and_not_remote_ui()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, SimulatedMetricProvider.CpuId);

        using HttpResponseMessage allowed = await ScrapeAsync(host, token, remoteIp: OtherPc);
        using HttpResponseMessage refused = await ScrapeAsync(host, null, remoteIp: OtherPc);
        using HttpResponseMessage api = await ScrapeApiStatusAsync(host, OtherPc);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.True(IsExposition(allowed));

        // Without the token it is the token that is missing, not the remote UI.
        await ApiAssert.ProblemAsync(refused, HttpStatusCode.Unauthorized, "unauthorized");

        // The token opens nothing else: the management API stays closed to other PCs while remote UI is off.
        await ApiAssert.ProblemAsync(api, HttpStatusCode.Forbidden, "remote_ui_disabled");
    }

    private static async Task<HttpResponseMessage> ScrapeApiStatusAsync(ApiTestHost host, string remoteIp)
    {
        using HttpClient client = host.Agent.CreateApiClient(https: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        request.Headers.Add(TestAgent.RemoteIpHeader, remoteIp);
        request.Headers.Add("Cookie", host.Cookie);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task The_export_is_only_served_on_the_https_listener()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, SimulatedMetricProvider.CpuId);

        using HttpResponseMessage response = await ScrapeAsync(host, token, https: false);

        Assert.False(IsExposition(response));
        Assert.DoesNotContain("qoffeeautomat_", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(host.Service<PrometheusExporter>().IsSampling);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_ui_page_at_the_same_address_is_not_taken_over(bool enabled)
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        if (enabled)
        {
            await EnableAsync(host, SimulatedMetricProvider.CpuId);
        }

        // A browser asks for HTML and has no token: on both listeners the request goes on to the UI, whatever the settings are.
        using HttpResponseMessage https = await ScrapeAsync(host, null, accept: BrowserAccept);
        using HttpResponseMessage loopback = await ScrapeAsync(host, null, https: false, accept: BrowserAccept);

        foreach (HttpResponseMessage response in new[] { https, loopback })
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(IsExposition(response));
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("prometheus_disabled", body, StringComparison.Ordinal);
            Assert.DoesNotContain("qoffeeautomat_", body, StringComparison.Ordinal);
        }

        // The metrics page of the UI reads the management API, which is unaffected.
        using HttpResponseMessage list = await host.GetAsync("/api/v1/metrics");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task A_new_token_replaces_the_old_one_and_a_deleted_token_closes_the_endpoint()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string first = await EnableAsync(host, SimulatedMetricProvider.CpuId);
        using (HttpResponseMessage ok = await ScrapeAsync(host, first))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        string second = await CreateTokenAsync(host);

        Assert.NotEqual(first, second);
        Assert.StartsWith(PrometheusTokenStore.TokenPrefix, second, StringComparison.Ordinal);
        using (HttpResponseMessage old = await ScrapeAsync(host, first))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        using (HttpResponseMessage current = await ScrapeAsync(host, second))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }

        using HttpResponseMessage deleted = await host.DeleteAsync("/api/v1/prometheus/token");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (HttpResponseMessage closed = await ScrapeAsync(host, second))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
        }

        using HttpResponseMessage status = await host.GetAsync("/api/v1/prometheus");
        JsonNode body = await ApiAssert.JsonAsync(status);
        Assert.False(body["tokenSet"]!.GetValue<bool>());
        Assert.True(body["enabled"]!.GetValue<bool>());
        Assert.Equal([SimulatedMetricProvider.CpuId], body["metrics"]!.AsArray().Select(metric => metric!.GetValue<string>()));
    }

    [Fact]
    public async Task Deleting_a_token_that_does_not_exist_is_not_an_error()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage deleted = await host.DeleteAsync("/api/v1/prometheus/token");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task The_token_itself_is_never_stored()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, SimulatedMetricProvider.CpuId);

        string secrets = await File.ReadAllTextAsync(Path.Combine(host.DataPath, "secrets", "secrets.json"));

        Assert.Contains("prometheus.token_hash", secrets, StringComparison.Ordinal);
        Assert.DoesNotContain(token, secrets, StringComparison.Ordinal);
        Assert.DoesNotContain(token[PrometheusTokenStore.TokenPrefix.Length..], secrets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_survives_a_restart_of_the_token_store_because_only_its_hash_is_needed()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        string token = await EnableAsync(host, SimulatedMetricProvider.CpuId);

        // A second store on the same secrets file, as after a restart of the agent.
        using var restarted = new PrometheusTokenStore(host.Service<QoffeeAutomat.Core.Secrets.ISecretStore>());

        Assert.True(await restarted.IsSetAsync(CancellationToken.None));
        Assert.True(await restarted.VerifyAsync(token, CancellationToken.None));
        Assert.False(await restarted.VerifyAsync(token + "x", CancellationToken.None));
        Assert.False(await restarted.VerifyAsync(string.Empty, CancellationToken.None));
        Assert.False(await restarted.VerifyAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task The_management_endpoints_of_the_export_need_a_session()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage status = await host.SendAsync(HttpMethod.Get, "/api/v1/prometheus", anonymous: true);
        using HttpResponseMessage create = await host.SendAsync(HttpMethod.Post, "/api/v1/prometheus/token", anonymous: true);
        using HttpResponseMessage delete = await host.SendAsync(HttpMethod.Delete, "/api/v1/prometheus/token", anonymous: true);

        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    [Fact]
    public async Task Settings_with_a_malformed_metric_id_are_refused_and_nothing_is_exposed()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.PutAsync(
            "/api/v1/settings",
            "{\"schemaVersion\":1,\"name\":\"PC\",\"prometheus\":{\"enabled\":true,\"metrics\":[\"CPU Usage\"]}}");

        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/prometheus/metrics/0", ApiAssert.ErrorPaths(problem));
    }
}
