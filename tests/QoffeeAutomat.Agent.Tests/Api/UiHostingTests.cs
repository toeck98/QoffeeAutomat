using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>
/// The agent serves the web UI as static files (T401, PLAN.md D-015, D-018): the app page for the root and for client
/// routes, the files of the app, the MudBlazor assets from the package, all gated by remote UI on the LAN, and never in
/// the way of <c>/api</c>, <c>/link</c> or <c>/healthz</c>.
/// </summary>
[Trait("Category", "Ui")]
public sealed class UiHostingTests : IDisposable
{
    private const string LanAddress = "192.168.1.50";

    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/setup")]
    [InlineData("/commands/some-id/edit")]
    public async Task The_root_and_client_routes_answer_with_the_app_page(string path)
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"app\">", html, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/icon.svg")]
    [InlineData("/icon.png")]
    [InlineData("/favicon.ico")]
    [InlineData("/css/app.css")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.js")]
    [InlineData("/_content/QoffeeAutomat.Ui.Components/qoffeeautomat.js")]
    [InlineData("/_content/QoffeeAutomat.Ui.Components/qoffeeautomat.css")]
    [InlineData("/_content/QoffeeAutomat.Ui.Components/monaco-config.js")]
    [InlineData("/_content/BlazorMonaco/jsInterop.js")]
    [InlineData("/_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js")]
    [InlineData("/_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js")]
    [InlineData("/_framework/blazor.webassembly.js")]
    public async Task The_files_of_the_app_and_its_component_library_are_served_locally(string path)
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_missing_file_is_a_404_and_not_the_app_page()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Get, "/css/missing.css");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/v1/nothing-here")]
    [InlineData("/api/other")]
    [InlineData("/link/v1/nothing-here")]
    [InlineData("/healthz/nothing-here")]
    [InlineData("/local/v1/nothing-here")]
    public async Task The_app_page_never_shadows_the_api_the_link_or_the_health_check(string path)
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_real_endpoints_still_win_over_the_fallback()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage health = await TestAgent.SendAsync(client, HttpMethod.Get, "/healthz");
        using HttpResponseMessage me = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me");
        using HttpResponseMessage setup = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/setup");

        Assert.Equal("application/json", health.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
    }

    [Fact]
    public async Task Other_methods_on_a_client_route_are_not_answered_with_the_app_page()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Post, "/login", json: "{}");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Other_pcs_get_neither_the_app_page_nor_its_files_while_remote_ui_is_off()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        foreach (string path in new[] { "/", "/login", "/index.html", "/_framework/blazor.webassembly.js", "/icon.svg" })
        {
            using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Get, path, remoteIp: LanAddress);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, path + " answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " instead of 403.");
            Assert.Contains("remote_ui_disabled", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // This PC is served, and the health check stays open to everybody.
        using HttpResponseMessage local = await TestAgent.SendAsync(client, HttpMethod.Get, "/", remoteIp: "127.0.0.1");
        using HttpResponseMessage health = await TestAgent.SendAsync(client, HttpMethod.Get, "/healthz", remoteIp: LanAddress);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Other_pcs_get_the_app_page_once_remote_ui_is_on()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        using HttpResponseMessage saved = await host.PutAsync("/api/v1/settings", "{\"schemaVersion\":1,\"name\":\"PC\",\"remoteUi\":true}");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using HttpResponseMessage page = await TestAgent.SendAsync(host.Client, HttpMethod.Get, "/login", remoteIp: LanAddress);
        using HttpResponseMessage file = await TestAgent.SendAsync(host.Client, HttpMethod.Get, "/icon.svg", remoteIp: LanAddress);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
    }

    [Fact]
    public async Task The_published_app_page_loads_nothing_from_another_host()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage response = await TestAgent.SendAsync(client, HttpMethod.Get, "/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//cdn", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_reserved_prefixes_cover_every_fixed_path_of_the_agent()
    {
        string[] fixedPaths =
        [
            "/api/v1/status",
            "/openapi/v1.json",
            ProtocolInfo.LinkPath,
            ProtocolInfo.PairPath,
            AgentHost.HealthPath,
            LocalChannelNames.EmitPath,
        ];

        foreach (string path in fixedPaths)
        {
            Assert.True(UiHosting.IsReserved(new PathString(path)), path + " is not reserved.");
        }

        Assert.False(UiHosting.IsReserved(new PathString("/login")));
        Assert.False(UiHosting.IsReserved(new PathString("/apiary")));
    }
}
