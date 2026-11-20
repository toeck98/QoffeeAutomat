using System.Text.Json.Serialization;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>The enrollment status of an agent in the registry (PLAN.md S20 "Enrollment and trust").</summary>
public static class AgentStatuses
{
    /// <summary>The token requires approval and an administrator has not decided yet. The agent has no credential.</summary>
    public const string Pending = "pending";

    /// <summary>An administrator accepted the agent; it receives its credential with its next enroll request.</summary>
    public const string Approved = "approved";

    /// <summary>The agent has a credential and may open the agent link.</summary>
    public const string Enrolled = "enrolled";

    /// <summary>An administrator rejected the agent. It stays in the list until it is removed.</summary>
    public const string Rejected = "rejected";

    /// <summary>All statuses.</summary>
    public static IReadOnlyList<string> All { get; } = [Pending, Approved, Enrolled, Rejected];

    /// <summary>Tells whether a text is a status.</summary>
    /// <param name="status">The text.</param>
    /// <returns>True for one of the four statuses.</returns>
    public static bool IsValid(string? status) => status is Pending or Approved or Enrolled or Rejected;
}

/// <summary>An agent of the registry.</summary>
/// <param name="Id">The agent id, chosen by the agent.</param>
/// <param name="Name">The name the agent reported.</param>
/// <param name="Hostname">The host name of the PC.</param>
/// <param name="Os">The operating system family: <c>windows</c> or <c>linux</c>.</param>
/// <param name="OsVersion">The version of the operating system, if reported.</param>
/// <param name="Arch">The processor architecture.</param>
/// <param name="Version">The version of the agent software.</param>
/// <param name="Macs">The MAC addresses of the PC.</param>
/// <param name="UiUrl">The address of the local UI of the agent, if reported.</param>
/// <param name="Tags">The tags of the agent: those of its enrollment tokens plus those an administrator set.</param>
/// <param name="Status">One of <see cref="AgentStatuses"/>.</param>
/// <param name="EnrolledAt">When the agent first asked to be enrolled.</param>
/// <param name="ApprovedAt">When an administrator accepted the agent; absent for agents of tokens without approval and for pending ones.</param>
/// <param name="LastSeenAt">When the agent last opened the agent link, or null.</param>
/// <param name="TokenId">The id of the enrollment token that enrolled the agent.</param>
/// <param name="CredentialIssuedAt">When the current credential was issued, or null while there is none.</param>
/// <param name="RotationPending">True while a new credential was issued that the agent has not used yet.</param>
public sealed record AgentRecord(
    string Id,
    string Name,
    string Hostname,
    string Os,
    string? OsVersion,
    string Arch,
    string Version,
    IReadOnlyList<string> Macs,
    string? UiUrl,
    IReadOnlyList<string> Tags,
    string Status,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? LastSeenAt,
    string? TokenId,
    DateTimeOffset? CredentialIssuedAt,
    bool RotationPending);

/// <summary>The JSON of the lists in the columns <c>tags</c> and <c>macs</c>.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class AgentStorageJsonContext : JsonSerializerContext
{
}
