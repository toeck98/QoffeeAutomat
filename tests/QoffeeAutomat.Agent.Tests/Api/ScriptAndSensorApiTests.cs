using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>The script and sensor endpoints of the management API (T213).</summary>
[Trait("Category", "Api")]
public sealed class ScriptAndSensorApiTests
{
    [Fact]
    public async Task A_script_can_be_created_read_listed_changed_and_deleted_with_its_etag()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage created = await host.PutAsync("/api/v1/scripts/hello.ps1", "{\"content\":\"Write-Output 'hello'\\n\"}");
        JsonNode createdBody = await ApiAssert.JsonAsync(created, HttpStatusCode.Created);
        string etag = ApiAssert.ETag(created);
        Assert.Equal(etag, createdBody["eTag"]!.GetValue<string>());
        Assert.Equal(64, etag.Length);

        using HttpResponseMessage read = await host.GetAsync("/api/v1/scripts/hello.ps1");
        JsonNode readBody = await ApiAssert.JsonAsync(read);
        Assert.Equal("Write-Output 'hello'\n", readBody["content"]!.GetValue<string>());
        Assert.Equal(etag, ApiAssert.ETag(read));
        Assert.Equal("Write-Output 'hello'\n", await File.ReadAllTextAsync(Path.Combine(host.DataPath, "scripts", "hello.ps1")));

        using HttpResponseMessage list = await host.GetAsync("/api/v1/scripts");
        JsonNode listBody = await ApiAssert.JsonAsync(list);
        Assert.Contains(listBody["scripts"]!.AsArray(), script => script!["name"]!.GetValue<string>() == "hello.ps1");

        using HttpResponseMessage updated = await host.PutAsync("/api/v1/scripts/hello.ps1", "{\"content\":\"Write-Output 'bye'\"}", ifMatch: "\"" + etag + "\"");
        await ApiAssert.JsonAsync(updated);
        Assert.NotEqual(etag, ApiAssert.ETag(updated));

        using HttpResponseMessage stale = await host.PutAsync("/api/v1/scripts/hello.ps1", "{\"content\":\"stale\"}", ifMatch: "\"" + etag + "\"");
        await ApiAssert.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, "revision_mismatch");
        using HttpResponseMessage staleDelete = await host.DeleteAsync("/api/v1/scripts/hello.ps1", ifMatch: "\"" + etag + "\"");
        await ApiAssert.ProblemAsync(staleDelete, HttpStatusCode.PreconditionFailed, "revision_mismatch");

        using HttpResponseMessage deleted = await host.DeleteAsync("/api/v1/scripts/hello.ps1", ifMatch: "\"" + ApiAssert.ETag(updated) + "\"");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage gone = await host.GetAsync("/api/v1/scripts/hello.ps1");
        await ApiAssert.ProblemAsync(gone, HttpStatusCode.NotFound, "unknown_script");
    }

    [Fact]
    public async Task A_script_name_that_could_leave_the_scripts_directory_is_refused_with_422()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage hidden = await host.PutAsync("/api/v1/scripts/.hidden.ps1", "{\"content\":\"x\"}");
        using HttpResponseMessage reserved = await host.PutAsync("/api/v1/scripts/NUL", "{\"content\":\"x\"}");

        JsonNode problem = await ApiAssert.ProblemAsync(hidden, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/name", ApiAssert.ErrorPaths(problem));
        await ApiAssert.ProblemAsync(reserved, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.False(File.Exists(Path.Combine(host.DataPath, "scripts", ".hidden.ps1")));
    }

    [Fact]
    public async Task A_sensor_can_be_created_changed_and_deleted_and_appears_in_the_metric_catalog()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        MetricEngine metrics = host.Service<MetricEngine>();

        using HttpResponseMessage created = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor("app_running", "no-such-process"));
        await ApiAssert.JsonAsync(created, HttpStatusCode.Created);
        Assert.Equal("1", ApiAssert.ETag(created));
        await TestWait.UntilAsync(() => metrics.Catalog.Any(entry => entry.Id == "custom.app_running"), "the sensor in the metric catalog");

        using HttpResponseMessage read = await host.GetAsync("/api/v1/sensors/app_running");
        JsonNode document = await ApiAssert.JsonAsync(read);
        Assert.Equal("process_running", document["source"]!["type"]!.GetValue<string>());

        using HttpResponseMessage duplicate = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor("app_running", "other"));
        await ApiAssert.ProblemAsync(duplicate, HttpStatusCode.Conflict, "already_exists");

        using HttpResponseMessage changed = await host.PutAsync("/api/v1/sensors/app_running", ApiSamples.Sensor("app_running", "another-process"), ifMatch: "\"1\"");
        await ApiAssert.JsonAsync(changed);
        using HttpResponseMessage stale = await host.PutAsync("/api/v1/sensors/app_running", ApiSamples.Sensor("app_running", "third"), ifMatch: "\"1\"");
        await ApiAssert.ProblemAsync(stale, HttpStatusCode.PreconditionFailed, "revision_mismatch");

        using HttpResponseMessage list = await host.GetAsync("/api/v1/sensors");
        JsonNode all = await ApiAssert.JsonAsync(list);
        Assert.Contains(all["sensors"]!.AsArray(), sensor => sensor!["id"]!.GetValue<string>() == "app_running");

        using HttpResponseMessage deleted = await host.DeleteAsync("/api/v1/sensors/app_running");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await TestWait.UntilAsync(() => metrics.Catalog.All(entry => entry.Id != "custom.app_running"), "the sensor gone from the metric catalog");
    }

    [Fact]
    public async Task A_sensor_with_validation_errors_is_refused_with_422_and_field_paths()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        const string invalid = """
            {"id":"bad_sensor","name":"Bad","enabled":true,"exposeToHa":true,"kind":"binary_sensor","valueType":"number","source":{"type":"process_running","process":"x"}}
            """;

        using HttpResponseMessage response = await host.PostAsync("/api/v1/sensors", invalid);

        JsonNode problem = await ApiAssert.ProblemAsync(response, HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Contains("/valueType", ApiAssert.ErrorPaths(problem));
    }

    [Fact]
    public async Task Testing_a_sensor_draft_samples_it_once_without_saving_it()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using Process self = Process.GetCurrentProcess();

        using HttpResponseMessage running = await host.PostAsync("/api/v1/sensors/test", ApiSamples.Sensor("draft_sensor", self.ProcessName));
        JsonNode runningResult = await ApiAssert.JsonAsync(running);
        using HttpResponseMessage missing = await host.PostAsync("/api/v1/sensors/test", ApiSamples.Sensor("draft_sensor", "definitely-not-running-xyz"));
        JsonNode missingResult = await ApiAssert.JsonAsync(missing);

        Assert.True(runningResult["succeeded"]!.GetValue<bool>());
        Assert.True(runningResult["value"]!.GetValue<bool>());
        Assert.True(missingResult["succeeded"]!.GetValue<bool>());
        Assert.False(missingResult["value"]!.GetValue<bool>());
        using HttpResponseMessage notSaved = await host.GetAsync("/api/v1/sensors/draft_sensor");
        Assert.Equal(HttpStatusCode.NotFound, notSaved.StatusCode);
    }

    [Fact]
    public async Task Testing_an_invalid_sensor_draft_returns_422_and_a_sensor_type_without_a_sampler_reports_its_error()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage invalid = await host.PostAsync("/api/v1/sensors/test", "{\"id\":\"x\"}");
        await ApiAssert.ProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "validation_failed");

        // A Windows-only sensor type is valid, but this agent cannot sample it: that is a result, not an error response.
        const string counter = """
            {"id":"win_counter","name":"Counter","enabled":true,"exposeToHa":false,"kind":"sensor","valueType":"number","source":{"type":"perf_counter","category":"Processor","counter":"% Processor Time","instance":"_Total"}}
            """;
        using HttpResponseMessage unsupported = await host.PostAsync("/api/v1/sensors/test", counter);
        JsonNode result = await ApiAssert.JsonAsync(unsupported);
        Assert.False(result["succeeded"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(result["error"]!.GetValue<string>()));
    }
}
