using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Storage;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Enrollment;

/// <summary>A created enrollment token: its id and the secret that an agent presents.</summary>
internal sealed record TestToken(string Id, string Secret, string EnrollmentString);

/// <summary>Stands in for the agent gateway (T906): records what the registry asks it to send and answers as the test says.</summary>
internal sealed class RecordingNotifier : IAgentLinkNotifier
{
    public bool RotateResult { get; set; } = true;

    public List<(string AgentId, string Credential)> Rotations { get; } = [];

    public List<(string AgentId, string Reason)> Unenrolls { get; } = [];

    public Task<bool> SendCredentialRotateAsync(string agentId, string credential, CancellationToken cancellationToken)
    {
        Rotations.Add((agentId, credential));
        return Task.FromResult(RotateResult);
    }

    public Task SendUnenrollAsync(string agentId, string reason, CancellationToken cancellationToken)
    {
        Unenrolls.Add((agentId, reason));
        return Task.CompletedTask;
    }

    /// <summary>The service registration that puts this notifier in place of the default.</summary>
    public Action<IServiceCollection> Register => services =>
    {
        services.RemoveAll<IAgentLinkNotifier>();
        services.AddSingleton<IAgentLinkNotifier>(this);
    };
}

/// <summary>Requests of the enrollment tests: tokens through the API, enroll calls and attempts to open the agent link, as an agent does them.</summary>
internal static class EnrollmentKit
{
    public const string EnrollPath = "/hub/v1/enroll";
    public const string LinkPath = "/hub/v1/agent";

    /// <summary>The JSON body of an enroll request, in the snake_case of QoffeeLink.</summary>
    public static string EnrollBody(string token, string agentId, string name = "Gaming-PC")
    {
        var body = new JsonObject
        {
            ["token"] = token,
            ["agent"] = new JsonObject
            {
                ["id"] = agentId,
                ["name"] = name,
                ["version"] = "1.0.0",
                ["os"] = "windows",
                ["os_version"] = "10.0.26200",
                ["arch"] = "x64",
                ["hostname"] = "GAMING-PC",
                ["macs"] = new JsonArray("A4:B1:C1:00:11:22"),
                ["ui_url"] = "https://gaming-pc.local:8481/",
            },
        };
        return body.ToJsonString();
    }

    /// <summary>Creates a token as the administrator and returns it with its secret.</summary>
    public static async Task<TestToken> CreateTokenAsync(
        HubTestHost host,
        string name = "lab",
        int? maxUses = null,
        bool approval = false,
        string[]? tags = null,
        DateTimeOffset? expiresAt = null)
    {
        var body = new JsonObject { ["name"] = name, ["requiresApproval"] = approval };
        if (maxUses is { } uses)
        {
            body["maxUses"] = uses;
        }

        if (tags is not null)
        {
            body["tags"] = new JsonArray(tags.Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray());
        }

        if (expiresAt is { } expiry)
        {
            body["expiresAt"] = expiry.ToString("O", CultureInfo.InvariantCulture);
        }

        using HttpResponseMessage response = await host.Admin.PostAsync("/api/v1/enrollment-tokens", body.ToJsonString());
        JsonNode created = await HubAssert.JsonAsync(response, HttpStatusCode.Created);
        string enrollmentString = created["enrollmentString"]!.GetValue<string>();
        return new TestToken(created["token"]!["id"]!.GetValue<string>(), EnrollmentString.Decode(enrollmentString).Token, enrollmentString);
    }

    /// <summary>Calls the enroll endpoint as an agent (no session, no anti-forgery header).</summary>
    public static async Task<HttpResponseMessage> EnrollAsync(HubTestHost host, string token, string agentId, string? remoteIp = null, string? rawBody = null)
    {
        HubApiClient client = host.CreateClient();
        client.SendCsrf = false;
        client.RemoteIp = remoteIp;
        return await client.PostAsync(EnrollPath, rawBody ?? EnrollBody(token, agentId));
    }

    /// <summary>Enrolls an agent and returns the credential; the enrollment must succeed with state <c>enrolled</c>.</summary>
    public static async Task<string> EnrollForCredentialAsync(HubTestHost host, string token, string agentId)
    {
        using HttpResponseMessage response = await EnrollAsync(host, token, agentId);
        JsonNode body = await HubAssert.JsonAsync(response);
        Assert.Equal("enrolled", body["state"]!.GetValue<string>());
        return body["credential"]!.GetValue<string>();
    }

    /// <summary>Tries to open the agent link with a credential and reports the status and the error code of the answer.</summary>
    public static async Task<(HttpStatusCode Status, string Code)> OpenLinkAsync(HubTestHost host, string? credential)
    {
        HubApiClient client = host.CreateClient();
        client.SendCsrf = false;
        client.BearerToken = credential;
        using HttpResponseMessage response = await client.GetAsync(LinkPath);
        string text = await response.Content.ReadAsStringAsync();
        string code = JsonNode.Parse(text)?["error"]?["code"]?.GetValue<string>() ?? string.Empty;
        return (response.StatusCode, code);
    }

    /// <summary>
    /// The credential of an agent passed the check. The request of <see cref="OpenLinkAsync"/> is a plain GET, not a WebSocket upgrade, so the agent gateway (T906),
    /// which only sees requests with an accepted credential, answers it with 400 <c>bad_request</c>; a refused credential never gets that far (401 or 403).
    /// </summary>
    public static void AssertAccepted((HttpStatusCode Status, string Code) answer)
    {
        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        Assert.Equal("bad_request", answer.Code);
    }

    /// <summary>The error code of a failed answer, after checking its status.</summary>
    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        JsonNode body = await HubAssert.JsonAsync(response, expected);
        return body["error"]!["code"]!.GetValue<string>();
    }

    /// <summary>The agent as the registry API shows it.</summary>
    public static async Task<JsonNode> GetAgentAsync(HubTestHost host, string agentId)
    {
        using HttpResponseMessage response = await host.Admin.GetAsync("/api/v1/agents/" + agentId);
        return await HubAssert.JsonAsync(response);
    }

    /// <summary>The hash of the current credential of an agent as stored in the database, or null while it has none.</summary>
    public static string? StoredCredentialHashOrNull(string agentId, HubTestHost host)
    {
        return host.Service<HubDatabase>().Query(connection =>
            HubSql.Scalar(connection, null, "SELECT credential_hash FROM agents WHERE id = @id;", ("@id", agentId)) as string);
    }

    /// <summary>The hash of the current credential of an agent; the agent must have one.</summary>
    public static string StoredCredentialHash(string agentId, HubTestHost host)
    {
        return StoredCredentialHashOrNull(agentId, host) ?? throw new InvalidOperationException("The agent " + agentId + " has no credential.");
    }
}
