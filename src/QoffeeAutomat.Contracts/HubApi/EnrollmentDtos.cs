namespace QoffeeAutomat.Hub.Api;

/// <summary>An enrollment token as the API shows it. The secret is never part of it.</summary>
/// <param name="Id">The id of the token.</param>
/// <param name="Name">The label that the administrator chose.</param>
/// <param name="Prefix">The first characters of the secret, to tell tokens apart.</param>
/// <param name="Tags">The tags that agents enrolled with this token get.</param>
/// <param name="RequiresApproval">True when an administrator has to accept every agent that enrolls with this token.</param>
/// <param name="MaxUses">How many enrollments the token allows.</param>
/// <param name="Uses">How many enrollments have used the token.</param>
/// <param name="CreatedBy">The user that created the token.</param>
/// <param name="CreatedAt">When the token was created.</param>
/// <param name="ExpiresAt">When the token expires; absent when it does not.</param>
/// <param name="LastUsedAt">When the token was last used; absent when never.</param>
/// <param name="RevokedAt">When the token was revoked; absent when it is not.</param>
/// <param name="Active">True when the token can enroll another agent now: not revoked, not expired and with uses left.</param>
public sealed record EnrollmentTokenSummary(
    string Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Tags,
    bool RequiresApproval,
    int MaxUses,
    int Uses,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Active);

/// <summary>The answer of <c>GET /api/v1/enrollment-tokens</c>.</summary>
/// <param name="Tokens">The tokens, newest first.</param>
public sealed record EnrollmentTokenList(IReadOnlyList<EnrollmentTokenSummary> Tokens);

/// <summary>Body of <c>POST /api/v1/enrollment-tokens</c>.</summary>
/// <param name="Name">A label, 1 to 64 characters.</param>
/// <param name="Tags">Tags that agents enrolled with the token get; at most 16, each 1 to 32 characters of a-z, 0-9, '.', '_' and '-'.</param>
/// <param name="RequiresApproval">True when an administrator has to accept every agent; default false.</param>
/// <param name="MaxUses">How many agents may enroll, 1 to 10000; default 1.</param>
/// <param name="ExpiresAt">When the token expires, within ten years; default never.</param>
/// <param name="HubUrl">The address under which agents reach the hub, for the enrollment string; default the address of this request. Needed when the hub is behind a proxy or Home Assistant ingress.</param>
public sealed record CreateEnrollmentTokenRequest(
    string? Name,
    IReadOnlyList<string>? Tags,
    bool? RequiresApproval,
    int? MaxUses,
    DateTimeOffset? ExpiresAt,
    string? HubUrl);

/// <summary>The answer of <c>POST /api/v1/enrollment-tokens</c>. The enrollment string holds the secret and is shown only here.</summary>
/// <param name="Token">The token.</param>
/// <param name="EnrollmentString">The string <c>qahub1.&lt;base64url&gt;</c> that is entered on the PC.</param>
public sealed record CreatedEnrollmentToken(EnrollmentTokenSummary Token, string EnrollmentString);

/// <summary>An agent of the registry.</summary>
/// <param name="Id">The agent id.</param>
/// <param name="Name">The name the agent reported.</param>
/// <param name="Hostname">The host name of the PC.</param>
/// <param name="Os">The operating system family.</param>
/// <param name="OsVersion">The version of the operating system; absent when not reported.</param>
/// <param name="Arch">The processor architecture.</param>
/// <param name="Version">The version of the agent software.</param>
/// <param name="Macs">The MAC addresses of the PC.</param>
/// <param name="UiUrl">The address of the local UI of the agent; absent when not reported.</param>
/// <param name="Tags">The tags of the agent.</param>
/// <param name="Status">The enrollment status: <c>pending</c>, <c>approved</c>, <c>enrolled</c> or <c>rejected</c>.</param>
/// <param name="EnrolledAt">When the agent first asked to be enrolled.</param>
/// <param name="ApprovedAt">When an administrator accepted the agent; absent when no approval happened.</param>
/// <param name="LastSeenAt">When the agent last opened the agent link; absent when never.</param>
/// <param name="TokenId">The enrollment token that enrolled the agent.</param>
/// <param name="CredentialIssuedAt">When the current credential was issued; absent while there is none.</param>
/// <param name="RotationPending">True while a new credential was issued that the agent has not used yet.</param>
public sealed record AgentSummary(
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

/// <summary>The answer of <c>GET /api/v1/agents</c>.</summary>
/// <param name="Agents">The agents, the most recently enrolled first.</param>
public sealed record AgentList(IReadOnlyList<AgentSummary> Agents);

/// <summary>Body of <c>PATCH /api/v1/agents/{id}</c>.</summary>
/// <param name="Tags">The new complete list of tags.</param>
public sealed record UpdateAgentRequest(IReadOnlyList<string>? Tags);
