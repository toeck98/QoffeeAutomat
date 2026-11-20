using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Config;

/// <summary>A library document in a list: the current revision without its body.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The library id: the document id, for scripts the file name.</param>
/// <param name="Name">The display name; absent for scripts and variables.</param>
/// <param name="Revision">The current revision.</param>
/// <param name="Hash">The hash of the body of the current revision, as the agent verifies it.</param>
/// <param name="CreatedAt">When the document was first saved.</param>
/// <param name="UpdatedAt">When the current revision was saved.</param>
public sealed record LibraryDocumentSummary(
    string Kind,
    string Id,
    string? Name,
    int Revision,
    string Hash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The answer of <c>GET /api/v1/library</c>.</summary>
/// <param name="Documents">The documents, ordered by kind and id.</param>
public sealed record LibraryDocumentList(IReadOnlyList<LibraryDocumentSummary> Documents);

/// <summary>One revision of a library document with its body.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The library id.</param>
/// <param name="Name">The display name; absent for scripts and variables.</param>
/// <param name="Revision">The revision number.</param>
/// <param name="Hash">The hash of the body as the agent verifies it.</param>
/// <param name="CreatedAt">When this revision was saved.</param>
/// <param name="CreatedBy">The user that saved this revision.</param>
/// <param name="Body">The body: a JSON object, for scripts a JSON string with the script text.</param>
/// <param name="Changed">Only in the answer of a save: false when the content equals the current revision and no revision was added.</param>
public sealed record LibraryDocumentInfo(
    string Kind,
    string Id,
    string? Name,
    int Revision,
    string Hash,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    JsonElement Body,
    bool? Changed);

/// <summary>Body of <c>PUT /api/v1/library/{kind}/{id}</c>.</summary>
/// <param name="Body">The document: a JSON object in the agent's format (S06); for scripts a JSON string with the script text.</param>
public sealed record SaveLibraryDocumentRequest(JsonElement? Body);

/// <summary>A revision in the history of a library document.</summary>
/// <param name="Revision">The revision number.</param>
/// <param name="Hash">The hash of the body.</param>
/// <param name="CreatedAt">When it was saved.</param>
/// <param name="CreatedBy">Who saved it.</param>
public sealed record LibraryRevisionSummary(int Revision, string Hash, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>The answer of <c>GET /api/v1/library/{kind}/{id}/revisions</c>.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Id">The library id.</param>
/// <param name="Revisions">The revisions, newest first.</param>
public sealed record LibraryRevisionList(string Kind, string Id, IReadOnlyList<LibraryRevisionSummary> Revisions);

/// <summary>A library document pinned at a revision in a profile.</summary>
/// <param name="Kind">One of <see cref="DocumentKinds"/>.</param>
/// <param name="Id">The library id.</param>
/// <param name="Revision">The pinned revision; a request that leaves it out pins the current one.</param>
public sealed record ProfileItemRef(string Kind, string Id, int? Revision);

/// <summary>The policy of a profile; each member is the value to set or <c>inherit</c> (S20, D-029).</summary>
/// <param name="LocalConfig"><c>allow</c>, <c>locked</c> or <c>inherit</c>.</param>
/// <param name="DirectLink"><c>allow</c>, <c>deny</c> or <c>inherit</c>.</param>
public sealed record ProfilePolicy(string LocalConfig, string DirectLink);

/// <summary>The policy in a request; a member that is left out means <c>inherit</c>.</summary>
/// <param name="LocalConfig"><c>allow</c>, <c>locked</c> or <c>inherit</c>.</param>
/// <param name="DirectLink"><c>allow</c>, <c>deny</c> or <c>inherit</c>.</param>
public sealed record ProfilePolicyRequest(string? LocalConfig, string? DirectLink);

/// <summary>What a profile contains: the draft, and what a published revision freezes.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Priority">Breaks ties between profiles whose targets are equally specific; the higher number wins.</param>
/// <param name="OsFilter">The profile only applies to agents of this operating system (<c>windows</c> or <c>linux</c>); absent for all.</param>
/// <param name="Items">The library documents with the pinned revision, sorted by kind and id.</param>
/// <param name="Variables">Variables of the profile, exposed to every run as <c>QA_HUBVAR_&lt;NAME&gt;</c>.</param>
/// <param name="Policy">The policy.</param>
public sealed record ProfileContent(
    string Name,
    int Priority,
    string? OsFilter,
    IReadOnlyList<ProfileItemRef> Items,
    IReadOnlyDictionary<string, string> Variables,
    ProfilePolicy Policy);

/// <summary>Body of <c>POST /api/v1/profiles</c> and <c>PUT /api/v1/profiles/{id}</c>; a PUT replaces the whole draft.</summary>
/// <param name="Id">The profile id (<c>^[a-z][a-z0-9_]{0,62}$</c>); only used when creating.</param>
/// <param name="Name">The display name, 1 to 100 characters.</param>
/// <param name="Priority">The priority, -10000 to 10000; default 0.</param>
/// <param name="OsFilter"><c>windows</c> or <c>linux</c>; default all.</param>
/// <param name="Items">The library documents; a missing revision pins the current one.</param>
/// <param name="Variables">The variables.</param>
/// <param name="Policy">The policy; default <c>inherit</c> for both members.</param>
public sealed record ProfileDraftRequest(
    string? Id,
    string? Name,
    int? Priority,
    string? OsFilter,
    IReadOnlyList<ProfileItemRef>? Items,
    IReadOnlyDictionary<string, string>? Variables,
    ProfilePolicyRequest? Policy);

/// <summary>A profile with its draft.</summary>
/// <param name="Id">The profile id.</param>
/// <param name="Draft">The draft, which a publish freezes.</param>
/// <param name="ActiveRevision">The published revision that is in effect; absent while nothing is published.</param>
/// <param name="LatestRevision">The highest published revision; 0 while nothing is published.</param>
/// <param name="HasUnpublishedChanges">True when the draft differs from the active revision.</param>
/// <param name="CreatedAt">When the profile was created.</param>
/// <param name="UpdatedAt">When the draft was last changed.</param>
public sealed record ProfileInfo(
    string Id,
    ProfileContent Draft,
    int? ActiveRevision,
    int LatestRevision,
    bool HasUnpublishedChanges,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The answer of <c>GET /api/v1/profiles</c>.</summary>
/// <param name="Profiles">The profiles, ordered by id.</param>
public sealed record ProfileList(IReadOnlyList<ProfileInfo> Profiles);

/// <summary>A published revision of a profile.</summary>
/// <param name="Revision">The revision number.</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="PublishedBy">The user that published it.</param>
/// <param name="Note">The note of the publisher, if any.</param>
/// <param name="Active">True when this revision is in effect.</param>
public sealed record ProfileRevisionSummary(int Revision, DateTimeOffset PublishedAt, string PublishedBy, string? Note, bool Active);

/// <summary>The answer of <c>GET /api/v1/profiles/{id}/revisions</c>.</summary>
/// <param name="ProfileId">The profile id.</param>
/// <param name="Revisions">The revisions, newest first.</param>
public sealed record ProfileRevisionList(string ProfileId, IReadOnlyList<ProfileRevisionSummary> Revisions);

/// <summary>One immutable revision of a profile with its content.</summary>
/// <param name="ProfileId">The profile id.</param>
/// <param name="Revision">The revision with its publication data.</param>
/// <param name="Content">The frozen content.</param>
public sealed record ProfileRevisionInfo(string ProfileId, ProfileRevisionSummary Revision, ProfileContent Content);

/// <summary>Body of <c>POST /api/v1/profiles/{id}/publish</c>.</summary>
/// <param name="Note">A note for the history, up to 500 characters.</param>
public sealed record PublishProfileRequest(string? Note);

/// <summary>Body of <c>POST /api/v1/profiles/{id}/rollback</c>.</summary>
/// <param name="Revision">The earlier revision to republish.</param>
/// <param name="Note">A note for the history, up to 500 characters.</param>
public sealed record RollbackProfileRequest(int? Revision, string? Note);

/// <summary>An assignment of a profile to a target.</summary>
/// <param name="Id">The assignment id.</param>
/// <param name="ProfileId">The profile.</param>
/// <param name="Target"><c>all</c>, <c>tag:&lt;tag&gt;</c> or <c>agent:&lt;id&gt;</c>.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="CreatedBy">The user that created it.</param>
public sealed record AssignmentInfo(string Id, string ProfileId, string Target, DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>The answer of <c>GET /api/v1/assignments</c>.</summary>
/// <param name="Assignments">The assignments, ordered by profile and target.</param>
public sealed record AssignmentList(IReadOnlyList<AssignmentInfo> Assignments);

/// <summary>Body of <c>POST /api/v1/assignments</c>.</summary>
/// <param name="ProfileId">The profile.</param>
/// <param name="Target"><c>all</c>, <c>tag:&lt;tag&gt;</c> or <c>agent:&lt;id&gt;</c>.</param>
public sealed record CreateAssignmentRequest(string? ProfileId, string? Target);

/// <summary>The answer of <c>POST /api/v1/assignments</c>.</summary>
/// <param name="Assignment">The new assignment.</param>
/// <param name="Agents">The agents that got a new generation because of it.</param>
public sealed record CreatedAssignment(AssignmentInfo Assignment, IReadOnlyList<AgentChange> Agents);

/// <summary>A document that differs between the generation an agent has and the one it would get.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Id">The document id.</param>
/// <param name="Change"><c>added</c>, <c>removed</c> or <c>changed</c>.</param>
/// <param name="OldHash">The hash the agent has; absent for added documents.</param>
/// <param name="NewHash">The hash the agent would get; absent for removed documents.</param>
public sealed record DocumentChange(string Kind, string Id, string Change, string? OldHash, string? NewHash);

/// <summary>A policy member that differs.</summary>
/// <param name="Name"><c>local_config</c> or <c>direct_link</c>.</param>
/// <param name="From">The value the agent has.</param>
/// <param name="To">The value the agent would get.</param>
public sealed record PolicyDifference(string Name, string From, string To);

/// <summary>An override or problem that the computation found, shown as a warning.</summary>
/// <param name="Code"><c>overridden</c>, <c>overridden_tie</c>, <c>variable_overridden</c>, <c>policy_overridden</c> or <c>missing_script</c>.</param>
/// <param name="Kind">The document kind, or <c>variable</c> or <c>policy</c>.</param>
/// <param name="Id">The document id, the name of the variable or the policy member.</param>
/// <param name="Message">What happens.</param>
/// <param name="Profile">The profile that wins or that has the problem.</param>
/// <param name="OtherProfile">The profile that loses, when two profiles collide.</param>
public sealed record ConfigWarningInfo(string Code, string Kind, string Id, string Message, string Profile, string? OtherProfile);

/// <summary>What a change does to the desired state of one agent.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="AgentName">The name of the agent.</param>
/// <param name="CurrentGeneration">The generation the agent was last told about; 0 when none.</param>
/// <param name="NewGeneration">The generation it gets (in a preview: would get).</param>
/// <param name="Documents">The documents that differ.</param>
/// <param name="Policy">The policy members that differ.</param>
/// <param name="Warnings">Overrides and problems in the new state.</param>
public sealed record AgentChange(
    string AgentId,
    string AgentName,
    long CurrentGeneration,
    long NewGeneration,
    IReadOnlyList<DocumentChange> Documents,
    IReadOnlyList<PolicyDifference> Policy,
    IReadOnlyList<ConfigWarningInfo> Warnings);

/// <summary>The answer of <c>GET /api/v1/profiles/{id}/preview</c>: what publishing the draft (or republishing a revision) would do.</summary>
/// <param name="ProfileId">The profile.</param>
/// <param name="Revision">The revision that would be in effect afterwards.</param>
/// <param name="NewRevision">True when a new revision would be created.</param>
/// <param name="Unchanged">True when the content equals what is in effect: nothing would change for any agent.</param>
/// <param name="ContainsSystemScripts">True when the content has a command that runs code as <c>system</c>, or a script that such a command uses.</param>
/// <param name="SystemScripts">What makes it a system script: <c>command:&lt;id&gt;</c> or <c>script:&lt;name&gt;</c>.</param>
/// <param name="RequiresAdmin">True when publishing needs the role admin.</param>
/// <param name="Agents">Exactly the agents whose desired state would change, with the differences.</param>
public sealed record PublishPreview(
    string ProfileId,
    int Revision,
    bool NewRevision,
    bool Unchanged,
    bool ContainsSystemScripts,
    IReadOnlyList<string> SystemScripts,
    bool RequiresAdmin,
    IReadOnlyList<AgentChange> Agents);

/// <summary>The answer of a publish or a rollback.</summary>
/// <param name="ProfileId">The profile.</param>
/// <param name="Revision">The revision that is in effect now.</param>
/// <param name="NewRevision">True when a new revision was created.</param>
/// <param name="Unchanged">True when nothing changed: no new generation for any agent.</param>
/// <param name="Agents">The agents that got a new generation.</param>
public sealed record PublishResult(
    string ProfileId,
    int Revision,
    bool NewRevision,
    bool Unchanged,
    IReadOnlyList<AgentChange> Agents);

/// <summary>A document of the desired state of an agent.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">The hash of the body.</param>
/// <param name="Profile">The profile the document comes from.</param>
/// <param name="ProfileRevision">The revision of that profile.</param>
public sealed record DesiredItemInfo(string Kind, string Id, string Hash, string Profile, int ProfileRevision);

/// <summary>The answer of <c>GET /api/v1/agents/{id}/desired-state</c>.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Generation">The generation the agent was last told about; 0 when none.</param>
/// <param name="UpToDate">True when that generation equals the desired state computed now.</param>
/// <param name="Policy">The policy the computation yields.</param>
/// <param name="Profiles">The profiles that contribute, in the order in which they are applied (later ones override).</param>
/// <param name="Items">The documents the computation yields.</param>
/// <param name="Warnings">The overrides and problems.</param>
/// <param name="UpdatedAt">When the generation was created; absent when there is none.</param>
public sealed record AgentDesiredInfo(
    string AgentId,
    long Generation,
    bool UpToDate,
    PolicyInfo Policy,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<DesiredItemInfo> Items,
    IReadOnlyList<ConfigWarningInfo> Warnings,
    DateTimeOffset? UpdatedAt);
