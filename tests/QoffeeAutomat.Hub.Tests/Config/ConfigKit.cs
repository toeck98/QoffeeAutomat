using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Tests.Config;

/// <summary>The agents of a test: the tests change them the way the registry would, without enrollment.</summary>
internal sealed class FakeAgentSource : IConfigAgentSource
{
    private readonly List<ConfigAgent> _agents = [];

    public void Set(params ConfigAgent[] agents)
    {
        _agents.Clear();
        _agents.AddRange(agents);
    }

    public void SetTags(string id, params string[] tags)
    {
        int index = _agents.FindIndex(agent => agent.Id == id);
        _agents[index] = _agents[index] with { Tags = tags };
    }

    public void Remove(string id) => _agents.RemoveAll(agent => agent.Id == id);

    public Task<IReadOnlyList<ConfigAgent>> ListAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ConfigAgent>>([.. _agents]);
    }
}

/// <summary>Builders for the documents and agents of the configuration tests.</summary>
internal static class Docs
{
    public static ConfigAgent Agent(string id, string os = "windows", string status = AgentStatuses.Enrolled, params string[] tags)
    {
        return new ConfigAgent(id, "name-" + id, os, tags, status);
    }

    public static JsonElement Command(string id, string name = "A command", string runAs = "user", string code = "Write-Output ok")
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 2, "id": "{{id}}", "name": "{{name}}", "enabled": true, "exposeToHa": false, "runAs": "{{runAs}}",
              "action": { "type": "powershell", "source": "inline", "code": "{{code}}", "engine": "auto" },
              "timeoutSeconds": 60, "concurrency": "queue", "cooldownSeconds": 0, "captureOutput": false
            }
            """);
    }

    public static JsonElement FileCommand(string id, string script, string runAs = "user")
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 2, "id": "{{id}}", "name": "File command", "enabled": true, "exposeToHa": false, "runAs": "{{runAs}}",
              "action": { "type": "powershell", "source": "file", "script": "{{script}}", "engine": "auto" },
              "timeoutSeconds": 60, "concurrency": "queue", "cooldownSeconds": 0, "captureOutput": false
            }
            """);
    }

    public static JsonElement BuiltinCommand(string id, string runAs = "system")
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 2, "id": "{{id}}", "name": "Builtin command", "enabled": true, "exposeToHa": false, "runAs": "{{runAs}}",
              "action": { "type": "builtin", "name": "power.shutdown" },
              "timeoutSeconds": 60, "concurrency": "queue", "cooldownSeconds": 0, "captureOutput": false
            }
            """);
    }

    public static JsonElement BashCommand(string id, string runAs = "user")
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 2, "id": "{{id}}", "name": "Bash command", "enabled": true, "exposeToHa": false, "runAs": "{{runAs}}",
              "action": { "type": "bash", "source": "inline", "code": "echo ok" },
              "timeoutSeconds": 60, "concurrency": "queue", "cooldownSeconds": 0, "captureOutput": false
            }
            """);
    }

    public static JsonElement SequenceCommand(string id, string calls, string runAs = "user")
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 2, "id": "{{id}}", "name": "Sequence", "enabled": true, "exposeToHa": false, "runAs": "{{runAs}}",
              "action": { "type": "sequence", "steps": [ { "command": "{{calls}}" } ], "onError": "stop" },
              "timeoutSeconds": 60, "concurrency": "queue", "cooldownSeconds": 0, "captureOutput": false
            }
            """);
    }

    public static JsonElement Sensor(string id)
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 1, "id": "{{id}}", "name": "Notepad", "enabled": true, "exposeToHa": true,
              "kind": "binary_sensor", "valueType": "bool", "source": { "type": "process_running", "process": "notepad" }
            }
            """);
    }

    public static JsonElement Mode(string id, string command)
    {
        return ConfigJson.Parse(
            $$"""
            {
              "schemaVersion": 1, "id": "{{id}}", "name": "Mode group", "enabled": true, "exposeToHa": true,
              "defaultMode": "normal", "onBoot": "reset", "onError": "stop",
              "modes": [
                { "id": "normal", "name": "Normal", "enter": [ { "command": "{{command}}" } ], "exit": [] },
                { "id": "other", "name": "Other", "enter": [], "exit": [] }
              ]
            }
            """);
    }

    public static JsonElement MetricOverrides(double intervalSeconds = 10)
    {
        return ConfigJson.Parse(
            $$"""
            { "schemaVersion": 1, "overrides": { "cpu.usage": { "intervalSeconds": {{intervalSeconds.ToString(CultureInfo.InvariantCulture)}} } } }
            """);
    }

    public static JsonElement Script(string text)
    {
        return ConfigJson.Parse(JsonValue.Create(text)!.ToJsonString());
    }

    public static JsonElement Variables(params (string Name, string Value)[] variables)
    {
        return ConfigJson.StringObject(variables.Select(variable => new KeyValuePair<string, string>(variable.Name, variable.Value)));
    }

    public static ProfileItemRef Item(string kind, string id, int? revision = null) => new(kind, id, revision);
}

/// <summary>
/// The configuration service on a real database in a temporary directory with a real signing key, and agents that the test controls. Time is
/// a <see cref="ManualTimeProvider"/>. The helpers assert that what they set up succeeds.
/// </summary>
internal sealed class ConfigFixture : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FileKeyProtector _protector;
    private readonly SecretStore _secrets;

    public ConfigFixture()
    {
        _protector = new FileKeyProtector(_directory.Below("secret.key"));
        _secrets = new SecretStore(_directory.Below("secrets.json"), _protector);
        Database = HubDatabase.Open(_directory.Below("hub.db"));
        Signer = new HubSigningKeyStore(_secrets);
        Service = new ConfigService(Database, Agents, Signer, Clock);
    }

    public HubDatabase Database { get; }

    public HubSigningKeyStore Signer { get; }

    public ManualTimeProvider Clock { get; } = new();

    public FakeAgentSource Agents { get; } = new();

    public ConfigService Service { get; }

    public static string Describe<T>(ConfigResult<T> result)
    {
        return result.Status + " " + result.Code + " " + result.Message + " "
            + string.Join("; ", result.Errors.Select(error => error.Path + " " + error.Code + " " + error.Message));
    }

    public async Task<LibraryDocumentInfo> SaveAsync(string kind, string id, JsonElement body, string user = "admin")
    {
        ConfigResult<LibraryDocumentInfo> result = await Service.SaveLibraryDocumentAsync(kind, id, body, user, CancellationToken.None);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value!;
    }

    public async Task<ProfileInfo> CreateProfileAsync(
        string id,
        ProfileItemRef[]? items = null,
        int priority = 0,
        string? osFilter = null,
        Dictionary<string, string>? variables = null,
        ProfilePolicyRequest? policy = null)
    {
        var request = new ProfileDraftRequest(id, "Profile " + id, priority, osFilter, items ?? [], variables, policy);
        ConfigResult<ProfileInfo> result = await Service.CreateProfileAsync(request, CancellationToken.None);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value!;
    }

    public async Task<ProfileInfo> UpdateProfileAsync(
        string id,
        ProfileItemRef[]? items = null,
        int priority = 0,
        string? osFilter = null,
        Dictionary<string, string>? variables = null,
        ProfilePolicyRequest? policy = null)
    {
        var request = new ProfileDraftRequest(null, "Profile " + id, priority, osFilter, items ?? [], variables, policy);
        ConfigResult<ProfileInfo> result = await Service.UpdateProfileAsync(id, request, CancellationToken.None);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value!;
    }

    public async Task<PublishResult> PublishAsync(string profileId, string role = HubRoles.Admin)
    {
        ConfigResult<PublishResult> result = await Service.PublishAsync(profileId, null, null, "tester", role, CancellationToken.None);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value!;
    }

    public async Task<PublishResult> RollbackAsync(string profileId, int revision, string role = HubRoles.Admin)
    {
        ConfigResult<PublishResult> result = await Service.PublishAsync(profileId, revision, null, "tester", role, CancellationToken.None);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value!;
    }

    public async Task<IReadOnlyList<AgentChange>> AssignAsync(string profileId, string target)
    {
        ConfigResult<CreatedAssignment> result = await Service.CreateAssignmentAsync(profileId, target, "tester", CancellationToken.None);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value!.Agents;
    }

    public async Task<long> GenerationOfAsync(string agentId)
    {
        AgentGeneration? generation = await Service.GetGenerationAsync(agentId, CancellationToken.None);
        return generation?.Generation ?? 0;
    }

    /// <summary>A library command and a profile that contains it, published and assigned to a target.</summary>
    public async Task<string> PublishedCommandProfileAsync(string profileId, string commandId, string target, string code = "Write-Output ok")
    {
        await SaveAsync("command", commandId, Docs.Command(commandId, code: code));
        await CreateProfileAsync(profileId, [Docs.Item("command", commandId)]);
        await PublishAsync(profileId);
        await AssignAsync(profileId, target);
        return profileId;
    }

    public void Dispose()
    {
        Signer.Dispose();
        Database.Dispose();
        _secrets.Dispose();
        _protector.Dispose();
        _directory.Dispose();
    }
}
