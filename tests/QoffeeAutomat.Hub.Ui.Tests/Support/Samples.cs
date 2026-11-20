using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Ui.Services;

namespace QoffeeAutomat.Hub.Ui.Tests.Support;

/// <summary>Data that the fake hub answers with.</summary>
internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public static HubSessionInfo Session(string role, string name = "alice", string method = "session") => new(true, name, role, method);

    public static AgentSummary Agent(
        string id,
        string? name = null,
        string status = "enrolled",
        string version = "1.2.3",
        params string[] tags)
    {
        return new AgentSummary(
            id,
            name ?? id.ToUpperInvariant(),
            id + ".example.test",
            "windows",
            "11",
            "x64",
            version,
            ["aa:bb:cc:dd:ee:ff"],
            null,
            tags,
            status,
            Now.AddDays(-3),
            status == "pending" ? null : Now.AddDays(-3),
            status == "enrolled" ? Now.AddMinutes(-2) : null,
            "token-1",
            Now.AddDays(-3),
            false);
    }

    public static AgentPresenceInfo Presence(bool online = true)
    {
        return new AgentPresenceInfo(online, online ? "unknown" : "sleep", online ? Now.AddHours(-1) : null, Now.AddMinutes(-2), "r1", "boot-1", ["mgmt_tunnel", "config_sync"]);
    }

    public static InventoryAgent Inventory(AgentSummary agent, bool online = true) => new(agent, Presence(online));

    public static InventoryDetail Detail(AgentSummary agent, bool online = true) => new(agent, Presence(online), null, null, []);

    public static AgentConfigStatusInfo Config(string id, string state = RolloutStates.UpToDate, long desired = 3, long applied = 3)
    {
        return new AgentConfigStatusInfo(id, id.ToUpperInvariant(), true, state, desired, desired, Now, null, applied, Now, applied, "applied", Now, [], []);
    }

    public static RolloutOverview Rollouts(params AgentConfigStatusInfo[] agents)
    {
        return new RolloutOverview(new RolloutCounts(agents.Length, 0, 0, 0, 0, 0), agents);
    }

    public static EnrollmentTokenSummary Token(string id, string name = "office", int uses = 0, int maxUses = 5, DateTimeOffset? revokedAt = null, DateTimeOffset? expiresAt = null)
    {
        return new EnrollmentTokenSummary(id, name, "qht_" + id, ["office"], false, maxUses, uses, "alice", Now.AddDays(-1), expiresAt, null, revokedAt, revokedAt is null && uses < maxUses);
    }

    public static UserInfo User(string id, string name, string role = HubRoleNames.Viewer, bool disabled = false)
    {
        return new UserInfo(id, name, role, disabled, Now.AddDays(-30), Now.AddDays(-30), Now.AddDays(-30), null);
    }

    public static ApiProblem Problem(int status, string code, string detail) => new("about:blank", "Error", status, detail, code);

    public static CommandDocument Command(string id, string name = "Lock screen")
    {
        return new CommandDocument(
            DocumentSchema.CurrentVersion,
            id,
            name,
            true,
            true,
            RunAsValues.User,
            new BuiltinAction("session.lock"),
            "Locks the session.",
            TimeoutSeconds: 60);
    }

    public static SensorDocument Sensor(string id, string name = "Explorer")
    {
        return new SensorDocument(DocumentSchema.CurrentVersion, id, name, true, true, "sensor", "bool", new ProcessRunningSensorSource("explorer"));
    }

    /// <summary>The body of a library command as the hub returns it: the document in the agent's format.</summary>
    public static JsonElement CommandBody(string id, string name = "Lock screen")
    {
        return JsonSerializer.SerializeToElement(Command(id, name) with { Origin = DocumentOrigins.Hub, Revision = 1 }, ApiDocumentJsonContext.Default.CommandDocument);
    }

    public static JsonElement SensorBody(string id, string name = "Explorer")
    {
        return JsonSerializer.SerializeToElement(Sensor(id, name) with { Origin = DocumentOrigins.Hub, Revision = 1 }, ApiDocumentJsonContext.Default.SensorDocument);
    }

    /// <summary>The body of a script: a JSON string that holds the text (no quote or backslash in the text).</summary>
    public static JsonElement Text(string text) => Json("\"" + text.Replace("\n", "\\n", StringComparison.Ordinal) + "\"");

    public static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static LibraryDocumentInfo Library(string kind, string id, string? name, JsonElement body, int revision = 1)
    {
        return new LibraryDocumentInfo(kind, id, name, revision, "hash-" + revision, Now.AddDays(-1), "alice", body, null);
    }

    public static ProfileInfo Profile(string id, string name, int priority = 0, int? active = null, params ProfileItemRef[] items)
    {
        var content = new ProfileContent(name, priority, null, items, new Dictionary<string, string>(), new ProfilePolicy("inherit", "inherit"));
        return new ProfileInfo(id, content, active, active ?? 0, active is null, Now.AddDays(-2), Now.AddDays(-1));
    }

    /// <summary>One agent of a fleet action; the name is the id in upper case, as <see cref="Agent"/> makes it.</summary>
    public static FleetActionItem FleetItem(string agentId, string state = FleetActionItemStates.Pending, string? message = null, string? errorCode = null, string? before = null, string? after = null)
    {
        return new FleetActionItem(agentId, agentId.ToUpperInvariant(), state, errorCode, message, null, null, before, after, null, null);
    }

    /// <summary>A fleet action whose agents are in the given state unless <paramref name="items"/> says otherwise.</summary>
    public static FleetActionInfo FleetAction(
        string id,
        string kind,
        string state,
        IReadOnlyList<string> agents,
        string? command = null,
        string? target = null,
        IReadOnlyList<FleetActionItem>? items = null,
        IReadOnlyList<string>? updated = null,
        string? stoppedAt = null,
        string? stopReason = null)
    {
        FleetActionItem[] all = [.. items ?? agents.Select(agent => FleetItem(agent))];
        int Count(string itemState) => all.Count(item => item.State == itemState);
        var counts = new FleetActionCounts(
            Count(FleetActionItemStates.Pending),
            Count(FleetActionItemStates.Running),
            Count(FleetActionItemStates.Success),
            Count(FleetActionItemStates.Failure),
            Count(FleetActionItemStates.Offline),
            Count(FleetActionItemStates.Skipped),
            Count(FleetActionItemStates.Cancelled));
        return new FleetActionInfo(id, kind, state, "alice", Now.AddMinutes(-5), state == FleetActionStates.Running ? null : Now, command, target, [], counts, all, updated ?? [], stoppedAt, stopReason);
    }

    public static AgentChange Change(string agentId, string name, long from = 1, long to = 2, params DocumentChange[] documents)
    {
        return new AgentChange(agentId, name, from, to, documents, [], []);
    }

    public static MetricSeriesData Series(string agentId, string metric, string unit, params (int MinutesAgo, double Value)[] points)
    {
        return new MetricSeriesData(
            agentId,
            metric,
            "number",
            unit,
            "raw",
            [.. points.OrderByDescending(point => point.MinutesAgo).Select(point => new QoffeeAutomat.Hub.Metrics.MetricPoint(Now.AddMinutes(-point.MinutesAgo), point.Value, null, null, null, null, null))],
            false);
    }
}
