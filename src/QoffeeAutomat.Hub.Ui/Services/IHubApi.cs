using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// What the hub pages need from the hub: its management API (<c>/api/v1</c>, PLAN.md S20) as typed calls. <see cref="HubApiClient"/> implements it
/// over HTTP; tests use a fake. Every call throws <see cref="ApiException"/> when the hub answers with an error or cannot be reached. The calls
/// that reach an agent are not here: they go through <c>TunnelAgentApi</c>.
/// </summary>
public interface IHubApi
{
    /// <summary>Asks whether first-run setup is possible (<c>GET /auth/setup</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state, or <c>null</c> when setup is not available because an account exists.</returns>
    Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the first administrator with the setup token and signs in (<c>POST /auth/setup</c>).</summary>
    /// <param name="request">The setup token, the name and the password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The signed-in account.</returns>
    Task<HubSessionInfo> SetUpAsync(HubSetupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Signs in (<c>POST /auth/login</c>); the session is a cookie.</summary>
    /// <param name="request">The name and the password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The signed-in account.</returns>
    Task<HubSessionInfo> LogInAsync(HubLoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ends the session (<c>POST /auth/logout</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the session is ended.</returns>
    Task LogOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks who is signed in (<c>GET /auth/me</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or <c>null</c> when there is no valid session.</returns>
    Task<HubSessionInfo?> GetMeAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes the password of the signed-in user (<c>POST /auth/password</c>).</summary>
    /// <param name="request">The current and the new password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the password is changed.</returns>
    Task ChangeOwnPasswordAsync(ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads who the hub is (<c>GET /hub</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The identity of the hub.</returns>
    Task<HubInfo> GetHubAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the agents of the registry, whatever their enrollment status (<c>GET /agents</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents.</returns>
    Task<AgentList> GetAgentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the enrolled agents with their presence (<c>GET /inventory</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents.</returns>
    Task<InventoryList> GetInventoryAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one enrolled agent in detail (<c>GET /inventory/{id}</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent with presence, manifest, live state and events.</returns>
    Task<InventoryDetail> GetInventoryDetailAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads the configuration status of every enrolled agent (<c>GET /rollouts</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The counts and the status per agent.</returns>
    Task<RolloutOverview> GetRolloutsAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the hub to send the desired state to an agent again (<c>POST /agents/{id}/config-push</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The configuration status of the agent.</returns>
    Task<AgentConfigStatusInfo> PushConfigAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Sets the tags of an agent (<c>PATCH /agents/{id}</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="tags">The complete new list of tags.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent.</returns>
    Task<AgentSummary> UpdateAgentTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>Accepts an agent that waits for approval (<c>POST /agents/{id}/approve</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent.</returns>
    Task<AgentSummary> ApproveAgentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Rejects an agent that waits for approval (<c>POST /agents/{id}/reject</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent.</returns>
    Task<AgentSummary> RejectAgentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Issues a new credential to an agent (<c>POST /agents/{id}/rotate-credential</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agent.</returns>
    Task<AgentSummary> RotateCredentialAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Revokes the credential of an agent and removes it (<c>DELETE /agents/{id}</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the agent is removed.</returns>
    Task RemoveAgentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the enrollment tokens (<c>GET /enrollment-tokens</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tokens, newest first.</returns>
    Task<EnrollmentTokenList> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an enrollment token (<c>POST /enrollment-tokens</c>).</summary>
    /// <param name="request">The settings of the token.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token and the enrollment string, which is shown only now.</returns>
    Task<CreatedEnrollmentToken> CreateEnrollmentTokenAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revokes an enrollment token (<c>DELETE /enrollment-tokens/{id}</c>).</summary>
    /// <param name="id">The token id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the token is revoked.</returns>
    Task RevokeEnrollmentTokenAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Starts the pairing of Home Assistant (<c>POST /fleet-pairings</c>).</summary>
    /// <param name="request">The label and the scope of the token that the pairing produces.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The code to enter in Home Assistant.</returns>
    Task<FleetPairingInfo> StartFleetPairingAsync(CreateFleetPairingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels the pending pairing (<c>DELETE /fleet-pairings</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the pairing is cancelled.</returns>
    Task CancelFleetPairingAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the fleet tokens of Home Assistant and tools (<c>GET /fleet-tokens</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tokens, newest first.</returns>
    Task<FleetTokenList> GetFleetTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes a fleet token (<c>DELETE /fleet-tokens/{id}</c>).</summary>
    /// <param name="id">The token id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the token is revoked.</returns>
    Task RevokeFleetTokenAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the accounts (<c>GET /users</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accounts.</returns>
    Task<UserList> GetUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an account (<c>POST /users</c>).</summary>
    /// <param name="request">The name, the password and the role.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    Task<UserInfo> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes the role or the disabled flag of an account (<c>PATCH /users/{id}</c>).</summary>
    /// <param name="id">The user id.</param>
    /// <param name="request">What changes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account.</returns>
    Task<UserInfo> UpdateUserAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sets the password of an account (<c>POST /users/{id}/password</c>).</summary>
    /// <param name="id">The user id.</param>
    /// <param name="request">The new password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the password is set.</returns>
    Task SetUserPasswordAsync(string id, SetPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes an account (<c>DELETE /users/{id}</c>).</summary>
    /// <param name="id">The user id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the account is deleted.</returns>
    Task DeleteUserAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the API tokens (<c>GET /tokens</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The tokens, newest first.</returns>
    Task<TokenList> GetTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an API token (<c>POST /tokens</c>).</summary>
    /// <param name="request">The name, the role and the expiry.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token and its secret, which is shown only now.</returns>
    Task<CreatedToken> CreateTokenAsync(CreateTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revokes an API token (<c>DELETE /tokens/{id}</c>).</summary>
    /// <param name="id">The token id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the token is revoked.</returns>
    Task RevokeTokenAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads a page of the audit log (<c>GET /audit</c>).</summary>
    /// <param name="query">The filters and the page.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, newest first.</returns>
    Task<AuditPage> GetAuditAsync(AuditQueryParameters query, CancellationToken cancellationToken = default);

    /// <summary>Lists the documents of the library (<c>GET /library</c>).</summary>
    /// <param name="kind">Only documents of this kind (<c>DocumentKinds</c>); <c>null</c> lists all.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The documents with their current revision, without bodies.</returns>
    Task<LibraryDocumentList> GetLibraryAsync(string? kind = null, CancellationToken cancellationToken = default);

    /// <summary>Reads one library document (<c>GET /library/{kind}/{id}</c>).</summary>
    /// <param name="kind">The kind of the document.</param>
    /// <param name="id">The library id.</param>
    /// <param name="revision">An earlier revision; <c>null</c> reads the current one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The document with its body.</returns>
    Task<LibraryDocumentInfo> GetLibraryDocumentAsync(string kind, string id, int? revision = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the revisions of a library document (<c>GET /library/{kind}/{id}/revisions</c>).</summary>
    /// <param name="kind">The kind of the document.</param>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revisions, newest first.</returns>
    Task<LibraryRevisionList> GetLibraryRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default);

    /// <summary>Saves a library document as its next revision (<c>PUT /library/{kind}/{id}</c>, administrators).</summary>
    /// <param name="kind">The kind of the document.</param>
    /// <param name="id">The library id.</param>
    /// <param name="body">The document; for a script the script text as a JSON string.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The saved document; <see cref="LibraryDocumentInfo.Changed"/> tells whether a revision was added.</returns>
    Task<LibraryDocumentInfo> SaveLibraryDocumentAsync(string kind, string id, System.Text.Json.JsonElement body, CancellationToken cancellationToken = default);

    /// <summary>Deletes a library document that no profile draft uses (<c>DELETE /library/{kind}/{id}</c>, administrators).</summary>
    /// <param name="kind">The kind of the document.</param>
    /// <param name="id">The library id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the document is deleted.</returns>
    Task DeleteLibraryDocumentAsync(string kind, string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the profiles with their drafts (<c>GET /profiles</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profiles.</returns>
    Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one profile with its draft (<c>GET /profiles/{id}</c>).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile.</returns>
    Task<ProfileInfo> GetProfileAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates a profile with a draft (<c>POST /profiles</c>, administrators).</summary>
    /// <param name="request">The id and the draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile.</returns>
    Task<ProfileInfo> CreateProfileAsync(ProfileDraftRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the draft of a profile (<c>PUT /profiles/{id}</c>, administrators).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="request">The new draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The profile.</returns>
    Task<ProfileInfo> UpdateProfileAsync(string id, ProfileDraftRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a profile with its revisions and assignments (<c>DELETE /profiles/{id}</c>, administrators).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the profile is deleted.</returns>
    Task DeleteProfileAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Lists the published revisions of a profile (<c>GET /profiles/{id}/revisions</c>).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revisions, newest first.</returns>
    Task<ProfileRevisionList> GetProfileRevisionsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads one published revision of a profile (<c>GET /profiles/{id}/revisions/{n}</c>).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revision with its frozen content.</returns>
    Task<ProfileRevisionInfo> GetProfileRevisionAsync(string id, int revision, CancellationToken cancellationToken = default);

    /// <summary>Shows what a publish or a roll-back would do (<c>GET /profiles/{id}/preview</c>); nothing changes.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="revision">An earlier revision to republish; <c>null</c> previews the draft.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The affected agents with the differences.</returns>
    Task<PublishPreview> PreviewProfileAsync(string id, int? revision = null, CancellationToken cancellationToken = default);

    /// <summary>Publishes the draft as the next revision (<c>POST /profiles/{id}/publish</c>, operators; administrators for system scripts).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="note">A note for the revision, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revision and the agents that got a new generation.</returns>
    Task<PublishResult> PublishProfileAsync(string id, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Republishes an earlier revision (<c>POST /profiles/{id}/rollback</c>, operators; administrators for system scripts).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="revision">The revision to go back to.</param>
    /// <param name="note">A note, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The revision in effect and the agents that got a new generation.</returns>
    Task<PublishResult> RollbackProfileAsync(string id, int revision, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Shows how a published revision has reached the agents (<c>GET /profiles/{id}/rollout</c>).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="revision">A revision other than the one in effect; <c>null</c> for the one in effect.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agents with the state of the documents.</returns>
    Task<ProfileRolloutInfo> GetProfileRolloutAsync(string id, int? revision = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the assignments (<c>GET /assignments</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assignments.</returns>
    Task<AssignmentList> GetAssignmentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Assigns a profile to a target (<c>POST /assignments</c>, administrators); it takes effect at once.</summary>
    /// <param name="request">The profile and the target (<c>all</c>, <c>tag:&lt;tag&gt;</c> or <c>agent:&lt;id&gt;</c>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assignment and the agents that got a new generation.</returns>
    Task<CreatedAssignment> CreateAssignmentAsync(CreateAssignmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes an assignment (<c>DELETE /assignments/{id}</c>, administrators).</summary>
    /// <param name="id">The assignment id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the assignment is removed.</returns>
    Task DeleteAssignmentAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Shows the desired state that the published profiles yield for an agent (<c>GET /agents/{id}/desired-state</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The documents, the policy, the contributing profiles and the warnings.</returns>
    Task<AgentDesiredInfo> GetAgentDesiredStateAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads the newest value of every metric that the hub stores for an agent (<c>GET /agents/{id}/metrics</c>).</summary>
    /// <param name="id">The agent id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The metrics with their newest value.</returns>
    Task<AgentMetricList> GetAgentMetricsAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Queries the stored series of one or several agents (<c>GET /metrics/query</c>).</summary>
    /// <param name="query">The agents, the metrics and the time range.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The series.</returns>
    Task<MetricQueryResult> QueryMetricsAsync(MetricQueryParameters query, CancellationToken cancellationToken = default);

    /// <summary>Shows the version of every enrolled agent against the newest release that an agent reports (<c>GET /fleet-versions</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The newest version and the agents with theirs.</returns>
    Task<FleetVersionOverview> GetFleetVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the bulk runs and updates that the hub still has in memory (<c>GET /fleet-actions</c>).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The actions, the newest first, with the state of every agent.</returns>
    Task<FleetActionList> GetFleetActionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one fleet action (<c>GET /fleet-actions/{id}</c>).</summary>
    /// <param name="id">The id of the action.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The action.</returns>
    Task<FleetActionInfo> GetFleetActionAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Runs a command on several agents (<c>POST /fleet-actions/bulk-runs</c>, operators). The hub answers at once; the action runs in the background.</summary>
    /// <param name="request">The command, the variables and the targets.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The action that was started.</returns>
    Task<FleetActionInfo> StartBulkRunAsync(StartBulkRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Updates agents one after another (<c>POST /fleet-actions/updates</c>, administrators). The hub answers at once; the update runs in the background.</summary>
    /// <param name="request">The targets, the target version and the timeout.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The action that was started.</returns>
    Task<FleetActionInfo> StartFleetUpdateAsync(StartFleetUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops a running fleet action (<c>POST /fleet-actions/{id}/cancel</c>, operators; administrators for an update).</summary>
    /// <param name="id">The id of the action.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The action after the cancel.</returns>
    Task<FleetActionInfo> CancelFleetActionAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>What a metric query asks for (<c>GET /api/v1/metrics/query</c>).</summary>
/// <param name="Agents">The agent ids, 1 to 50.</param>
/// <param name="Metrics">The metric ids or prefixes that end in <c>*</c>; empty selects all metrics.</param>
/// <param name="From">The start of the range; <c>null</c> lets the hub choose (one hour before <paramref name="To"/>).</param>
/// <param name="To">The end of the range; <c>null</c> means now.</param>
/// <param name="Resolution"><c>auto</c>, <c>raw</c>, <c>5m</c> or <c>1h</c>; <c>null</c> means <c>auto</c>.</param>
/// <param name="MaxPoints">The most points per series, or <c>null</c> for the limit of the hub.</param>
public sealed record MetricQueryParameters(
    IReadOnlyList<string> Agents,
    IReadOnlyList<string> Metrics,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Resolution = null,
    int? MaxPoints = null);

/// <summary>The filters and the page of an audit log query; a <c>null</c> member does not filter.</summary>
/// <param name="User">Only entries of this user.</param>
/// <param name="Action">Only entries of this action.</param>
/// <param name="Result">Only entries with this result: <c>success</c>, <c>failure</c> or <c>denied</c>.</param>
/// <param name="Agent">Only entries that affected this agent.</param>
/// <param name="Before">The <c>nextBefore</c> of the previous page.</param>
/// <param name="Limit">The page size, 1 to 500.</param>
public sealed record AuditQueryParameters(string? User = null, string? Action = null, string? Result = null, string? Agent = null, long? Before = null, int Limit = 100);
