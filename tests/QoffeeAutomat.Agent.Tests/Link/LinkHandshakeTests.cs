using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Link;
using LinkOptions = QoffeeAutomat.Agent.Link.LinkOptions;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>
/// The upgrade request, the handshake and the rules for frames of the QoffeeLink server (T210, PLAN.md S07).
/// Every frame that the agent sends in these tests is checked against protocol/schema by the test client.
/// </summary>
[Trait("Category", "Link")]
public sealed class LinkHandshakeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-link-token")]
    public async Task A_connection_without_a_valid_token_receives_http_401_and_is_not_upgraded(string? token)
    {
        await using var host = new LinkTestHost();

        // As a plain request, to see the status code and the body.
        using HttpClient http = host.Agent.CreateApiClient(https: true);
        using HttpResponseMessage refused = await GetLinkAsync(http, token);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("Bearer", refused.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.Unauthorized, problem.RootElement.GetProperty("code").GetString());

        // As a WebSocket upgrade request: the handshake fails and nothing connects.
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LinkTestClient.OpenSocketAsync(host.Agent, token));
        Assert.Contains("401", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Server.ConnectionCount);
    }

    [Fact]
    public async Task A_revoked_token_is_refused_with_http_401()
    {
        await using var host = new LinkTestHost();
        (string token, LinkClient owner) = await host.PairAsync();
        await using (await LinkTestClient.ConnectAndGreetAsync(host.Agent, token))
        {
            Assert.True(await host.Pairing.RevokeAsync(owner.Id, CancellationToken.None));
        }

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LinkTestClient.OpenSocketAsync(host.Agent, token));

        Assert.Contains("401", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_link_needs_https_even_with_a_valid_token()
    {
        await using var host = new LinkTestHost();
        (string token, _) = await host.PairAsync();
        using HttpClient plain = host.Agent.CreateApiClient(https: false);

        using HttpResponseMessage response = await GetLinkAsync(plain, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_without_an_upgrade_or_without_the_sub_protocol_is_a_bad_request()
    {
        await using var host = new LinkTestHost();
        (string token, _) = await host.PairAsync();
        using HttpClient http = host.Agent.CreateApiClient(https: true);

        using HttpResponseMessage notUpgraded = await GetLinkAsync(http, token);
        InvalidOperationException wrongProtocol = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LinkTestClient.OpenSocketAsync(host.Agent, token, "something.else"));

        Assert.Equal(HttpStatusCode.BadRequest, notUpgraded.StatusCode);
        Assert.Contains("400", wrongProtocol.Message, StringComparison.Ordinal);
        Assert.Equal(0, host.Server.ConnectionCount);
    }

    [Fact]
    public async Task The_agent_sends_hello_first_with_the_sub_protocol_the_version_range_and_the_manifest_revision()
    {
        await using var host = new LinkTestHost();
        (string token, _) = await host.PairAsync();
        await using LinkTestClient client = await LinkTestClient.ConnectAsync(host.Agent, token);

        JsonElement hello = client.Frames[0];

        Assert.Equal(ProtocolInfo.SubProtocol, client.SubProtocol);
        Assert.Equal(MessageTypes.Hello, LinkTestClient.TypeOf(hello));
        JsonElement protocol = hello.GetProperty("protocol");
        Assert.Equal(ProtocolInfo.Version, protocol.GetProperty("min").GetInt32());
        Assert.Equal(ProtocolInfo.Version, protocol.GetProperty("max").GetInt32());
        Assert.Equal(host.ManifestSource.Current.Rev, hello.GetProperty("manifest_rev").GetString());
        Assert.False(string.IsNullOrEmpty(hello.GetProperty("boot_id").GetString()));
        Assert.False(string.IsNullOrEmpty(hello.GetProperty("agent").GetProperty("id").GetString()));
        Assert.Equal(1, host.Server.ConnectionCount);
    }

    [Fact]
    public async Task The_manifest_that_get_manifest_returns_validates_and_has_the_revision_of_hello()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();
        JsonElement hello = client.Frames[0];

        JsonElement result = await client.RequestAsync(MessageTypes.GetManifest);

        Assert.True(result.GetProperty("ok").GetBoolean());
        JsonElement manifest = result.GetProperty("data");
        Assert.Empty(LinkSchemas.Validate(manifest, LinkSchemas.Manifest));
        Assert.Equal(hello.GetProperty("manifest_rev").GetString(), manifest.GetProperty("rev").GetString());
        Assert.Equal(hello.GetProperty("agent").GetProperty("id").GetString(), manifest.GetProperty("agent").GetProperty("id").GetString());
        Assert.Contains(manifest.GetProperty("metrics").EnumerateArray(), metric => metric.GetProperty("id").GetString() == "sim.cpu");
    }

    [Fact]
    public async Task A_client_hello_for_an_unsupported_version_gets_unsupported_version_and_close_code_4406()
    {
        await using var host = new LinkTestHost();
        (string token, _) = await host.PairAsync();
        await using LinkTestClient client = await LinkTestClient.ConnectAsync(host.Agent, token);

        await client.SendAsync(LinkTestClient.ClientHello(99));

        // A client_hello has no id; the error travels in a result with id 0 (protocol/README.md).
        JsonElement result = await client.WaitForResultAsync(0);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.UnsupportedVersion, LinkTestClient.ErrorCodeOf(result));
        Assert.Equal(CloseCodes.UnsupportedVersion, await client.WaitForCloseAsync());
    }

    [Theory]
    [InlineData("{\"t\":\"get_manifest\",\"id\":1}")]
    [InlineData("this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"t\":\"client_hello\"}")]
    public async Task A_first_frame_that_is_not_a_valid_client_hello_closes_the_connection_with_4400(string firstFrame)
    {
        await using var host = new LinkTestHost();
        (string token, _) = await host.PairAsync();
        await using LinkTestClient client = await LinkTestClient.ConnectAsync(host.Agent, token);

        await client.SendAsync(firstFrame);

        Assert.Equal(CloseCodes.BadRequest, await client.WaitForCloseAsync());
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the connection to be removed");
    }

    [Fact]
    public async Task A_client_that_does_not_answer_hello_in_time_is_closed_with_4400()
    {
        await using var host = new LinkTestHost(new LinkOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(300) });
        (string token, _) = await host.PairAsync();
        await using LinkTestClient client = await LinkTestClient.ConnectAsync(host.Agent, token);

        Assert.Equal(CloseCodes.BadRequest, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Unknown_message_types_and_frames_only_the_agent_sends_are_ignored_without_closing_the_connection()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await client.SendAsync("{\"t\":\"from_a_later_protocol_version\",\"id\":7,\"payload\":{\"a\":1}}");
        await client.SendAsync("{\"t\":\"state\",\"ts\":1,\"v\":{}}");
        await client.SendAsync("{\"t\":\"bye\",\"reason\":\"shutdown\"}");
        await client.SendAsync(LinkTestClient.ClientHello(ProtocolInfo.Version));
        await client.SendAsync("{\"t\":\"get_manifest\",\"id\":1000,\"unknown_field\":[1,2,3]}");

        // The request after the unknown frames is answered, so the connection is alive and in order.
        JsonElement result = await client.WaitForResultAsync(1000);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(client.IsOpen);
        Assert.Null(client.CloseStatus);
        Assert.DoesNotContain(client.Frames, frame => LinkTestClient.IsResult(frame, 7));
        Assert.Equal(1, host.Server.ConnectionCount);
    }

    [Theory]
    [InlineData("{\"t\":\"subscribe\",\"id\":5}")]
    [InlineData("{\"t\":\"subscribe\",\"id\":5,\"metrics\":\"sim.cpu\"}")]
    [InlineData("{\"t\":\"run\",\"id\":5}")]
    public async Task A_known_request_with_a_broken_body_is_answered_with_bad_request(string frame)
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await client.SendAsync(frame);

        JsonElement result = await client.WaitForResultAsync(5);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.BadRequest, LinkTestClient.ErrorCodeOf(result));
        Assert.True(client.IsOpen);
    }

    [Fact]
    public async Task A_frame_that_is_not_json_after_the_handshake_closes_the_connection_with_4400()
    {
        await using var host = new LinkTestHost();
        await using LinkTestClient client = await host.ConnectAsync();

        await client.SendAsync("}{ definitely not json");

        Assert.Equal(CloseCodes.BadRequest, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task A_frame_over_the_size_limit_closes_the_connection_with_1009()
    {
        await using var host = new LinkTestHost(new LinkOptions { MaxFrameBytes = 2048 });
        await using LinkTestClient client = await host.ConnectAsync();

        await client.SendAsync("{\"t\":\"unknown\",\"padding\":\"" + new string('x', 5000) + "\"}");

        Assert.Equal(CloseCodes.MessageTooBig, await client.WaitForCloseAsync());
    }

    [Fact]
    public void The_defaults_are_the_values_of_the_specification()
    {
        var options = new LinkOptions();

        WebSocketOptions socket = LinkEndpoint.CreateWebSocketOptions(options);

        Assert.Equal(1024 * 1024, options.MaxFrameBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), options.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(75), options.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), socket.KeepAliveInterval);

        // The wait for the answer to a ping is what is left of the 75 seconds; older frameworks do not have the setting.
        PropertyInfo? timeout = typeof(WebSocketOptions).GetProperty("KeepAliveTimeout", BindingFlags.Public | BindingFlags.Instance);
        if (timeout is { PropertyType: var type } && type == typeof(TimeSpan))
        {
            Assert.Equal(TimeSpan.FromSeconds(45), (TimeSpan)timeout.GetValue(socket)!);
        }
    }

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer   abc  ", "abc")]
    [InlineData("Basic abc", null)]
    [InlineData("Bearer", null)]
    public void The_bearer_token_is_read_from_the_authorization_header(string header, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = header;

        Assert.Equal(expected, LinkEndpoint.ReadBearerToken(context.Request));
    }

    private static async Task<HttpResponseMessage> GetLinkAsync(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolInfo.LinkPath);
        if (token is not null)
        {
            request.Headers.Authorization = string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }
}
