using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>
/// The management API is closed unless a route is documented as open (T212, PLAN.md S08, D-017). The
/// tests enumerate the endpoint data source, so a route added later is covered without touching them.
/// </summary>
[Trait("Category", "Auth")]
public sealed partial class RouteProtectionTests : IDisposable
{
    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteParameter();

    [Fact]
    public async Task Every_api_route_except_the_documented_exceptions_returns_401_without_a_session()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        List<(string Method, string Path, string Pattern)> routes = ApiRoutes(agent);

        // The enumeration must really see the API; an empty list would make this test pass for nothing.
        Assert.Contains(routes, route => route.Pattern == "/api/v1/auth/me");
        Assert.Contains(routes, route => route.Pattern == "/api/v1/link/clients/{id}" && route.Method == "DELETE");
        Assert.True(routes.Count >= 10);

        foreach ((string method, string path, string pattern) in routes)
        {
            if (ManagementApi.AnonymousRoutes.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // With and without the anti-forgery header: authentication comes first, so both are 401.
            foreach (bool csrf in new[] { true, false })
            {
                string? body = string.Equals(method, "GET", StringComparison.Ordinal) ? null : "{}";
                using HttpResponseMessage response = await TestAgent.SendAsync(client, new HttpMethod(method), path, csrf: csrf, json: body);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    Assert.Fail(method + " " + pattern + " answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " instead of 401 without a session.");
                }
            }
        }
    }

    [Fact]
    public void Only_the_documented_routes_allow_anonymous_access()
    {
        using var agent = new TestAgent(_data.Path);
        EndpointDataSource source = agent.Services.GetRequiredService<EndpointDataSource>();

        string[] anonymous =
        [
            .. source.Endpoints.OfType<RouteEndpoint>()
                .Where(endpoint => IsApi(endpoint.RoutePattern) && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                .Select(endpoint => endpoint.RoutePattern.RawText!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal),
        ];

        string[] documented = [.. ManagementApi.AnonymousRoutes.Order(StringComparer.Ordinal)];
        Assert.Equal(documented, anonymous);
    }

    [Fact]
    public void Every_api_route_requires_the_management_policy_unless_it_is_documented_as_open()
    {
        using var agent = new TestAgent(_data.Path);
        EndpointDataSource source = agent.Services.GetRequiredService<EndpointDataSource>();

        foreach (RouteEndpoint endpoint in source.Endpoints.OfType<RouteEndpoint>().Where(endpoint => IsApi(endpoint.RoutePattern)))
        {
            string pattern = endpoint.RoutePattern.RawText!;
            if (ManagementApi.AnonymousRoutes.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            IAuthorizeData[] requirements = [.. endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()];
            if (!requirements.Any(data => string.Equals(data.Policy, AuthNames.ManagementPolicy, StringComparison.Ordinal)))
            {
                Assert.Fail(pattern + " does not require the management policy.");
            }

            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    [Fact]
    public async Task Health_pairing_and_static_paths_stay_outside_the_api_protection()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();

        using HttpResponseMessage health = await TestAgent.SendAsync(client, HttpMethod.Get, "/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // The pairing endpoint answers 401 for a wrong code, not for a missing session.
        using HttpClient link = agent.CreateApiClient(https: true);
        using HttpResponseMessage pair = await TestAgent.SendAsync(
            link,
            HttpMethod.Post,
            "/link/v1/pair",
            csrf: false,
            json: "{\"code\":\"123456\",\"client\":{\"kind\":\"tool\",\"name\":\"x\"}}");
        Assert.Equal(HttpStatusCode.Unauthorized, pair.StatusCode);
        Assert.Contains("pairing code", await pair.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_management_api_is_only_served_to_this_pc_while_remote_ui_is_off()
    {
        await using var agent = new TestAgent(_data.Path);
        using HttpClient client = agent.CreateApiClient();
        string cookie = await agent.SetUpAsync(client);

        using HttpResponseMessage remote = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie, remoteIp: "192.168.1.50");
        using HttpResponseMessage remoteLogin = await TestAgent.SendAsync(client, HttpMethod.Post, "/api/v1/auth/login", remoteIp: "192.168.1.50", json: "{\"password\":\"x\"}");
        using HttpResponseMessage local = await TestAgent.SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", cookie, remoteIp: "127.0.0.1");
        using HttpResponseMessage health = await TestAgent.SendAsync(client, HttpMethod.Get, "/healthz", remoteIp: "192.168.1.50");

        Assert.Equal(HttpStatusCode.Forbidden, remote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remoteLogin.StatusCode);
        Assert.Contains("remote_ui_disabled", await remote.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static bool IsApi(RoutePattern pattern)
    {
        return pattern.RawText is { } text && text.StartsWith(ManagementApi.BasePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All method and path combinations below <c>/api/v1</c>, with route parameters replaced by a sample value.</summary>
    private static List<(string Method, string Path, string Pattern)> ApiRoutes(TestAgent agent)
    {
        EndpointDataSource source = agent.Services.GetRequiredService<EndpointDataSource>();
        var routes = new List<(string Method, string Path, string Pattern)>();
        foreach (RouteEndpoint endpoint in source.Endpoints.OfType<RouteEndpoint>().Where(endpoint => IsApi(endpoint.RoutePattern)))
        {
            string pattern = endpoint.RoutePattern.RawText!;
            string path = RouteParameter().Replace(pattern, "sample");
            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (string method in methods)
            {
                routes.Add((method, path, pattern));
            }
        }

        return routes;
    }
}
