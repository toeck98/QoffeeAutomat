using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Tests.Config;
using QoffeeAutomat.Hub.Tests.Enrollment;
using QoffeeAutomat.Hub.Tests.Gateway;
using QoffeeAutomat.Hub.Tests.Security;

namespace QoffeeAutomat.Hub.Tests.Rollout;

/// <summary>
/// The hub as the roll-out tests need it: the whole hub in an in-process host with the gateway announcing <c>config_sync</c>, agents that are really enrolled in the
/// registry, and helpers for the library, profiles and assignments. Agents are played by hand (<see cref="ScriptedConfigAgent"/>), so a test decides what every agent
/// answers and reports. Time is the manual clock of the host.
/// </summary>
internal sealed class RolloutRig : IAsyncDisposable
{
    public const string Rev = "aaaaaaaaaaaa";

    private RolloutRig(HubTestHost hub)
    {
        Hub = hub;
    }

    public HubTestHost Hub { get; }

    public AgentGateway Gateway => Hub.Service<AgentGateway>();

    public ConfigService Config => Hub.Service<ConfigService>();

    public ConfigRolloutService Rollout => Hub.Service<ConfigRolloutService>();

    /// <summary>Short timings and the feature <c>config_sync</c>; the frame limit can be lowered to force several <c>config_documents</c> frames.</summary>
    public static GatewayOptions Options(int maxFrameBytes = 1024 * 1024) => new()
    {
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        CloseTimeout = TimeSpan.FromMilliseconds(500),
        RequestTimeout = TimeSpan.FromSeconds(10),
        RunTimeout = TimeSpan.FromSeconds(20),
        MaxFrameBytes = maxFrameBytes,
        Features = [HubFeatures.ConfigSync],
    };

    public static async Task<RolloutRig> StartAsync(GatewayOptions? options = null, bool withUsers = false)
    {
        HubTestHost hub = await HubTestHost.StartAsync(
            withUsers: withUsers,
            configure: services =>
            {
                services.RemoveAll<GatewayOptions>();
                services.AddSingleton(options ?? Options());
            });
        return new RolloutRig(hub);
    }

    /// <summary>Enrolls an agent in the registry the way an agent does (a token and an enroll request); it has not connected.</summary>
    public async Task EnrollAsync(string agentId, params string[] tags)
    {
        TestToken token = await EnrollmentKit.CreateTokenAsync(Hub, name: "lab-" + agentId, tags: tags.Length == 0 ? null : tags);
        _ = await EnrollmentKit.EnrollForCredentialAsync(Hub, token.Secret, agentId);
    }

    public async Task SaveCommandAsync(string id, string code = "Write-Output ok")
    {
        ConfigResult<LibraryDocumentInfo> result = await Config.SaveLibraryDocumentAsync("command", id, Docs.Command(id, code: code), "admin", CancellationToken.None);
        Assert.True(result.IsSuccess, ConfigFixture.Describe(result));
    }

    public async Task CreateProfileAsync(string id, IReadOnlyList<string> commandIds, ProfilePolicyRequest? policy = null)
    {
        var request = new ProfileDraftRequest(id, "Profile " + id, 0, null, Items(commandIds), null, policy);
        ConfigResult<ProfileInfo> result = await Config.CreateProfileAsync(request, CancellationToken.None);
        Assert.True(result.IsSuccess, ConfigFixture.Describe(result));
    }

    public async Task UpdateProfileAsync(string id, IReadOnlyList<string> commandIds, ProfilePolicyRequest? policy = null)
    {
        var request = new ProfileDraftRequest(null, "Profile " + id, 0, null, Items(commandIds), null, policy);
        ConfigResult<ProfileInfo> result = await Config.UpdateProfileAsync(id, request, CancellationToken.None);
        Assert.True(result.IsSuccess, ConfigFixture.Describe(result));
    }

    public async Task<PublishResult> PublishAsync(string profileId, int? revision = null)
    {
        ConfigResult<PublishResult> result = await Config.PublishAsync(profileId, revision, null, "tester", HubRoles.Admin, CancellationToken.None);
        Assert.True(result.IsSuccess, ConfigFixture.Describe(result));
        return result.Value!;
    }

    public async Task AssignAsync(string profileId, string target)
    {
        ConfigResult<CreatedAssignment> result = await Config.CreateAssignmentAsync(profileId, target, "tester", CancellationToken.None);
        Assert.True(result.IsSuccess, ConfigFixture.Describe(result));
    }

    /// <summary>A library command, a profile that contains it, published, and assigned to a target; the first generation exists afterwards for the agents the target matches.</summary>
    public async Task PublishCommandProfileAsync(string profileId, string commandId, string target, string code = "Write-Output ok")
    {
        await SaveCommandAsync(commandId, code);
        await CreateProfileAsync(profileId, [commandId]);
        _ = await PublishAsync(profileId);
        await AssignAsync(profileId, target);
    }

    public async Task<long> GenerationOfAsync(string agentId)
    {
        AgentGeneration? generation = await Config.GetGenerationAsync(agentId, CancellationToken.None);
        return generation?.Generation ?? 0;
    }

    /// <summary>Waits until the status of an agent satisfies a condition, and returns it.</summary>
    public async Task<AgentConfigStatusInfo> WaitForAsync(string agentId, Func<AgentConfigStatusInfo, bool> condition, string because, int timeoutMilliseconds = 10000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (true)
        {
            AgentConfigStatusInfo? status = await Rollout.GetAgentStatusAsync(agentId, CancellationToken.None);
            if (status is not null && condition(status))
            {
                return status;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + because);
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Hub.DisposeAsync();
    }

    private static ProfileItemRef[] Items(IReadOnlyList<string> commandIds) => [.. commandIds.Select(id => Docs.Item("command", id))];
}

/// <summary>
/// An agent that speaks <c>config_sync</c>, played by hand on a socket of its own: the test reads what the hub sends (<c>config_desired</c>, <c>config_documents</c>), answers
/// the requests the way a real agent would (<c>need</c>, ok or an error) and reports its status. It connects through the gateway like the socket of the endpoint.
/// </summary>
internal sealed class ScriptedConfigAgent
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private ScriptedConfigAgent(string id, ScriptedAgentSocket socket, Task serving)
    {
        Id = id;
        Socket = socket;
        Serving = serving;
    }

    public string Id { get; }

    public ScriptedAgentSocket Socket { get; }

    public Task Serving { get; }

    /// <summary>Connects the agent, waits until it is online and, with <paramref name="applied"/>, sends the status that a real agent sends after every handshake.</summary>
    /// <param name="rig">The hub.</param>
    /// <param name="id">The agent id; the agent must be enrolled.</param>
    /// <param name="applied">The generation that the agent reports as applied after the handshake; null sends no status.</param>
    public static async Task<ScriptedConfigAgent> ConnectAsync(RolloutRig rig, string id, long? applied = 0)
    {
        var socket = new ScriptedAgentSocket();
        Task serving = rig.Gateway.ServeAsync(socket, ScriptedAgent.Record(id), CancellationToken.None);
        socket.SendText(ScriptedAgent.Hello(id, RolloutRig.Rev, 1, 1, HubFeatures.ConfigSync));
        _ = await socket.NextFrameOfAsync(MessageTypes.ClientHello);
        await TestWait.UntilAsync(() => rig.Gateway.GetPresence(id).Online, "the agent to be online");

        var agent = new ScriptedConfigAgent(id, socket, serving);
        if (applied is { } generation)
        {
            agent.ReportStatus(generation, ConfigStates.Applied);
        }

        return agent;
    }

    public Task<JsonElement> NextDesiredAsync() => Socket.NextFrameOfAsync(HubMessageTypes.ConfigDesired);

    public Task<JsonElement> NextDocumentsAsync() => Socket.NextFrameOfAsync(HubMessageTypes.ConfigDocuments);

    /// <summary>Answers <c>config_desired</c> with the documents the agent lacks.</summary>
    public void AnswerNeed(JsonElement request, params (string Kind, string Id)[] need)
    {
        string json = JsonSerializer.Serialize(
            new ConfigDesiredResultData([.. need.Select(entry => new DocumentRef(entry.Kind, entry.Id))]),
            HubJsonContext.Default.ConfigDesiredResultData);
        using JsonDocument data = JsonDocument.Parse(json);
        Socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(request), data.RootElement.Clone()));
    }

    /// <summary>Answers a request with an ok result without data.</summary>
    public void AnswerOk(JsonElement request) => Socket.SendText(ScriptedAgent.Ok(ScriptedAgent.IdOf(request)));

    /// <summary>Answers a request with an error, the way an agent refuses a desired state.</summary>
    public void AnswerError(JsonElement request, string code = ErrorCodes.BadRequest) => Socket.SendText(ScriptedAgent.Fail(ScriptedAgent.IdOf(request), code));

    /// <summary>Sends <c>config_status</c>.</summary>
    public void ReportStatus(long generation, string state, params ConfigItemResult[] results)
    {
        Socket.SendText(JsonSerializer.Serialize(new ConfigStatusMessage(generation, state, results), HubJsonContext.Default.ConfigStatusMessage));
    }

    /// <summary>Plays an agent that has none of the documents: asks for all, takes them over and reports them applied. Returns the <c>config_desired</c> frame.</summary>
    public async Task<JsonElement> ApplyNextAsync()
    {
        JsonElement desired = await NextDesiredAsync();
        (string Kind, string Id)[] all =
        [
            .. desired.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("kind").GetString()!, item.GetProperty("id").GetString()!)),
        ];
        AnswerNeed(desired, all);
        if (all.Length > 0)
        {
            bool final;
            do
            {
                JsonElement documents = await NextDocumentsAsync();
                AnswerOk(documents);
                final = documents.GetProperty("final").GetBoolean();
            }
            while (!final);
        }

        ReportStatus(
            desired.GetProperty("generation").GetInt64(),
            ConfigStates.Applied,
            [.. all.Select(entry => new ConfigItemResult(entry.Kind, entry.Id, ConfigItemStates.Applied))]);
        return desired;
    }

    /// <summary>The types of the frames that the hub sent and the test has not read yet.</summary>
    public async Task<string[]> DrainAsync()
    {
        var types = new List<string>();
        while (Socket.Pending > 0)
        {
            types.Add(HubTestLink.TypeOf(await Socket.NextFrameAsync()));
        }

        return [.. types];
    }

    /// <summary>Closes the connection as an agent does that goes away, and waits until the hub has seen it.</summary>
    public async Task DisconnectAsync(RolloutRig rig)
    {
        Socket.SendClose();
        await Serving.WaitAsync(Limit);
        await TestWait.UntilAsync(() => !rig.Gateway.GetPresence(Id).Online, "the agent to be offline");
    }
}
