using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Core.Templates;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>The template gallery endpoints of the management API (T701), served from the <c>templates/</c> folder of the repository.</summary>
[Trait("Category", "Api")]
public sealed class TemplateApiTests
{
    [Fact]
    public async Task The_gallery_lists_commands_and_sensors_with_their_kind()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/v1/templates");
        JsonNode body = await ApiAssert.JsonAsync(response);

        JsonArray templates = body["templates"]!.AsArray();
        Assert.Contains(templates, template => template!["id"]!.GetValue<string>() == "set_volume" && template["kind"]!.GetValue<string>() == "command");
        Assert.Contains(templates, template => template!["id"]!.GetValue<string>() == "disk_free_percent" && template["kind"]!.GetValue<string>() == "sensor");
        Assert.All(templates, template => Assert.False(string.IsNullOrWhiteSpace(template!["name"]!.GetValue<string>())));
    }

    [Fact]
    public async Task A_template_is_previewed_with_its_document_and_scripts()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage response = await host.GetAsync("/api/v1/templates/set_display_profile");
        JsonNode body = await ApiAssert.JsonAsync(response);

        Assert.Equal("set_display_profile", body["command"]!["id"]!.GetValue<string>());
        Assert.Null(body["sensor"]);
        JsonNode script = Assert.Single(body["scripts"]!.AsArray())!;
        Assert.Equal("set_display_profile.ps1", script["name"]!.GetValue<string>());
        Assert.Contains("param(", script["content"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_template_is_404_for_the_preview_and_for_adding()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage preview = await host.GetAsync("/api/v1/templates/nothing_like_it");
        using HttpResponseMessage add = await host.PostAsync("/api/v1/templates/nothing_like_it/instantiate");

        await ApiAssert.ProblemAsync(preview, HttpStatusCode.NotFound, "unknown_template");
        await ApiAssert.ProblemAsync(add, HttpStatusCode.NotFound, "unknown_template");
    }

    [Fact]
    public async Task Adding_a_command_template_twice_creates_two_commands_with_their_own_scripts()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage first = await host.PostAsync("/api/v1/templates/set_display_profile/instantiate");
        JsonNode firstBody = await ApiAssert.JsonAsync(first, HttpStatusCode.Created);
        using HttpResponseMessage second = await host.PostAsync("/api/v1/templates/set_display_profile/instantiate");
        JsonNode secondBody = await ApiAssert.JsonAsync(second, HttpStatusCode.Created);

        Assert.Equal("command", firstBody["kind"]!.GetValue<string>());
        Assert.Equal("set_display_profile", firstBody["id"]!.GetValue<string>());
        Assert.Equal("set_display_profile_2", secondBody["id"]!.GetValue<string>());
        Assert.Equal("/api/v1/commands/set_display_profile_2", second.Headers.Location?.ToString());

        using HttpResponseMessage command = await host.GetAsync("/api/v1/commands/set_display_profile_2");
        JsonNode commandBody = await ApiAssert.JsonAsync(command);
        Assert.Equal("Set display profile (2)", commandBody["name"]!.GetValue<string>());
        Assert.Equal("set_display_profile_2.ps1", commandBody["action"]!["script"]!.GetValue<string>());

        using HttpResponseMessage script = await host.GetAsync("/api/v1/scripts/set_display_profile_2.ps1");
        await ApiAssert.JsonAsync(script);
        Assert.True(File.Exists(Path.Combine(host.DataPath, "scripts", "set_display_profile.ps1")));
        Assert.True(File.Exists(Path.Combine(host.DataPath, "scripts", "set_display_profile_2.ps1")));
    }

    [Fact]
    public async Task Adding_a_sensor_template_creates_a_sensor_that_the_catalog_lists()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage added = await host.PostAsync("/api/v1/templates/app_running/instantiate");
        JsonNode body = await ApiAssert.JsonAsync(added, HttpStatusCode.Created);

        Assert.Equal("sensor", body["kind"]!.GetValue<string>());
        using HttpResponseMessage sensor = await host.GetAsync("/api/v1/sensors/app_running");
        JsonNode sensorBody = await ApiAssert.JsonAsync(sensor);
        Assert.Equal("process_running", sensorBody["source"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_mode_template_brings_the_commands_it_runs()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage added = await host.PostAsync("/api/v1/templates/pc_mode/instantiate");
        JsonNode body = await ApiAssert.JsonAsync(added, HttpStatusCode.Created);

        Assert.Equal("mode", body["kind"]!.GetValue<string>());
        Assert.Equal("pc_mode", body["id"]!.GetValue<string>());
        Assert.Equal("/api/v1/modes/pc_mode", added.Headers.Location?.ToString());
        JsonNode[] items = [.. body["created"]!.AsArray().Select(item => item!)];
        string[] commands = [.. items.Where(item => item["kind"]!.GetValue<string>() == "command").Select(item => item["id"]!.GetValue<string>())];
        Assert.Contains("set_display_profile", commands);
        Assert.Contains("launch_simhub", commands);
        Assert.Equal("mode", items[^1]["kind"]!.GetValue<string>());
        Assert.Equal("pc_mode", items[^1]["id"]!.GetValue<string>());
        using HttpResponseMessage dependency = await host.GetAsync("/api/v1/commands/launch_simhub");
        await ApiAssert.JsonAsync(dependency);
    }

    [Fact]
    public async Task A_mode_template_is_listed_and_previewed_with_its_group_document()
    {
        await using ApiTestHost host = await StartAsync();

        using HttpResponseMessage list = await host.GetAsync("/api/v1/templates");
        JsonNode listBody = await ApiAssert.JsonAsync(list);
        Assert.Contains(
            listBody["templates"]!.AsArray(),
            template => template!["id"]!.GetValue<string>() == "pc_mode" && template["kind"]!.GetValue<string>() == "mode");

        using HttpResponseMessage response = await host.GetAsync("/api/v1/templates/pc_mode");
        JsonNode body = await ApiAssert.JsonAsync(response);

        Assert.Equal("mode", body["summary"]!["kind"]!.GetValue<string>());
        Assert.Null(body["command"]);
        Assert.Null(body["sensor"]);
        Assert.Equal("pc_mode", body["mode"]!["id"]!.GetValue<string>());
        Assert.Equal(5, body["mode"]!["modes"]!.AsArray().Count);
    }

    private static Task<ApiTestHost> StartAsync()
    {
        return ApiTestHost.StartAsync(services => services.AddSingleton(new TemplateCatalogSource(TemplatesDirectory())));
    }

    private static string TemplatesDirectory()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "global.json")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory, "templates");
    }
}
