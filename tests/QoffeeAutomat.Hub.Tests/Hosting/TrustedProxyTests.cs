using System.Net;
using Microsoft.AspNetCore.Http;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Tests.Hosting;

/// <summary>Forwarded headers are honoured only for configured reverse proxies (T902, PLAN.md S20 "Listeners").</summary>
[Trait("Category", "Hosting")]
public sealed class TrustedProxyTests
{
    private static IPNetwork Network(string text)
    {
        Assert.True(ProxyAddresses.TryParse(text, out IPNetwork network), text + " must parse.");
        return network;
    }

    private static async Task<DefaultHttpContext> RunAsync(string[] proxies, string peer, params (string Name, string Value)[] headers)
    {
        var settings = new HubSettings(HubPaths.ForRoot("/tmp/qoffeeautomat-hub-unit-test"))
        {
            TrustedProxies = [.. proxies.Select(Network)],
        };
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("hub.internal:8490");
        foreach ((string name, string value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        var middleware = new TrustedProxyMiddleware(_ => Task.CompletedTask, settings);
        await middleware.InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task Without_trusted_proxies_the_forwarded_headers_are_ignored()
    {
        DefaultHttpContext context = await RunAsync([], "10.0.0.1", ("X-Forwarded-For", "203.0.113.5"), ("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "evil.example"));

        Assert.Equal(IPAddress.Parse("10.0.0.1"), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("hub.internal:8490", context.Request.Host.Value);
    }

    [Fact]
    public async Task A_request_from_a_trusted_proxy_gets_the_forwarded_address_scheme_and_host()
    {
        DefaultHttpContext context = await RunAsync(["10.0.0.1"], "10.0.0.1", ("X-Forwarded-For", "203.0.113.5"), ("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "hub.example.org"));

        Assert.Equal(IPAddress.Parse("203.0.113.5"), context.Connection.RemoteIpAddress);
        Assert.Equal("https", context.Request.Scheme);
        Assert.True(context.Request.IsHttps);
        Assert.Equal("hub.example.org", context.Request.Host.Value);
    }

    [Fact]
    public async Task A_request_from_any_other_address_cannot_forge_them()
    {
        DefaultHttpContext context = await RunAsync(["10.0.0.1"], "192.0.2.77", ("X-Forwarded-For", "203.0.113.5"), ("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "hub.example.org"));

        Assert.Equal(IPAddress.Parse("192.0.2.77"), context.Connection.RemoteIpAddress);
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("hub.internal:8490", context.Request.Host.Value);
    }

    [Fact]
    public async Task The_client_is_the_first_address_from_the_right_that_is_not_a_trusted_proxy()
    {
        DefaultHttpContext chained = await RunAsync(["10.0.0.1", "10.0.0.2"], "10.0.0.1", ("X-Forwarded-For", "198.51.100.7, 10.0.0.2"));
        DefaultHttpContext spoofed = await RunAsync(["10.0.0.1"], "10.0.0.1", ("X-Forwarded-For", "1.2.3.4, 203.0.113.9"));

        Assert.Equal(IPAddress.Parse("198.51.100.7"), chained.Connection.RemoteIpAddress);

        // What the client wrote at the left end is not what the proxy saw; the proxy appended the real address on the right.
        Assert.Equal(IPAddress.Parse("203.0.113.9"), spoofed.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("203.0.113.5:4711", "203.0.113.5")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("2001:db8::2", "2001:db8::2")]
    public async Task Ports_and_brackets_are_removed_from_the_forwarded_address(string forwarded, string expected)
    {
        DefaultHttpContext context = await RunAsync(["10.0.0.1"], "10.0.0.1", ("X-Forwarded-For", forwarded));

        Assert.Equal(IPAddress.Parse(expected), context.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("203.0.113.5, garbage")]
    [InlineData("1")]
    [InlineData("")]
    public async Task A_malformed_forwarded_address_is_ignored(string forwarded)
    {
        DefaultHttpContext context = await RunAsync(["10.0.0.1"], "10.0.0.1", ("X-Forwarded-For", forwarded));

        Assert.Equal(IPAddress.Parse("10.0.0.1"), context.Connection.RemoteIpAddress);
    }

    [Theory]
    [InlineData("ftp", "http")]
    [InlineData("HTTPS", "https")]
    [InlineData("javascript:alert(1)", "http")]
    public async Task Only_http_and_https_are_taken_as_the_forwarded_scheme(string forwarded, string expected)
    {
        DefaultHttpContext context = await RunAsync(["10.0.0.1"], "10.0.0.1", ("X-Forwarded-Proto", forwarded));

        Assert.Equal(expected, context.Request.Scheme);
    }

    [Theory]
    [InlineData("evil.example/<script>")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task An_invalid_forwarded_host_is_ignored(string forwarded)
    {
        DefaultHttpContext context = await RunAsync(["10.0.0.1"], "10.0.0.1", ("X-Forwarded-Host", forwarded));

        Assert.Equal("hub.internal:8490", context.Request.Host.Value);
    }

    [Theory]
    [InlineData("172.17.5.5", true)]
    [InlineData("172.18.0.1", false)]
    [InlineData("::ffff:172.17.0.9", true)]
    public async Task A_proxy_can_be_a_network(string peer, bool trusted)
    {
        DefaultHttpContext context = await RunAsync(["172.17.0.0/16"], peer, ("X-Forwarded-For", "203.0.113.5"));

        Assert.Equal(trusted, Equals(IPAddress.Parse("203.0.113.5"), context.Connection.RemoteIpAddress));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("2001:db8::/32", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData(" 10.0.0.1 ", true)]
    [InlineData("", false)]
    [InlineData("10.0.0.1/8", false)]
    [InlineData("10.0.0.0/40", false)]
    [InlineData("hub.example.org", false)]
    public void A_proxy_is_an_address_or_a_cidr_range(string text, bool valid)
    {
        Assert.Equal(valid, ProxyAddresses.TryParse(text, out _));
    }

    [Fact]
    public void An_ipv4_address_mapped_into_ipv6_counts_as_the_ipv4_address()
    {
        IPNetwork[] proxies = [Network("10.0.0.1")];

        Assert.True(ProxyAddresses.Contains(proxies, IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.True(ProxyAddresses.Contains(proxies, IPAddress.Parse("10.0.0.1")));
        Assert.False(ProxyAddresses.Contains(proxies, IPAddress.Parse("10.0.0.2")));
    }
}
