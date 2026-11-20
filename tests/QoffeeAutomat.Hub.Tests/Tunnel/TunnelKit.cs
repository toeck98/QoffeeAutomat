using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Tests.Gateway;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Tunnel;

/// <summary>One event of a stream as the browser sees it.</summary>
internal readonly record struct ReceivedEvent(string Name, string Data);

/// <summary>Reads a <c>text/event-stream</c> response while it is open.</summary>
internal sealed class SseClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(15);

    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseClient(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    public HttpStatusCode Status => _response.StatusCode;

    public string? MediaType => _response.Content.Headers.ContentType?.MediaType;

    /// <summary>Opens a stream; the response must be 200, otherwise the problem is in the assertion message.</summary>
    public static async Task<SseClient> OpenAsync(HubApiClient client, string path)
    {
        HttpResponseMessage response = await client.OpenStreamAsync(path);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail("Expected the stream to open, but got " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + await response.Content.ReadAsStringAsync());
        }
        Stream body = await response.Content.ReadAsStreamAsync();
        return new SseClient(response, new StreamReader(body, Encoding.UTF8));
    }

    /// <summary>The next event; comment lines (keep-alives) are skipped. Null when the stream has ended.</summary>
    public async Task<ReceivedEvent?> NextAsync(TimeSpan? limit = null)
    {
        using var timeout = new CancellationTokenSource(limit ?? DefaultLimit);
        string? name = null;
        var data = new List<string>();
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Timed out waiting for the next event of the stream.");
                throw;
            }

            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (name is not null || data.Count > 0)
                {
                    return new ReceivedEvent(name ?? "message", string.Join('\n', data));
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].TrimStart();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Add(line[5..].TrimStart());
            }
        }
    }

    /// <summary>Reads events until one with the name arrives.</summary>
    public async Task<ReceivedEvent> UntilAsync(string name, TimeSpan? limit = null)
    {
        while (true)
        {
            ReceivedEvent? next = await NextAsync(limit);
            Assert.True(next is not null, "The stream ended before the event '" + name + "' arrived.");
            if (next.Value.Name == name)
            {
                return next.Value;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared pieces of the tunnel tests (T912).</summary>
internal static class TunnelKit
{
    public static GatewayOptions Options(int streamQueue = 256, TimeSpan? backpressure = null, int maxStreams = 16, TimeSpan? requestTimeout = null)
    {
        return new GatewayOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            CloseTimeout = TimeSpan.FromMilliseconds(500),
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10),
            RunTimeout = TimeSpan.FromSeconds(20),
            StreamQueueCapacity = streamQueue,
            StreamBackpressureTimeout = backpressure ?? TimeSpan.FromSeconds(2),
            MaxStreamsPerAgent = maxStreams,
            Features = [HubFeatures.ConfigSync, HubFeatures.MgmtTunnel],
        };
    }

    /// <summary>The URL on the hub that reaches <c>/api/v1/{path}</c> of an agent.</summary>
    public static string AgentApi(string agentId, string path) => "/api/v1/agents/" + agentId + "/agent-api/" + path;

    /// <summary>A <c>mgmt_request</c> frame as the hub would send it, built by hand so that a test can send what the hub never would.</summary>
    public static Func<long, byte[]> RawRequest(string method, string path, string user, string role, JsonNode? body = null)
    {
        return id =>
        {
            var frame = new JsonObject
            {
                ["t"] = HubMessageTypes.MgmtRequest,
                ["id"] = id,
                ["method"] = method,
                ["path"] = path,
                ["principal"] = new JsonObject { ["user"] = user, ["role"] = role },
            };
            if (body is not null)
            {
                frame["body"] = body.DeepClone();
            }

            return Encoding.UTF8.GetBytes(frame.ToJsonString());
        };
    }

    /// <summary>The status of an ok result of <c>mgmt_request</c>.</summary>
    public static int StatusOf(AgentRequestOutcome outcome)
    {
        Assert.True(outcome.Ok, "The tunnel failed: " + outcome.ErrorCode + " " + outcome.ErrorMessage);
        return outcome.Data!.Value.GetProperty("status").GetInt32();
    }

    /// <summary>The <c>code</c> of the problem in an ok result of <c>mgmt_request</c>.</summary>
    public static string? ProblemCodeOf(AgentRequestOutcome outcome)
    {
        return outcome.Data!.Value.TryGetProperty("body", out JsonElement body) && body.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    /// <summary>Takes the computer to sleep without the agent being able to dial the hub again, so that the hub sees an agent that is offline.</summary>
    public static async Task GoOfflineAsync(GatewayRig rig, SimulatedAgent agent)
    {
        agent.Link.Online = false;
        await Task.Run(() => agent.Platform.Power.Simulate(QoffeeAutomat.Core.Platform.PowerEventKind.Sleep));
        await TestWait.UntilAsync(() => !rig.Gateway.GetPresence(agent.Id).Online, "the agent to be offline", 15000);
    }

    public static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
