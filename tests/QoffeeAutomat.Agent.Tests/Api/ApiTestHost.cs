using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Tests.Api;

/// <summary>
/// The whole agent in a temporary data directory with the first-run setup done, and a client that carries the
/// session cookie and the anti-forgery header, for the tests of the management API.
/// </summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly TempDataDirectory _data = new();
    private bool _disposed;

    private ApiTestHost(Action<IServiceCollection>? configure)
    {
        Agent = new TestAgent(_data.Path, configure);
        Client = Agent.CreateApiClient();
    }

    public TestAgent Agent { get; }

    public HttpClient Client { get; }

    public string Cookie { get; private set; } = string.Empty;

    public string DataPath => _data.Path;

    public T Service<T>()
        where T : notnull
    {
        return Agent.Services.GetRequiredService<T>();
    }

    /// <summary>Starts the agent and sets the password, so that the session cookie is available.</summary>
    public static async Task<ApiTestHost> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var host = new ApiTestHost(configure);
        host.Cookie = await host.Agent.SetUpAsync(host.Client);
        return host;
    }

    /// <summary>Sends a request as the signed-in user.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? ifMatch = null,
        bool anonymous = false,
        byte[]? body = null,
        string contentType = "application/zip",
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!anonymous)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        request.Headers.Add(CsrfHeaderFilter.HeaderName, CsrfHeaderFilter.HeaderValue);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        else if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        return await Client.SendAsync(request, completion);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<HttpResponseMessage> PostAsync(string path, string? json = null) => SendAsync(HttpMethod.Post, path, json);

    public Task<HttpResponseMessage> PutAsync(string path, string json, string? ifMatch = null) => SendAsync(HttpMethod.Put, path, json, ifMatch);

    public Task<HttpResponseMessage> PatchAsync(string path, string json, string? ifMatch = null) => SendAsync(HttpMethod.Patch, path, json, ifMatch);

    public Task<HttpResponseMessage> DeleteAsync(string path, string? ifMatch = null) => SendAsync(HttpMethod.Delete, path, ifMatch: ifMatch);

    /// <summary>Creates a command through the API and returns it as the API answered.</summary>
    public async Task<JsonNode> CreateCommandAsync(string json)
    {
        using HttpResponseMessage response = await PostAsync("/api/v1/commands", json);
        return await ApiAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Client.Dispose();
        await Agent.DisposeAsync();
        _data.Dispose();
    }
}

/// <summary>Assertions on API responses that show the body when they fail.</summary>
internal static class ApiAssert
{
    /// <summary>Checks the status and returns the JSON body.</summary>
    public static async Task<JsonNode> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("The body is not JSON: " + body);
    }

    /// <summary>Checks that the response is a problem (RFC 9457) with the status and the code, and returns its body.</summary>
    public static async Task<JsonNode> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        JsonNode problem = await JsonAsync(response, expected);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expected, problem["status"]!.GetValue<int>());
        Assert.Equal(code, problem["code"]!.GetValue<string>());
        return problem;
    }

    /// <summary>The JSON pointers of the validation errors of a 422 problem.</summary>
    public static string[] ErrorPaths(JsonNode problem)
    {
        return [.. problem["errors"]!.AsArray().Select(error => error!["path"]!.GetValue<string>())];
    }

    /// <summary>The ETag header of a response without the quotes.</summary>
    public static string ETag(HttpResponseMessage response)
    {
        return response.Headers.ETag?.Tag.Trim('"') ?? throw new InvalidOperationException("The response has no ETag.");
    }
}

/// <summary>Documents that the API tests send.</summary>
internal static class ApiSamples
{
    /// <summary>A process command; the executable does not exist, tests that run it use a fake process runner.</summary>
    public static string Command(string id, bool enabled = true, bool exposeToHa = true, string name = "Say hello")
    {
        return $$"""
            {"id":"{{id}}","name":"{{name}}","enabled":{{(enabled ? "true" : "false")}},"exposeToHa":{{(exposeToHa ? "true" : "false")}},"runAs":"system","action":{"type":"process","file":"hello-tool","args":["hello"]},"captureOutput":true}
            """;
    }

    /// <summary>A command with a required integer variable <c>Level</c> between 1 and 5.</summary>
    public static string CommandWithLevel(string id)
    {
        // Three dollar signs: {{{id}}} is the hole, and the two braces of {{Level}} are text.
        return $$$"""
            {"id":"{{{id}}}","name":"Set level","enabled":true,"exposeToHa":true,"runAs":"system","action":{"type":"process","file":"level-tool","args":["--level","{{Level}}"]},"parameters":[{"name":"Level","type":"int","required":true,"min":1,"max":5}],"captureOutput":true}
            """;
    }

    /// <summary>A sensor that reports whether a process runs.</summary>
    public static string Sensor(string id, string process)
    {
        return $$"""
            {"id":"{{id}}","name":"Process {{id}}","enabled":true,"exposeToHa":true,"kind":"binary_sensor","valueType":"bool","source":{"type":"process_running","process":"{{process}}"},"description":"A test sensor"}
            """;
    }
}
