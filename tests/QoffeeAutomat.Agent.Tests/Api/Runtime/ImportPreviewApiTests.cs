using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace QoffeeAutomat.Agent.Tests.Api.Runtime;

/// <summary>The preview of a configuration import: it validates like the import and writes nothing (T407).</summary>
[Trait("Category", "ApiRuntime")]
public sealed class ImportPreviewApiTests
{
    private const string ExportPath = "/api/v1/config/export";
    private const string PreviewPath = "/api/v1/config/import/preview";

    [Fact]
    public async Task The_preview_of_a_valid_archive_lists_new_items_and_writes_nothing()
    {
        await using ApiTestHost source = await ApiTestHost.StartAsync();
        await source.CreateCommandAsync(ApiSamples.Command("say_hello"));
        await CreateSensorAsync(source, "app_running", "notepad");
        using (HttpResponseMessage script = await source.PutAsync("/api/v1/scripts/hello.ps1", "{\"content\":\"Write-Output 'hello'\\n\"}"))
        {
            Assert.True(script.IsSuccessStatusCode);
        }

        byte[] archive = await ExportAsync(source);

        await using ApiTestHost target = await ApiTestHost.StartAsync();
        using HttpResponseMessage response = await target.SendAsync(HttpMethod.Post, PreviewPath, body: archive);
        JsonNode preview = await ApiAssert.JsonAsync(response);

        Assert.True(preview["valid"]!.GetValue<bool>());
        Assert.Empty(preview["errors"]!.AsArray());
        Assert.Equal(
            ["command/say_hello/new", "sensor/app_running/new", "script/hello.ps1/new"],
            Describe(preview));
        await AssertNothingWrittenAsync(target);
    }

    [Fact]
    public async Task The_preview_tells_changed_and_unchanged_items_apart()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.CreateCommandAsync(ApiSamples.Command("say_hello"));
        await host.CreateCommandAsync(ApiSamples.Command("other", name: "Other"));
        await CreateSensorAsync(host, "app_running", "notepad");
        byte[] archive = await ExportAsync(host);

        using HttpResponseMessage same = await host.SendAsync(HttpMethod.Post, PreviewPath, body: archive);
        JsonNode unchanged = await ApiAssert.JsonAsync(same);
        Assert.True(unchanged["valid"]!.GetValue<bool>());
        Assert.All(unchanged["items"]!.AsArray(), item => Assert.Equal("unchanged", item!["change"]!.GetValue<string>()));

        // The stored command changes (a new revision, another name); the archive still holds the old one.
        using (HttpResponseMessage put = await host.PutAsync("/api/v1/commands/say_hello", ApiSamples.Command("say_hello", name: "Renamed")))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using HttpResponseMessage different = await host.SendAsync(HttpMethod.Post, PreviewPath, body: archive);
        JsonNode changed = await ApiAssert.JsonAsync(different);
        string[] items = Describe(changed);
        Assert.Contains("command/say_hello/changed", items);
        Assert.Contains("command/other/unchanged", items);
        Assert.Contains("sensor/app_running/unchanged", items);

        // Nothing was imported: the stored name is still the new one.
        using HttpResponseMessage read = await host.GetAsync("/api/v1/commands/say_hello");
        JsonNode command = await ApiAssert.JsonAsync(read);
        Assert.Equal("Renamed", command["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_preview_of_an_invalid_archive_reports_the_errors_and_writes_nothing()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        byte[] archive = BuildArchive(
            ("config/commands/good_one.json", ApiSamples.Command("good_one")),
            ("config/commands/broken.json", "{\"id\":\"broken\",\"name\":\"\",\"action\":{\"type\":\"nonsense\"}}"),
            ("scripts/fine.ps1", "Write-Output 'fine'\n"));

        using HttpResponseMessage response = await host.SendAsync(HttpMethod.Post, PreviewPath, body: archive);
        JsonNode preview = await ApiAssert.JsonAsync(response);

        Assert.False(preview["valid"]!.GetValue<bool>());
        Assert.Empty(preview["items"]!.AsArray());
        string[] paths = [.. preview["errors"]!.AsArray().Select(error => error!["path"]!.GetValue<string>())];
        Assert.Contains(paths, path => path.Contains("broken.json", StringComparison.Ordinal));
        await AssertNothingWrittenAsync(host);
    }

    [Fact]
    public async Task The_preview_of_a_file_that_is_not_a_zip_is_invalid()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.SendAsync(HttpMethod.Post, PreviewPath, body: Encoding.UTF8.GetBytes("this is not a zip file"));
        JsonNode preview = await ApiAssert.JsonAsync(response);

        Assert.False(preview["valid"]!.GetValue<bool>());
        Assert.Equal("invalid_archive", preview["errors"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_preview_without_a_body_is_400()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();

        using HttpResponseMessage response = await host.SendAsync(HttpMethod.Post, PreviewPath, body: []);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task The_preview_needs_a_session_and_the_anti_forgery_header()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        byte[] archive = BuildArchive(("config/commands/say_hello.json", ApiSamples.Command("say_hello")));

        using HttpResponseMessage anonymous = await host.SendAsync(HttpMethod.Post, PreviewPath, body: archive, anonymous: true);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, PreviewPath) { Content = new ByteArrayContent(archive) };
        request.Headers.Add("Cookie", host.Cookie);
        using HttpResponseMessage noHeader = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
    }

    private static string[] Describe(JsonNode preview)
    {
        return [.. preview["items"]!.AsArray().Select(item =>
            item!["kind"]!.GetValue<string>() + "/" + item["id"]!.GetValue<string>() + "/" + item["change"]!.GetValue<string>())];
    }

    private static async Task AssertNothingWrittenAsync(ApiTestHost host)
    {
        foreach ((string path, string member) in new[] { ("/api/v1/commands", "commands"), ("/api/v1/sensors", "sensors"), ("/api/v1/scripts", "scripts") })
        {
            using HttpResponseMessage response = await host.GetAsync(path);
            JsonNode body = await ApiAssert.JsonAsync(response);
            Assert.Empty(body[member]!.AsArray());
        }

        string scripts = Path.Combine(host.DataPath, "scripts");
        Assert.True(!Directory.Exists(scripts) || Directory.GetFiles(scripts).Length == 0, "The preview wrote a script.");
    }

    private static async Task CreateSensorAsync(ApiTestHost host, string id, string process)
    {
        using HttpResponseMessage response = await host.PostAsync("/api/v1/sensors", ApiSamples.Sensor(id, process));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<byte[]> ExportAsync(ApiTestHost host)
    {
        using HttpResponseMessage response = await host.GetAsync(ExportPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static byte[] BuildArchive(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in files)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using Stream target = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                target.Write(bytes, 0, bytes.Length);
            }
        }

        return stream.ToArray();
    }
}
