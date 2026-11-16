using System.Net;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>The metric, settings and status endpoints of the management API (T213).</summary>
[Trait("Category", "Api")]
public sealed class MetricSettingsAndStatusApiTests
{
    [Fact]
    public async Task The_metric_catalog_lists_the_metrics_with_their_effective_settings()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage list = await host.GetAsync("/api/v1/metrics");
        JsonNode body = await ApiAssert.JsonAsync(list);
        using HttpResponseMessage single = await host.GetAsync("/api/v1/metrics/" + SimulatedMetricProvider.CpuId);
        JsonNode cpu = await ApiAssert.JsonAsync(single);

        Assert.Contains(body["metrics"]!.AsArray(), metric => metric!["id"]!.GetValue<string>() == SimulatedMetricProvider.CpuId);
        Assert.Equal("number", cpu["valueType"]!.GetValue<string>());
        Assert.True(cpu["enabled"]!.GetValue<bool>());
        Assert.True(cpu["exposeToHa"]!.GetValue<bool>());
        Assert.False(cpu["custom"]!.GetValue<bool>());
        using HttpResponseMessage unknown = await host.GetAsync("/api/v1/metrics/no.such.metric");
        await ApiAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_metric");
    }

    [Fact]
    public async Task An_override_changes_the_catalog_and_the_manifest_and_can_be_removed_again()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        MetricEngine engine = host.Service<MetricEngine>();
        ManifestService manifest = host.Service<ManifestService>();
        string id = SimulatedMetricProvider.CpuId;
        string path = "/api/v1/metrics/" + id;
        Assert.Contains(manifest.Current.Metrics, metric => metric.Id == id);
        string before = manifest.Current.Rev;

        using HttpResponseMessage hide = await host.PutAsync(path, "{\"exposeToHa\":false,\"intervalSeconds\":5}");
        JsonNode document = await ApiAssert.JsonAsync(hide);
        Assert.Equal("1", ApiAssert.ETag(hide));
        Assert.False(document["overrides"]![id]!["exposeToHa"]!.GetValue<bool>());
        await TestWait.UntilAsync(() => !engine.Catalog.Single(entry => entry.Id == id).ExposeToHa, "the override in the catalog");
        Assert.DoesNotContain(manifest.Current.Metrics, metric => metric.Id == id);
        Assert.NotEqual(before, manifest.Current.Rev);
        using HttpResponseMessage described = await host.GetAsync(path);
        JsonNode info = await ApiAssert.JsonAsync(described);
        Assert.Equal(5d, info["intervalSeconds"]!.GetValue<double>());
        Assert.False(info["exposeToHa"]!.GetValue<bool>());

        using HttpResponseMessage stale = await host.PutAsync(path, "{\"enabled\":false}", ifMatch: "\"7\"");
        JsonNode problem = await ApiAssert.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        Assert.Equal(1, problem["currentRevision"]!.GetValue<int>());

        using HttpResponseMessage removed = await host.DeleteAsync(path, ifMatch: "\"1\"");
        JsonNode afterRemoval = await ApiAssert.JsonAsync(removed);
        Assert.Null(afterRemoval["overrides"]![id]);
        await TestWait.UntilAsync(() => engine.Catalog.Single(entry => entry.Id == id).ExposeToHa, "the override removed from the catalog");
        Assert.Equal(before, manifest.Current.Rev);
    }

    [Fact]
    public async Task An_override_that_is_not_valid_is_refused_with_422_and_for_an_unknown_metric_with_404()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage negative = await host.PutAsync("/api/v1/metrics/" + SimulatedMetricProvider.CpuId, "{\"intervalSeconds\":-3}");
        using HttpResponseMessage unknown = await host.PutAsync("/api/v1/metrics/no.such.metric", "{\"enabled\":false}");

        JsonNode problem = await ApiAssert.ProblemAsync(negative, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains(ApiAssert.ErrorPaths(problem), path => path.EndsWith("/intervalSeconds", StringComparison.Ordinal));
        await ApiAssert.ProblemAsync(unknown, HttpStatusCode.NotFound, "unknown_metric");
    }

    [Fact]
    public async Task The_settings_can_be_read_and_replaced_and_the_name_reaches_the_manifest()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage initial = await host.GetAsync("/api/v1/settings");
        JsonNode defaults = await ApiAssert.JsonAsync(initial);
        Assert.Equal("0", ApiAssert.ETag(initial));
        Assert.False(string.IsNullOrEmpty(defaults["name"]!.GetValue<string>()));

        using HttpResponseMessage saved = await host.PutAsync("/api/v1/settings", "{\"schemaVersion\":1,\"name\":\"Study PC\",\"logging\":{\"level\":\"warning\"}}");
        JsonNode document = await ApiAssert.JsonAsync(saved);
        Assert.Equal("1", ApiAssert.ETag(saved));
        Assert.Equal("Study PC", document["name"]!.GetValue<string>());
        Assert.Equal("Study PC", host.Service<ManifestService>().Current.Agent.Name);

        using HttpResponseMessage stale = await host.PutAsync("/api/v1/settings", "{\"name\":\"Other\"}", ifMatch: "\"0\"");
        await ApiAssert.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        using HttpResponseMessage reread = await host.GetAsync("/api/v1/settings");
        JsonNode current = await ApiAssert.JsonAsync(reread);
        Assert.Equal("Study PC", current["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_with_errors_are_refused_with_422_and_field_paths()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.PutAsync(
            "/api/v1/settings",
            "{\"name\":\"PC\",\"uiPort\":9000,\"linkPort\":9000,\"logging\":{\"level\":\"loud\"}}");

        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        string[] paths = ApiAssert.ErrorPaths(problem);
        Assert.Contains("/linkPort", paths);
        Assert.Contains("/logging/level", paths);
    }

    [Fact]
    public async Task The_status_reports_version_platform_session_link_and_the_pause_flag()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/v1/status");
        JsonNode status = await ApiAssert.JsonAsync(response);

        Assert.False(string.IsNullOrEmpty(status["version"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(status["agentId"]!.GetValue<string>()));
        Assert.Equal("simulated", status["platform"]!["kind"]!.GetValue<string>());
        Assert.False(status["paused"]!.GetValue<bool>());
        Assert.True(status["uptimeSeconds"]!.GetValue<long>() >= 0);
        Assert.Equal(0, status["link"]!["pairedClients"]!.GetValue<int>());
        Assert.Equal(0, status["link"]!["connections"]!.GetValue<int>());
        Assert.Equal(0, status["activeRuns"]!.GetValue<int>());
        Assert.Equal(host.Service<ManifestService>().Current.Rev, status["manifestRev"]!.GetValue<string>());
        Assert.NotNull(status["session"]!["ready"]);
    }

    [Fact]
    public async Task The_status_follows_the_pause_flag_that_the_tray_icon_sets_and_clears()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        var pause = host.Service<AgentPause>();

        pause.SetPaused(true);
        using HttpResponseMessage paused = await host.GetAsync("/api/v1/status");
        JsonNode pausedStatus = await ApiAssert.JsonAsync(paused);
        pause.SetPaused(false);
        using HttpResponseMessage resumed = await host.GetAsync("/api/v1/status");
        JsonNode resumedStatus = await ApiAssert.JsonAsync(resumed);

        Assert.True(pausedStatus["paused"]!.GetValue<bool>());
        Assert.False(resumedStatus["paused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_tray_status_comes_from_the_settings_the_pairing_store_and_the_link()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        TrayStatus status = await host.Service<ITrayStatusSource>().GetAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(status.AgentName));
        Assert.Equal(0, status.PairedClients);
        Assert.Equal(0, status.ConnectedClients);
        Assert.True(status.ShowTrayIcon);

        // The address is a loopback address when the host listens on one, and unknown (null) otherwise, never anything else.
        Assert.True(status.UiUrl is null || status.UiUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
    }
}
