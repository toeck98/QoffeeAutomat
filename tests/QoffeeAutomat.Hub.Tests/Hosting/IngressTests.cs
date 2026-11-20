using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Tests.Hosting;

/// <summary>
/// Home Assistant ingress listener, Supervisor peer restriction and user-header authentication (PLAN.md S20, T918).
/// </summary>
[Trait("Category", "Hosting")]
public sealed class IngressTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Settings_resolve_ingress_environment_variables()
    {
        using var directory = new TempDirectory();
        var env = new Dictionary<string, string?>
        {
            [HubSettings.IngressPortEnvironmentVariable] = "8099",
            [HubSettings.IngressAllowedAddressEnvironmentVariable] = "172.30.32.2",
            [HubSettings.IngressRoleEnvironmentVariable] = HubRoles.Operator,
        };

        HubSettings settings = HubSettings.Resolve(
            new HubOptions { DataDir = directory.Below("data"), Port = 8490 },
            name => env.GetValueOrDefault(name),
            inContainer: false,
            directory.Below("local"));

        Assert.Equal(8099, settings.IngressPort);
        Assert.Equal(IPAddress.Parse("172.30.32.2"), settings.IngressAllowedAddress);
        Assert.Equal(HubRoles.Operator, settings.IngressRole);
    }

    [Fact]
    public void Settings_reject_invalid_ingress_role_or_port_collision()
    {
        using var directory = new TempDirectory();
        var badRole = new Dictionary<string, string?>
        {
            [HubSettings.IngressPortEnvironmentVariable] = "8099",
            [HubSettings.IngressRoleEnvironmentVariable] = "superuser",
        };
        Assert.Throws<HubStartupException>(() => HubSettings.Resolve(
            new HubOptions { DataDir = directory.Below("d1"), Port = 8490 },
            name => badRole.GetValueOrDefault(name),
            inContainer: false,
            directory.Below("l1")));

        var collidingPort = new Dictionary<string, string?>
        {
            [HubSettings.IngressPortEnvironmentVariable] = "8490",
        };
        Assert.Throws<HubStartupException>(() => HubSettings.Resolve(
            new HubOptions { DataDir = directory.Below("d2"), Port = 8490 },
            name => collidingPort.GetValueOrDefault(name),
            inContainer: false,
            directory.Below("l2")));
    }

    [Fact]
    public async Task Gate_middleware_rejects_requests_from_non_supervisor_addresses()
    {
        var settings = new HubSettings(HubPaths.ForRoot("/tmp/qoffeeautomat-hub-ingress-test"))
        {
            IngressPort = 8099,
            IngressAllowedAddress = IPAddress.Parse("172.30.32.2"),
        };

        bool reachedNext = false;
        var middleware = new IngressGateMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, settings);

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Connection.LocalPort = 8099;
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        context.Request.Headers["X-Forwarded-For"] = "172.30.32.2";
        context.Request.Headers["X-Remote-User-Name"] = "attacker";

        await middleware.InvokeAsync(context);

        Assert.False(reachedNext);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Ingress_listener_authenticates_supervisor_and_rejects_other_addresses()
    {
        using var directory = new TempDirectory();
        int mainPort = FreePort();
        int ingressPort = FreePort();
        var options = new HubOptions
        {
            DataDir = directory.Below("hub-data"),
            Port = mainPort,
            Bind = "127.0.0.1",
            Tls = HubTlsMode.SelfSigned,
            LogFormat = HubLogFormat.Text,
        };

        HubSettings baseSettings = HubSettings.Resolve(options, _ => null, inContainer: false, directory.Below("local"));
        HubSettings allowedSettings = baseSettings with
        {
            IngressPort = ingressPort,
            IngressAllowedAddress = IPAddress.Loopback,
            IngressRole = HubRoles.Operator,
        };

        await using (WebApplication app = HubHost.Build(options, services => services.AddSingleton(allowedSettings)))
        {
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                // 1. Allowed Supervisor address + HA user header -> authenticated with configured IngressRole.
                using var ingressRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ingressPort}/api/v1/auth/me");
                ingressRequest.Headers.Add("X-Remote-User-Id", "ha-user-1");
                ingressRequest.Headers.Add("X-Remote-User-Name", "alice");
                using HttpResponseMessage ingressResponse = await http.SendAsync(ingressRequest);
                Assert.Equal(HttpStatusCode.OK, ingressResponse.StatusCode);
                HubSessionInfo? session = await ingressResponse.Content.ReadFromJsonAsync(HubApiJsonContext.Default.HubSessionInfo);
                Assert.NotNull(session);
                Assert.True(session.Authenticated);
                Assert.Equal("ha:alice", session.Name);
                Assert.Equal(HubRoles.Operator, session.Role);
                Assert.Equal(AuthMethods.Ingress, session.Method);

                // 2. Main listener ignores X-Remote-User-* headers completely.
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                };
                using var mainHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                using var mainRequest = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{mainPort}/api/v1/auth/me");
                mainRequest.Headers.Add("X-Remote-User-Id", "ha-user-1");
                mainRequest.Headers.Add("X-Remote-User-Name", "alice");
                using HttpResponseMessage mainResponse = await mainHttp.SendAsync(mainRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, mainResponse.StatusCode);
            }
            finally
            {
                await app.StopAsync();
            }
        }

        // 3. Request from another address (127.0.0.1 when only 172.30.32.2 is allowed) is rejected with 403.
        int rejectedPort = FreePort();
        HubSettings restrictedSettings = baseSettings with
        {
            IngressPort = rejectedPort,
            IngressAllowedAddress = IPAddress.Parse("172.30.32.2"),
            IngressRole = HubRoles.Admin,
        };
        await using WebApplication restrictedApp = HubHost.Build(options, services => services.AddSingleton(restrictedSettings));
        await restrictedApp.StartAsync();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{rejectedPort}/api/v1/auth/me");
            req.Headers.Add("X-Remote-User-Name", "alice");
            using HttpResponseMessage resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally
        {
            await restrictedApp.StopAsync();
        }
    }
}
