using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>
/// The transcripts of the reverse handshake in <c>protocol/fixtures/reverse</c> (ADR 0003) replayed against the agent: what the agent sends in
/// the upgrade request, which frame it sends first, and what it does when the peer answers as the fixture says (HTTP 401, or the close code 4401
/// after <c>hello</c>). The Python tests of the integration replay the same files against the endpoint of Home Assistant.
/// </summary>
[Trait("Category", "OutboundLink")]
public sealed class ReverseHandshakeFixtureTests
{
    public static TheoryData<string> HandshakeNames { get; } =
    [
        "handshake.json",
        "handshake.unauthorized.json",
        "handshake.revoked.json",
        "handshake.wrong_agent.json",
    ];

    [Theory]
    [MemberData(nameof(HandshakeNames))]
    public async Task The_agent_does_what_the_transcript_describes(string name)
    {
        using JsonDocument transcript = JsonDocument.Parse(ReverseFixtures.ReadReverse(name));
        JsonElement root = transcript.RootElement;
        JsonElement request = root.GetProperty("request");
        string authorization = request.GetProperty("headers").GetProperty("Authorization").GetString()!;
        string token = authorization["Bearer ".Length..];
        int status = root.GetProperty("response").GetProperty("status").GetInt32();
        string? expectedState = root.TryGetProperty("agent_state", out JsonElement agentState) ? agentState.GetString() : null;

        await using FakeHomeAssistant ha = await FakeHomeAssistant.StartAsync();
        await using var host = new LinkTestHost(configure: services => ha.Configure(services));
        ReverseLinkManager manager = host.Agent.Services.GetRequiredService<ReverseLinkManager>();
        ReverseLinkService service = host.Agent.Services.GetRequiredService<ReverseLinkService>();

        // The peer knows the token of the transcript, except when the transcript says that it does not.
        ha.Token = status == 401 ? ReverseLinkEnrollment.CreateToken(RandomNumberGenerator.GetBytes(32)) : token;
        ReverseLinkJoinResult joined = await manager.JoinAsync(new ReverseLinkEnrollment(new Uri(FakeHomeAssistant.Url), token, null).ToEnrollmentString(), CancellationToken.None);
        Assert.True(joined.Joined, joined.Error);

        if (status == 401)
        {
            await TestWait.UntilAsync(() => service.Connector?.State == OutboundLinkState.Rejected, "the agent to be rejected");
            Assert.Equal(0, ha.Accepted);
            Assert.Equal("Bearer " + token, ha.Presented[0]);
            Assert.Equal("rejected", expectedState);
            return;
        }

        await using LinkTestClient peer = await ha.WaitForAgentAsync();

        // The upgrade request is the one of the transcript: the path, the bearer token and the sub-protocol.
        Assert.Equal(FakeHomeAssistant.Path, request.GetProperty("path").GetString());
        Assert.Equal(authorization, ha.Presented[0]);
        Assert.Equal(ProtocolInfo.SubProtocol, request.GetProperty("headers").GetProperty("Sec-WebSocket-Protocol").GetString());

        // The frames of the transcript, in order: the ones of the agent arrive, the ones of the client are sent.
        foreach (JsonElement step in root.GetProperty("frames").EnumerateArray())
        {
            string frameText = ReverseFixtures.ReadFrame(step.GetProperty("fixture").GetString()!);
            using JsonDocument expected = JsonDocument.Parse(frameText);
            string type = expected.RootElement.GetProperty("t").GetString()!;
            if (step.GetProperty("from").GetString() == "agent")
            {
                Assert.Equal(type, LinkTestClient.TypeOf(peer.Frames[0]));
            }
            else
            {
                await peer.SendAsync(frameText);
            }
        }

        if (root.TryGetProperty("close", out JsonElement close))
        {
            await peer.CloseWithAsync(close.GetProperty("code").GetInt32(), close.GetProperty("reason").GetString()!);
            await TestWait.UntilAsync(() => service.Connector?.State == OutboundLinkState.Rejected, "the agent to be rejected");
            Assert.Equal("rejected", expectedState);
            return;
        }

        // Without a close the connection is an ordinary QoffeeLink connection: the agent answers requests.
        JsonElement manifest = await peer.RequestAsync(MessageTypes.GetManifest);
        Assert.True(manifest.GetProperty("ok").GetBoolean());
        Assert.Null(expectedState);
        Assert.Equal(OutboundLinkState.Connected, service.Connector!.State);
    }

    [Fact]
    public void Every_transcript_refers_only_to_fixtures_that_exist_and_uses_the_endpoint_of_the_adr()
    {
        foreach (string name in HandshakeNames)
        {
            using JsonDocument transcript = JsonDocument.Parse(ReverseFixtures.ReadReverse(name));
            JsonElement root = transcript.RootElement;

            Assert.Equal("/api/qoffeeautomat/reverse/v1", root.GetProperty("request").GetProperty("path").GetString());
            Assert.StartsWith("Bearer qalr_", root.GetProperty("request").GetProperty("headers").GetProperty("Authorization").GetString(), StringComparison.Ordinal);
            foreach (JsonElement step in root.GetProperty("frames").EnumerateArray())
            {
                Assert.NotEmpty(ReverseFixtures.ReadFrame(step.GetProperty("fixture").GetString()!));
            }
        }
    }
}
