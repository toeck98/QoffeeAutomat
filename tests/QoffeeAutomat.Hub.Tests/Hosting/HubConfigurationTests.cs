using System.Net;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Tests.Hosting;

/// <summary>The command line, the environment variables and the data directory of the hub (T902).</summary>
[Trait("Category", "Hosting")]
public sealed class HubConfigurationTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string> map = variables.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private static HubSettings Resolve(HubOptions options, Func<string, string?> environment, bool inContainer = false)
    {
        return HubSettings.Resolve(options, environment, inContainer, "/home/someone/.local/share");
    }

    [Fact]
    public void In_a_container_the_data_directory_is_slash_data()
    {
        HubPaths paths = HubPaths.Resolve(null, null, inContainer: true, "/home/someone/.local/share");

        Assert.Equal(Path.GetFullPath("/data"), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "hub.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.Root, "secrets"), paths.SecretsDirectory);
    }

    [Fact]
    public void Outside_a_container_the_default_is_below_the_application_data_folder()
    {
        HubPaths paths = HubPaths.Resolve(null, null, inContainer: false, "/home/someone/.local/share");

        Assert.Equal(Path.GetFullPath("/home/someone/.local/share/QoffeeAutomatHub"), paths.Root);
    }

    [Fact]
    public void The_option_wins_over_the_environment_variable_which_wins_over_the_default()
    {
        Assert.Equal(Path.GetFullPath("/from/option"), HubPaths.Resolve("/from/option", "/from/env", inContainer: true, "/x").Root);
        Assert.Equal(Path.GetFullPath("/from/env"), HubPaths.Resolve(null, "/from/env", inContainer: true, "/x").Root);
        Assert.Equal(Path.GetFullPath("/from/env"), HubPaths.Resolve("  ", "/from/env", inContainer: true, "/x").Root);
    }

    [Fact]
    public void The_command_line_is_parsed_and_unknown_options_go_to_aspnet_core()
    {
        HubOptions options = HubCommandLine.Parse(
            ["--data-dir", "/srv/hub", "--port=9000", "--bind", "127.0.0.1", "--tls", "none", "--trusted-proxy", "10.0.0.1", "--trusted-proxy=172.17.0.0/16", "--environment", "Development", "--log-format", "json", "--name", "Home hub"]);

        Assert.Null(options.Error);
        Assert.Equal("/srv/hub", options.DataDir);
        Assert.Equal(9000, options.Port);
        Assert.Equal("127.0.0.1", options.Bind);
        Assert.Equal(HubTlsMode.None, options.Tls);
        Assert.Equal(HubLogFormat.Json, options.LogFormat);
        Assert.Equal("Home hub", options.Name);
        string[] expectedProxies = ["10.0.0.1", "172.17.0.0/16"];
        string[] expectedPassThrough = ["--environment", "Development"];
        Assert.Equal(expectedProxies, options.TrustedProxies);
        Assert.Equal(expectedPassThrough, options.PassThrough);
    }

    [Theory]
    [InlineData("--port", "99999")]
    [InlineData("--port", "abc")]
    [InlineData("--tls", "maybe")]
    [InlineData("--log-format", "xml")]
    public void A_wrong_value_is_a_usage_error(string option, string value)
    {
        HubOptions options = HubCommandLine.Parse([option, value]);

        Assert.NotNull(options.Error);
    }

    [Fact]
    public void An_option_without_a_value_is_a_usage_error()
    {
        Assert.NotNull(HubCommandLine.Parse(["--data-dir"]).Error);
    }

    [Fact]
    public void Help_and_version_are_recognised()
    {
        Assert.True(HubCommandLine.Parse(["--help"]).ShowHelp);
        Assert.True(HubCommandLine.Parse(["--version"]).ShowVersion);
    }

    [Fact]
    public void The_defaults_are_port_8490_on_all_addresses_with_a_self_signed_certificate()
    {
        HubSettings settings = Resolve(new HubOptions(), Env());

        Assert.Equal(8490, settings.Port);
        Assert.Equal(IPAddress.Any, settings.Bind);
        Assert.Equal(HubTlsMode.SelfSigned, settings.Tls);
        Assert.Empty(settings.TrustedProxies);
    }

    [Fact]
    public void The_environment_fills_in_what_the_command_line_leaves_out()
    {
        HubSettings settings = Resolve(
            new HubOptions { Port = 9100 },
            Env(
                (HubSettings.PortEnvironmentVariable, "9200"),
                (HubSettings.BindEnvironmentVariable, "127.0.0.1"),
                (HubSettings.NameEnvironmentVariable, "Basement"),
                (HubSettings.TlsEnvironmentVariable, "none"),
                (HubSettings.TrustedProxiesEnvironmentVariable, "10.1.1.1, 192.168.0.0/24"),
                (HubPaths.DataDirEnvironmentVariable, "/var/lib/hub")));

        Assert.Equal(9100, settings.Port);
        Assert.Equal(IPAddress.Loopback, settings.Bind);
        Assert.Equal("Basement", settings.InitialName);
        Assert.Equal(HubTlsMode.None, settings.Tls);
        Assert.Equal(2, settings.TrustedProxies.Count);
        Assert.Equal(Path.GetFullPath("/var/lib/hub"), settings.Paths.Root);
    }

    [Fact]
    public void Plain_http_needs_a_trusted_proxy()
    {
        var exception = Assert.Throws<HubStartupException>(() => Resolve(new HubOptions { Tls = HubTlsMode.None }, Env()));

        Assert.Contains("--trusted-proxy", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HubTlsMode.None, Resolve(new HubOptions { Tls = HubTlsMode.None, TrustedProxies = ["10.0.0.1"] }, Env()).Tls);
    }

    [Fact]
    public void A_user_certificate_needs_an_existing_file()
    {
        Assert.Throws<HubStartupException>(() => Resolve(new HubOptions { Tls = HubTlsMode.Certificate }, Env()));
        Assert.Throws<HubStartupException>(() => Resolve(new HubOptions { Tls = HubTlsMode.Certificate, TlsCertificate = "/no/such/file.pfx" }, Env()));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.1/8")]
    public void A_wrong_trusted_proxy_stops_the_start(string proxy)
    {
        Assert.Throws<HubStartupException>(() => Resolve(new HubOptions { TrustedProxies = [proxy] }, Env()));
    }

    [Fact]
    public void Wrong_environment_values_stop_the_start()
    {
        Assert.Throws<HubStartupException>(() => Resolve(new HubOptions(), Env((HubSettings.PortEnvironmentVariable, "many"))));
        Assert.Throws<HubStartupException>(() => Resolve(new HubOptions(), Env((HubSettings.BindEnvironmentVariable, "somewhere"))));
        Assert.Throws<HubStartupException>(() => Resolve(new HubOptions(), Env((HubSettings.TlsEnvironmentVariable, "maybe"))));
    }

    [Fact]
    public void The_log_format_is_json_in_a_container_and_text_elsewhere()
    {
        Assert.Equal(HubLogFormat.Json, HubSettings.ResolveLogFormat(new HubOptions(), Env((HubSettings.RunningInContainerEnvironmentVariable, "true"))));
        Assert.Equal(HubLogFormat.Text, HubSettings.ResolveLogFormat(new HubOptions(), Env()));
        Assert.Equal(HubLogFormat.Text, HubSettings.ResolveLogFormat(new HubOptions(), Env((HubSettings.LogFormatEnvironmentVariable, "text"), (HubSettings.RunningInContainerEnvironmentVariable, "true"))));
        Assert.Equal(HubLogFormat.Json, HubSettings.ResolveLogFormat(new HubOptions { LogFormat = HubLogFormat.Json }, Env()));
    }
}
