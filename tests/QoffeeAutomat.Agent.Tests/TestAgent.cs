using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Agent.Tests.Discovery;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests;

/// <summary>
/// The whole agent in an in-process test server with a temporary data directory (simulated platform).
/// Requests carry the session cookie by hand, so a test sees exactly what is sent and received.
/// A request can pretend to come from another address with the header <see cref="RemoteIpHeader"/>.
/// </summary>
internal sealed class TestAgent : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    public const string StrongPassword = "correct horse battery staple";

    private readonly string _dataDirectory;
    private readonly Action<IServiceCollection>? _configure;

    /// <param name="dataDirectory">The temporary data directory.</param>
    /// <param name="configure">Registers or replaces services after the agent's own and the defaults of this class.</param>
    public TestAgent(string dataDirectory, Action<IServiceCollection>? configure = null)
    {
        _dataDirectory = dataDirectory;
        _configure = configure;
    }

    public string SecretsDirectory => Path.Combine(_dataDirectory, "secrets");

    public string SetupTokenPath => Path.Combine(SecretsDirectory, SetupTokenService.FileName);

    public string ReadSetupToken() => File.ReadAllText(SetupTokenPath).Trim();

    /// <summary>Creates a client. An https base address makes the request secure (Secure cookie, pairing allowed).</summary>
    public HttpClient CreateApiClient(bool https = false)
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
    }

    /// <summary>Sends a request; the anti-forgery header is added unless <paramref name="csrf"/> is false.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookie = null,
        bool csrf = true,
        string? json = null,
        string? remoteIp = null,
        string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (csrf)
        {
            request.Headers.Add(CsrfHeaderFilter.HeaderName, CsrfHeaderFilter.HeaderValue);
        }

        if (remoteIp is not null)
        {
            request.Headers.Add(RemoteIpHeader, remoteIp);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    /// <summary>Sets the password through the setup endpoint and returns the session cookie (<c>name=value</c>).</summary>
    public async Task<string> SetUpAsync(HttpClient client, string password = StrongPassword)
    {
        string json = "{\"setupToken\":\"" + ReadSetupToken() + "\",\"password\":\"" + password + "\"}";
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/setup", json: json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return CookieFrom(response);
    }

    /// <summary>Signs in and returns the session cookie.</summary>
    public static async Task<string> LoginAsync(HttpClient client, string password = StrongPassword)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", json: "{\"password\":\"" + password + "\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return CookieFrom(response);
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
        return Assert.Single(values!, value => value.StartsWith("qoffeeautomat.session=", StringComparison.Ordinal));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAppPaths>();
            services.AddSingleton<IAppPaths>(AppPaths.ForRoot(_dataDirectory));
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter());

            // No test touches the network (T216): the executable's multicast DNS responder is replaced by the
            // recording advertiser of the simulated platform, and network changes only happen when a test raises them.
            services.RemoveAll<IServiceAdvertiser>();
            services.AddSingleton<IServiceAdvertiser>(provider => provider.GetRequiredService<SimulatedPlatform>().Advertiser);
            services.RemoveAll<INetworkChangeSource>();
            services.AddSingleton<FakeNetworkChangeSource>();
            services.AddSingleton<INetworkChangeSource>(provider => provider.GetRequiredService<FakeNetworkChangeSource>());

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
