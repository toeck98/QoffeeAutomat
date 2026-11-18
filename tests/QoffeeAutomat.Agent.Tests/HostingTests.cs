using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Tests;

/// <summary>Host bootstrap acceptance tests of T201.</summary>
[Trait("Category", "Hosting")]
public sealed class HostingTests : IDisposable
{
    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Healthz_answers_200_with_the_simulated_platform_on_a_real_listener()
    {
        var options = new AgentOptions(Platform: "simulated", DataDir: _data.Path, UiPort: 0, LinkPort: 0);
        WebApplication app = AgentHost.Build(options);
        await app.StartAsync();
        try
        {
            string address = Assert.Single(LoopbackAddresses(app), candidate => candidate.StartsWith("http://", StringComparison.Ordinal));
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.GetAsync(new Uri(address + AgentHost.HealthPath));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_loopback_listener_binds_to_127_0_0_1_only()
    {
        var options = new AgentOptions(Platform: "simulated", DataDir: _data.Path, UiPort: 0, LinkPort: 0);
        WebApplication app = AgentHost.Build(options);
        await app.StartAsync();
        try
        {
            IServerAddressesFeature feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            string[] addresses = [.. feature.Addresses];

            // The UI and API listener is plain HTTP and bound to 127.0.0.1 only (D-018).
            string[] plain = [.. addresses.Where(address => address.StartsWith("http://", StringComparison.Ordinal))];
            var loopback = new Uri(Assert.Single(plain));
            Assert.Equal(IPAddress.Loopback, IPAddress.Parse(loopback.Host));

            // Since T211 the certificate exists, so the HTTPS link listener is open next to it and is
            // the only other listener.
            Assert.Single(addresses, address => address.StartsWith("https://", StringComparison.Ordinal));
            Assert.Equal(2, addresses.Length);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void The_listener_plan_opens_https_on_all_interfaces_only_with_a_certificate()
    {
        IReadOnlyList<ListenerSpec> without = ListenerPlan.Create(8480, 8481, hasCertificate: false);
        ListenerSpec ui = Assert.Single(without);
        Assert.Equal(IPAddress.Loopback, ui.Address);
        Assert.False(ui.UseHttps);

        IReadOnlyList<ListenerSpec> with = ListenerPlan.Create(8480, 8481, hasCertificate: true);
        Assert.Equal(2, with.Count);
        Assert.Equal(IPAddress.Loopback, with[0].Address);
        Assert.Equal(IPAddress.Any, with[1].Address);
        Assert.True(with[1].UseHttps);
    }

    [Fact]
    public async Task WebApplicationFactory_starts_the_host_with_a_temporary_data_directory()
    {
        await using var factory = new AgentFactory(_data.Path);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(AgentHost.HealthPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The config store loaded below the temporary directory and nowhere else.
        var store = factory.Services.GetRequiredService<IConfigStore>();
        Assert.NotNull(store);
        Assert.True(Directory.Exists(Path.Combine(_data.Path, "config")) || Directory.GetDirectories(_data.Path).Length > 0);
    }

    [Fact]
    public void An_unavailable_platform_is_a_startup_error()
    {
        var options = new AgentOptions(Platform: "windows", DataDir: _data.Path);

        // Off Windows the platform needs a Windows system; the net10.0 build of the agent (this test project) has no
        // Windows platform at all (T301). Either way the simulated platform is not substituted silently when windows is requested.
        Assert.Throws<AgentStartupException>(() => AgentHost.Build(options));
    }

    [Fact]
    public void Data_directory_option_wins_over_the_environment_and_defaults()
    {
        AppPaths fromOption = AppPaths.Resolve("/opt/option", "/opt/env", HostOs.Linux, "/programdata");
        Assert.Equal(Path.GetFullPath("/opt/option"), Path.GetDirectoryName(fromOption.ConfigDirectory));

        AppPaths fromEnvironment = AppPaths.Resolve(null, "/opt/env", HostOs.Windows, "/programdata");
        Assert.Equal(Path.GetFullPath("/opt/env"), Path.GetDirectoryName(fromEnvironment.ConfigDirectory));

        AppPaths linux = AppPaths.Resolve(null, null, HostOs.Linux, "/programdata");
        Assert.Equal("/etc/qoffeeautomat", linux.ConfigDirectory);

        AppPaths windows = AppPaths.Resolve(null, null, HostOs.Windows, "/programdata");
        Assert.Equal(
            Path.GetFullPath(Path.Combine("/programdata", AppPaths.ProductDirectoryName)),
            Path.GetDirectoryName(windows.ConfigDirectory));
    }

    private static IEnumerable<string> LoopbackAddresses(WebApplication app)
    {
        IServerAddressesFeature feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        return feature.Addresses;
    }

    private sealed class AgentFactory : WebApplicationFactory<Program>
    {
        private readonly string _dataDirectory;

        static AgentFactory()
        {
            // The entry point starts the host with an empty command line; the tests run on the simulated platform, not the real Linux one (T802).
            Environment.SetEnvironmentVariable("QOFFEE_PLATFORM", "simulated");
        }

        public AgentFactory(string dataDirectory) => _dataDirectory = dataDirectory;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAppPaths>();
                services.AddSingleton<IAppPaths>(AppPaths.ForRoot(_dataDirectory));
            });
        }
    }
}
