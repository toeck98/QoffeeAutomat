using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Tests.Security;

/// <summary>
/// The whole hub in an in-process test server with a temporary data directory. A request can pretend to come from another address with
/// the header <see cref="RemoteIpHeader"/>. Time is a <see cref="ManualTimeProvider"/>.
/// </summary>
internal sealed class HubFactory : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    private readonly HubSettings _settings;
    private readonly ManualTimeProvider _clock;
    private readonly LogCapture _logs;
    private readonly Action<IServiceCollection>? _configure;

    static HubFactory()
    {
        // Prevent WebApplication.CreateBuilder from allocating Linux inotify FileSystemWatchers on every test host.
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
    }

    public HubFactory(HubSettings settings, ManualTimeProvider clock, LogCapture logs, Action<IServiceCollection>? configure)
    {
        _settings = settings;
        _clock = clock;
        _logs = logs;
        _configure = configure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<HubSettings>();
            services.AddSingleton(_settings);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<ILoggerProvider>(_logs);
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter());
            _configure?.Invoke(services);
        });
    }

    /// <summary>Lets a test pretend that a request comes from another address.</summary>
    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (HttpContext context, Func<Task> proceed) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteIpHeader, out Microsoft.Extensions.Primitives.StringValues value)
                        && IPAddress.TryParse(value.ToString(), out IPAddress? address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    await proceed();
                });
                next(app);
            };
        }
    }
}

/// <summary>A client of the test hub that carries a session cookie or an API token and the anti-forgery header, by hand, so a test sees exactly what is sent.</summary>
internal sealed class HubApiClient(HttpClient client)
{
    public string? Cookie { get; set; }

    public string? BearerToken { get; set; }

    public bool SendCsrf { get; set; } = true;

    public string? RemoteIp { get; set; }

    public string? Origin { get; set; }

    /// <summary>Sends a GET that asks for <c>text/event-stream</c> and returns as soon as the headers are there, so that a test reads the events while the stream is open.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string path) => SendAsync(HttpMethod.Get, path, null, "text/event-stream", HttpCompletionOption.ResponseHeadersRead);

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? accept = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, path);
        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        if (Cookie is not null)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        if (BearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        }

        if (SendCsrf)
        {
            request.Headers.Add(CsrfHeaderFilter.HeaderName, CsrfHeaderFilter.HeaderValue);
        }

        if (RemoteIp is not null)
        {
            request.Headers.Add(HubFactory.RemoteIpHeader, RemoteIp);
        }

        if (Origin is not null)
        {
            request.Headers.Add("Origin", Origin);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, completion);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<HttpResponseMessage> PostAsync(string path, string? json = null) => SendAsync(HttpMethod.Post, path, json);

    public Task<HttpResponseMessage> PatchAsync(string path, string json) => SendAsync(HttpMethod.Patch, path, json);

    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    /// <summary>Signs in with a user name and a password; the client carries the session cookie afterwards.</summary>
    public async Task LogInAsync(string username, string password)
    {
        using HttpResponseMessage response = await PostAsync("/api/v1/auth/login", Json(("username", username), ("password", password)));
        await HubAssert.JsonAsync(response);
        Cookie = HubTestHost.CookieFrom(response);
    }

    /// <summary>Builds a small JSON object body.</summary>
    public static string Json(params (string Name, object? Value)[] members)
    {
        var node = new JsonObject();
        foreach ((string name, object? value) in members)
        {
            node[name] = value switch
            {
                null => null,
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                _ => throw new ArgumentException("Unsupported value for " + name),
            };
        }

        return node.ToJsonString();
    }
}

/// <summary>Assertions on API responses that show the body when they fail.</summary>
internal static class HubAssert
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

    /// <summary>Checks the status of a response that has no JSON body.</summary>
    public static void Status(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}.");
    }
}

/// <summary>
/// The hub in a temporary data directory with the first-run setup done: an administrator signed in, and on request an operator and a viewer.
/// Passwords are the product's (PBKDF2 with 600 000 iterations), so a host costs a second or so; tests that only read share nothing and start their own.
/// </summary>
internal sealed class HubTestHost : IAsyncDisposable
{
    public const string AdminName = "admin";
    public const string OperatorName = "olivia";
    public const string ViewerName = "victor";
    public const string StrongPassword = "correct horse battery staple";

    private readonly TempDirectory _data = new();
    private readonly HubFactory _factory;
    private readonly List<HttpClient> _clients = [];
    private bool _disposed;

    private HubTestHost(IReadOnlyList<IPNetwork> proxies, Action<IServiceCollection>? configure)
    {
        var settings = new HubSettings(HubPaths.ForRoot(_data.Path)) { TrustedProxies = proxies };
        _factory = new HubFactory(settings, Clock, Logs, configure);
        Anonymous = CreateClient();
        Admin = CreateClient();
        Operator = CreateClient();
        Viewer = CreateClient();
    }

    public ManualTimeProvider Clock { get; } = new();

    public LogCapture Logs { get; } = new();

    /// <summary>A client without credentials.</summary>
    public HubApiClient Anonymous { get; }

    /// <summary>A client signed in as the administrator.</summary>
    public HubApiClient Admin { get; private set; }

    /// <summary>A client signed in as an operator (only when the host was started with users).</summary>
    public HubApiClient Operator { get; private set; }

    /// <summary>A client signed in as a viewer (only when the host was started with users).</summary>
    public HubApiClient Viewer { get; private set; }

    public string DataPath => _data.Path;

    /// <summary>Opens a WebSocket to the in-process hub, for an agent or a test client.</summary>
    public Microsoft.AspNetCore.TestHost.WebSocketClient CreateWebSocketClient() => _factory.Server.CreateWebSocketClient();

    /// <summary>The message handler of the in-process hub, for an HTTP client that is not made by this host.</summary>
    public HttpMessageHandler CreateHandler() => _factory.Server.CreateHandler();

    public string SetupTokenPath => Path.Combine(_data.Path, "setup-token.txt");

    public T Service<T>()
        where T : notnull
    {
        return _factory.Services.GetRequiredService<T>();
    }

    /// <summary>Starts the hub, creates the administrator through the setup route and, when asked, an operator and a viewer.</summary>
    public static async Task<HubTestHost> StartAsync(bool withUsers = false, IReadOnlyList<IPNetwork>? proxies = null, Action<IServiceCollection>? configure = null)
    {
        var host = new HubTestHost(proxies ?? [], configure);
        string token = (await File.ReadAllTextAsync(host.SetupTokenPath)).Trim();
        using (HttpResponseMessage response = await host.Anonymous.PostAsync(
            "/api/v1/auth/setup",
            HubApiClient.Json(("setupToken", token), ("username", AdminName), ("password", StrongPassword))))
        {
            await HubAssert.JsonAsync(response);
            host.Admin.Cookie = CookieFrom(response);
        }

        if (withUsers)
        {
            await host.CreateUserAsync(OperatorName, HubRoles.Operator);
            await host.CreateUserAsync(ViewerName, HubRoles.Viewer);
            await host.Operator.LogInAsync(OperatorName, StrongPassword);
            await host.Viewer.LogInAsync(ViewerName, StrongPassword);
        }

        return host;
    }

    /// <summary>Starts the hub without doing the setup; the setup token file is there.</summary>
    public static HubTestHost StartFresh(IReadOnlyList<IPNetwork>? proxies = null, Action<IServiceCollection>? configure = null)
    {
        return new HubTestHost(proxies ?? [], configure);
    }

    /// <summary>The signed-in client of a role (the host must have been started with users for the operator and the viewer).</summary>
    public HubApiClient ClientFor(string role)
    {
        return role switch
        {
            HubRoles.Admin => Admin,
            HubRoles.Operator => Operator,
            _ => Viewer,
        };
    }

    /// <summary>A new client with a new session of the account of a role (for operations that end the session).</summary>
    public async Task<HubApiClient> FreshClientAsync(string role)
    {
        HubApiClient client = CreateClient();
        string name = role switch
        {
            HubRoles.Admin => AdminName,
            HubRoles.Operator => OperatorName,
            _ => ViewerName,
        };
        await client.LogInAsync(name, StrongPassword);
        return client;
    }

    /// <summary>Creates a client of the test server. An https base address makes the request secure (Secure cookie).</summary>
    public HubApiClient CreateClient()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
        _clients.Add(client);
        return new HubApiClient(client);
    }

    /// <summary>Creates an account through the API as the administrator and returns its id.</summary>
    public async Task<string> CreateUserAsync(string username, string role, string password = StrongPassword)
    {
        using HttpResponseMessage response = await Admin.PostAsync(
            "/api/v1/users",
            HubApiClient.Json(("username", username), ("password", password), ("role", role)));
        JsonNode user = await HubAssert.JsonAsync(response, HttpStatusCode.Created);
        return user["id"]!.GetValue<string>();
    }

    /// <summary>Creates an API token through the API as the administrator and returns its secret.</summary>
    public async Task<string> CreateTokenAsync(string name, string role, string? expiresAt = null)
    {
        string body = expiresAt is null
            ? HubApiClient.Json(("name", name), ("role", role))
            : HubApiClient.Json(("name", name), ("role", role), ("expiresAt", expiresAt));
        using HttpResponseMessage response = await Admin.PostAsync("/api/v1/tokens", body);
        JsonNode created = await HubAssert.JsonAsync(response, HttpStatusCode.Created);
        return created["secret"]!.GetValue<string>();
    }

    /// <summary>The number of entries in the audit log.</summary>
    public async Task<long> AuditCountAsync()
    {
        return await Service<AuditLog>().CountAsync(CancellationToken.None);
    }

    /// <summary>The <c>name=value</c> part of the session cookie that a response sets.</summary>
    public static string CookieFrom(HttpResponseMessage response)
    {
        string setCookie = SetCookieHeader(response);
        int end = setCookie.IndexOf(';', StringComparison.Ordinal);
        return end < 0 ? setCookie : setCookie[..end];
    }

    public static string SetCookieHeader(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values));
        return Assert.Single(values!, value => value.StartsWith(HubSessionCookie.Name + "=", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (HttpClient client in _clients)
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
        _data.Dispose();
    }
}
