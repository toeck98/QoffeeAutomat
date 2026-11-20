using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Ui.Services;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Hub.Ui.Tests.Support;

/// <summary>
/// A hub for component tests: answers with data that a test sets, records every call, and, like the real hub, refuses with 403 a call that the role
/// of the signed-in user may not make, so that a test can show that a page does not even ask.
/// </summary>
internal sealed class FakeHubApi : IHubApi
{
    public SetupState? Setup { get; set; }

    public HubSessionInfo? Me { get; set; }

    public ApiException? LoginFailure { get; set; }

    public ApiException? SetupFailure { get; set; }

    public ApiException? ListFailure { get; set; }

    public List<string> Calls { get; } = [];

    public List<HubLoginRequest> LoginRequests { get; } = [];

    public List<HubSetupRequest> SetupRequests { get; } = [];

    public int LogOutCalls { get; private set; }

    public AgentList Agents { get; set; } = new([]);

    public InventoryList Inventory { get; set; } = new([]);

    public RolloutOverview? Rollouts { get; set; }

    public Dictionary<string, InventoryDetail> Details { get; } = [];

    public EnrollmentTokenList EnrollmentTokens { get; set; } = new([]);

    public CreatedEnrollmentToken? NextCreatedToken { get; set; }

    public List<CreateEnrollmentTokenRequest> CreatedTokenRequests { get; } = [];

    public List<string> Approved { get; } = [];

    public List<string> Rejected { get; } = [];

    public List<string> RevokedEnrollmentTokens { get; } = [];

    public List<(string Id, IReadOnlyList<string> Tags)> TagUpdates { get; } = [];

    public UserList Users { get; set; } = new([]);

    public TokenList Tokens { get; set; } = new([]);

    public FleetTokenList FleetTokens { get; set; } = new([]);

    public AuditPage Audit { get; set; } = new([]);

    public bool Has(string call) => Calls.Contains(call);

    public Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetSetupStateAsync));
        return Task.FromResult(Setup);
    }

    public Task<HubSessionInfo> SetUpAsync(HubSetupRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(SetUpAsync));
        SetupRequests.Add(request);
        if (SetupFailure is not null)
        {
            throw SetupFailure;
        }

        HubSessionInfo me = Samples.Session(HubRoleNames.Admin, request.Username);
        Me = me;
        Setup = null;
        return Task.FromResult(me);
    }

    public Task<HubSessionInfo> LogInAsync(HubLoginRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(LogInAsync));
        LoginRequests.Add(request);
        if (LoginFailure is not null)
        {
            throw LoginFailure;
        }

        HubSessionInfo me = Me ?? Samples.Session(HubRoleNames.Admin, request.Username);
        Me = me;
        return Task.FromResult(me);
    }

    public Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(LogOutAsync));
        LogOutCalls++;
        Me = null;
        return Task.CompletedTask;
    }

    public Task<HubSessionInfo?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetMeAsync));
        return Task.FromResult(Me);
    }

    public Task ChangeOwnPasswordAsync(ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ChangeOwnPasswordAsync));
        return Task.CompletedTask;
    }

    public Task<HubInfo> GetHubAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetHubAsync));
        return Task.FromResult(new HubInfo("hub-1", "Home hub", "0.1.0", "selfsigned", "AA:BB", "MFkw"));
    }

    public Task<AgentList> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetAgentsAsync));
        ThrowListFailure();
        return Task.FromResult(Agents);
    }

    public Task<InventoryList> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetInventoryAsync));
        return Task.FromResult(Inventory);
    }

    public Task<InventoryDetail> GetInventoryDetailAsync(string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetInventoryDetailAsync) + ":" + id);
        if (!Details.TryGetValue(id, out InventoryDetail? detail))
        {
            throw new ApiException(404, Samples.Problem(404, "unknown_agent", "There is no such agent."));
        }

        return Task.FromResult(detail);
    }

    public Task<RolloutOverview> GetRolloutsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetRolloutsAsync));
        return Task.FromResult(Rollouts ?? throw new ApiException(404, Samples.Problem(404, "not_found", "No roll-out.")));
    }

    public Task<AgentConfigStatusInfo> PushConfigAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Operator);
        Calls.Add(nameof(PushConfigAsync) + ":" + id);
        return Task.FromResult(Samples.Config(id, RolloutStates.Pending));
    }

    public Task<AgentSummary> UpdateAgentTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(UpdateAgentTagsAsync) + ":" + id);
        TagUpdates.Add((id, tags));
        return Task.FromResult(Samples.Agent(id, tags: [.. tags]));
    }

    public Task<AgentSummary> ApproveAgentAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(ApproveAgentAsync) + ":" + id);
        Approved.Add(id);
        Agents = new AgentList([.. Agents.Agents.Select(agent => agent.Id == id ? agent with { Status = "enrolled" } : agent)]);
        return Task.FromResult(Samples.Agent(id));
    }

    public Task<AgentSummary> RejectAgentAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(RejectAgentAsync) + ":" + id);
        Rejected.Add(id);
        Agents = new AgentList([.. Agents.Agents.Select(agent => agent.Id == id ? agent with { Status = "rejected" } : agent)]);
        return Task.FromResult(Samples.Agent(id, status: "rejected"));
    }

    public Task<AgentSummary> RotateCredentialAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(RotateCredentialAsync) + ":" + id);
        return Task.FromResult(Samples.Agent(id));
    }

    public Task RemoveAgentAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(RemoveAgentAsync) + ":" + id);
        return Task.CompletedTask;
    }

    public Task<EnrollmentTokenList> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(GetEnrollmentTokensAsync));
        return Task.FromResult(EnrollmentTokens);
    }

    public Task<CreatedEnrollmentToken> CreateEnrollmentTokenAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(CreateEnrollmentTokenAsync));
        CreatedTokenRequests.Add(request);
        CreatedEnrollmentToken created = NextCreatedToken ?? new CreatedEnrollmentToken(Samples.Token("new"), "qahub1.AAAA");
        EnrollmentTokens = new EnrollmentTokenList([created.Token, .. EnrollmentTokens.Tokens]);
        return Task.FromResult(created);
    }

    public Task RevokeEnrollmentTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(RevokeEnrollmentTokenAsync) + ":" + id);
        RevokedEnrollmentTokens.Add(id);
        return Task.CompletedTask;
    }

    public Task<FleetPairingInfo> StartFleetPairingAsync(CreateFleetPairingRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(StartFleetPairingAsync));
        return Task.FromResult(new FleetPairingInfo("123456", Samples.Now.AddMinutes(5), request.Name ?? string.Empty, new FleetScopeSummary(request.Scope ?? "all", request.Tags ?? []), "AA:BB"));
    }

    public Task CancelFleetPairingAsync(CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(CancelFleetPairingAsync));
        return Task.CompletedTask;
    }

    public Task<FleetTokenList> GetFleetTokensAsync(CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(GetFleetTokensAsync));
        return Task.FromResult(FleetTokens);
    }

    public Task RevokeFleetTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(RevokeFleetTokenAsync) + ":" + id);
        return Task.CompletedTask;
    }

    public Task<UserList> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(GetUsersAsync));
        return Task.FromResult(Users);
    }

    public Task<UserInfo> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(CreateUserAsync));
        UserInfo created = Samples.User("u-new", request.Username, request.Role);
        Users = new UserList([.. Users.Users, created]);
        return Task.FromResult(created);
    }

    public Task<UserInfo> UpdateUserAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(UpdateUserAsync) + ":" + id);
        return Task.FromResult(Samples.User(id, id));
    }

    public Task SetUserPasswordAsync(string id, SetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(SetUserPasswordAsync) + ":" + id);
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(DeleteUserAsync) + ":" + id);
        return Task.CompletedTask;
    }

    public Task<TokenList> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(GetTokensAsync));
        return Task.FromResult(Tokens);
    }

    public Task<CreatedToken> CreateTokenAsync(CreateTokenRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(CreateTokenAsync));
        var info = new TokenInfo("t1", request.Name, request.Role, "qht_abc", "alice", Samples.Now, request.ExpiresAt, null, null, true);
        return Task.FromResult(new CreatedToken(info, "qht_secret-value"));
    }

    public Task RevokeTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(RevokeTokenAsync) + ":" + id);
        return Task.CompletedTask;
    }

    public Task<AuditPage> GetAuditAsync(AuditQueryParameters query, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(GetAuditAsync));
        return Task.FromResult(Audit);
    }

    // Configuration and metrics (T916) ---------------------------------------------------------------------------------------------------

    /// <summary>The library: the documents with their bodies, newest revision only.</summary>
    public List<LibraryDocumentInfo> LibraryDocuments { get; } = [];

    /// <summary>Every save of a library document that reached the hub.</summary>
    public List<(string Kind, string Id, JsonElement Body)> SavedDocuments { get; } = [];

    /// <summary>When set, every save fails with it (the hub's 422 for a document that does not validate).</summary>
    public ApiException? SaveFailure { get; set; }

    public List<(string Kind, string Id)> DeletedDocuments { get; } = [];

    public Dictionary<string, ProfileInfo> ProfileStore { get; } = [];

    public Dictionary<string, List<ProfileRevisionSummary>> ProfileRevisionStore { get; } = [];

    public List<(string Id, ProfileDraftRequest Request)> ProfileUpdates { get; } = [];

    public List<ProfileDraftRequest> ProfileCreations { get; } = [];

    public List<string> DeletedProfiles { get; } = [];

    /// <summary>What a preview answers; null answers a preview of nothing.</summary>
    public PublishPreview? Preview { get; set; }

    public List<(string Id, int? Revision)> Previews { get; } = [];

    public List<(string Id, string? Note)> Published { get; } = [];

    public List<(string Id, int Revision, string? Note)> RolledBack { get; } = [];

    public Dictionary<string, ProfileRolloutInfo> ProfileRollouts { get; } = [];

    public List<AssignmentInfo> AssignmentStore { get; } = [];

    public List<CreateAssignmentRequest> AssignmentRequests { get; } = [];

    public List<string> DeletedAssignments { get; } = [];

    /// <summary>The agents that a new assignment gives a new generation.</summary>
    public List<AgentChange> AssignmentAgents { get; } = [];

    public Dictionary<string, AgentDesiredInfo> Desired { get; } = [];

    public Dictionary<string, AgentMetricList> AgentMetrics { get; } = [];

    public List<MetricQueryParameters> MetricQueries { get; } = [];

    /// <summary>What a metric query answers; null answers 404.</summary>
    public Func<MetricQueryParameters, MetricQueryResult>? MetricAnswer { get; set; }

    public Task<LibraryDocumentList> GetLibraryAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetLibraryAsync));
        ThrowListFailure();
        IEnumerable<LibraryDocumentInfo> documents = LibraryDocuments.Where(document => kind is null || document.Kind == kind);
        return Task.FromResult(new LibraryDocumentList([.. documents.Select(Summary)]));
    }

    public Task<LibraryDocumentInfo> GetLibraryDocumentAsync(string kind, string id, int? revision = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetLibraryDocumentAsync) + ":" + kind + "/" + id);
        LibraryDocumentInfo? found = LibraryDocuments.FirstOrDefault(document => document.Kind == kind && document.Id == id);
        return found is null
            ? throw new ApiException(404, Samples.Problem(404, "unknown_document", "There is no such library document."))
            : Task.FromResult(found);
    }

    public Task<LibraryRevisionList> GetLibraryRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetLibraryRevisionsAsync) + ":" + kind + "/" + id);
        LibraryDocumentInfo found = LibraryDocuments.FirstOrDefault(document => document.Kind == kind && document.Id == id)
            ?? throw new ApiException(404, Samples.Problem(404, "unknown_document", "There is no such library document."));
        return Task.FromResult(new LibraryRevisionList(
            kind,
            id,
            [.. Enumerable.Range(1, found.Revision).Reverse().Select(number => new LibraryRevisionSummary(number, "hash-" + number, Samples.Now.AddHours(-number), "alice"))]));
    }

    public Task<LibraryDocumentInfo> SaveLibraryDocumentAsync(string kind, string id, JsonElement body, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(SaveLibraryDocumentAsync) + ":" + kind + "/" + id);
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        SavedDocuments.Add((kind, id, body.Clone()));
        LibraryDocumentInfo? existing = LibraryDocuments.FirstOrDefault(document => document.Kind == kind && document.Id == id);
        bool changed = existing is null || existing.Body.GetRawText() != body.GetRawText();
        int revision = existing is null ? 1 : existing.Revision + (changed ? 1 : 0);
        string? name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
        var saved = new LibraryDocumentInfo(kind, id, name, revision, "hash-" + revision, Samples.Now, "alice", body.Clone(), changed);
        if (existing is not null)
        {
            LibraryDocuments.Remove(existing);
        }

        LibraryDocuments.Add(saved);
        return Task.FromResult(saved);
    }

    public Task DeleteLibraryDocumentAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(DeleteLibraryDocumentAsync) + ":" + kind + "/" + id);
        DeletedDocuments.Add((kind, id));
        LibraryDocuments.RemoveAll(document => document.Kind == kind && document.Id == id);
        return Task.CompletedTask;
    }

    public Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetProfilesAsync));
        ThrowListFailure();
        return Task.FromResult(new ProfileList([.. ProfileStore.Values]));
    }

    public Task<ProfileInfo> GetProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetProfileAsync) + ":" + id);
        return ProfileStore.TryGetValue(id, out ProfileInfo? profile)
            ? Task.FromResult(profile)
            : throw new ApiException(404, Samples.Problem(404, "unknown_profile", "There is no such profile."));
    }

    public Task<ProfileInfo> CreateProfileAsync(ProfileDraftRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(CreateProfileAsync));
        ProfileCreations.Add(request);
        ProfileInfo created = Samples.Profile(request.Id ?? "new", request.Name ?? "New");
        ProfileStore[created.Id] = created;
        return Task.FromResult(created);
    }

    public Task<ProfileInfo> UpdateProfileAsync(string id, ProfileDraftRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(UpdateProfileAsync) + ":" + id);
        ProfileUpdates.Add((id, request));
        ProfileInfo previous = ProfileStore[id];
        var content = new ProfileContent(
            request.Name ?? previous.Draft.Name,
            request.Priority ?? 0,
            request.OsFilter,
            [.. (request.Items ?? []).Select(item => item.Revision is null
                ? item with { Revision = LibraryDocuments.FirstOrDefault(document => document.Kind == item.Kind && document.Id == item.Id)?.Revision ?? 1 }
                : item)],
            request.Variables ?? new Dictionary<string, string>(),
            new ProfilePolicy(request.Policy?.LocalConfig ?? "inherit", request.Policy?.DirectLink ?? "inherit"));
        ProfileInfo updated = previous with { Draft = content, HasUnpublishedChanges = true, UpdatedAt = Samples.Now };
        ProfileStore[id] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(DeleteProfileAsync) + ":" + id);
        DeletedProfiles.Add(id);
        ProfileStore.Remove(id);
        return Task.CompletedTask;
    }

    public Task<ProfileRevisionList> GetProfileRevisionsAsync(string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetProfileRevisionsAsync) + ":" + id);
        return Task.FromResult(new ProfileRevisionList(id, ProfileRevisionStore.TryGetValue(id, out List<ProfileRevisionSummary>? revisions) ? [.. revisions] : []));
    }

    public Task<ProfileRevisionInfo> GetProfileRevisionAsync(string id, int revision, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetProfileRevisionAsync) + ":" + id + "@" + revision);
        ProfileInfo profile = ProfileStore[id];
        return Task.FromResult(new ProfileRevisionInfo(id, new ProfileRevisionSummary(revision, Samples.Now, "alice", null, true), profile.Draft));
    }

    public Task<PublishPreview> PreviewProfileAsync(string id, int? revision = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(PreviewProfileAsync) + ":" + id);
        Previews.Add((id, revision));
        return Task.FromResult(Preview ?? new PublishPreview(id, revision ?? 1, revision is null, false, false, [], false, []));
    }

    public Task<PublishResult> PublishProfileAsync(string id, string? note = null, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Operator);
        Calls.Add(nameof(PublishProfileAsync) + ":" + id);
        RequireAdminForSystemScripts();
        Published.Add((id, note));
        PublishPreview preview = Preview ?? new PublishPreview(id, 1, true, false, false, [], false, []);
        ProfileInfo profile = ProfileStore[id];
        ProfileStore[id] = profile with { ActiveRevision = preview.Revision, LatestRevision = preview.Revision, HasUnpublishedChanges = false };
        if (!ProfileRevisionStore.TryGetValue(id, out List<ProfileRevisionSummary>? revisions))
        {
            revisions = [];
            ProfileRevisionStore[id] = revisions;
        }

        revisions.RemoveAll(revision => revision.Revision == preview.Revision);
        revisions.Insert(0, new ProfileRevisionSummary(preview.Revision, Samples.Now, "alice", note, true));
        return Task.FromResult(new PublishResult(id, preview.Revision, preview.NewRevision, preview.Unchanged, preview.Agents));
    }

    public Task<PublishResult> RollbackProfileAsync(string id, int revision, string? note = null, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Operator);
        Calls.Add(nameof(RollbackProfileAsync) + ":" + id);
        RequireAdminForSystemScripts();
        RolledBack.Add((id, revision, note));
        PublishPreview preview = Preview ?? new PublishPreview(id, revision, false, false, false, [], false, []);
        ProfileStore[id] = ProfileStore[id] with { ActiveRevision = revision };
        return Task.FromResult(new PublishResult(id, revision, false, preview.Unchanged, preview.Agents));
    }

    public Task<ProfileRolloutInfo> GetProfileRolloutAsync(string id, int? revision = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetProfileRolloutAsync) + ":" + id);
        return ProfileRollouts.TryGetValue(id, out ProfileRolloutInfo? rollout)
            ? Task.FromResult(rollout)
            : throw new ApiException(404, Samples.Problem(404, "not_published", "The profile has no published revision."));
    }

    public Task<AssignmentList> GetAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetAssignmentsAsync));
        return Task.FromResult(new AssignmentList([.. AssignmentStore]));
    }

    public Task<CreatedAssignment> CreateAssignmentAsync(CreateAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(CreateAssignmentAsync));
        AssignmentRequests.Add(request);
        var assignment = new AssignmentInfo("a" + (AssignmentStore.Count + 1), request.ProfileId ?? string.Empty, request.Target ?? string.Empty, Samples.Now, "alice");
        AssignmentStore.Add(assignment);
        return Task.FromResult(new CreatedAssignment(assignment, [.. AssignmentAgents]));
    }

    public Task DeleteAssignmentAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(DeleteAssignmentAsync) + ":" + id);
        DeletedAssignments.Add(id);
        AssignmentStore.RemoveAll(assignment => assignment.Id == id);
        return Task.CompletedTask;
    }

    public Task<AgentDesiredInfo> GetAgentDesiredStateAsync(string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetAgentDesiredStateAsync) + ":" + id);
        return Desired.TryGetValue(id, out AgentDesiredInfo? desired)
            ? Task.FromResult(desired)
            : throw new ApiException(404, Samples.Problem(404, "unknown_agent", "There is no such enrolled agent."));
    }

    public Task<AgentMetricList> GetAgentMetricsAsync(string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetAgentMetricsAsync) + ":" + id);
        return AgentMetrics.TryGetValue(id, out AgentMetricList? metrics)
            ? Task.FromResult(metrics)
            : throw new ApiException(404, Samples.Problem(404, "unknown_agent", "There is no such agent."));
    }

    public Task<MetricQueryResult> QueryMetricsAsync(MetricQueryParameters query, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(QueryMetricsAsync));
        MetricQueries.Add(query);
        return MetricAnswer is null
            ? throw new ApiException(404, Samples.Problem(404, "unknown_agent", "There is no such agent."))
            : Task.FromResult(MetricAnswer(query));
    }

    // Fleet actions (T917) ---------------------------------------------------------------------------------------------------------------

    public FleetVersionOverview FleetVersions { get; set; } = new(null, []);

    public List<FleetActionInfo> FleetActions { get; } = [];

    public List<StartBulkRunRequest> BulkRunRequests { get; } = [];

    public List<StartFleetUpdateRequest> UpdateRequests { get; } = [];

    public List<string> CancelledActions { get; } = [];

    /// <summary>What the hub answers a start with; null makes the fake build an action from the request.</summary>
    public ApiException? StartFailure { get; set; }

    public Task<FleetVersionOverview> GetFleetVersionsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetFleetVersionsAsync));
        return Task.FromResult(FleetVersions);
    }

    public Task<FleetActionList> GetFleetActionsAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetFleetActionsAsync));
        return Task.FromResult(new FleetActionList([.. FleetActions]));
    }

    public Task<FleetActionInfo> GetFleetActionAsync(string id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetFleetActionAsync) + ":" + id);
        return Task.FromResult(FleetActions.FirstOrDefault(action => action.Id == id)
            ?? throw new ApiException(404, Samples.Problem(404, "unknown_action", "There is no such fleet action.")));
    }

    public Task<FleetActionInfo> StartBulkRunAsync(StartBulkRunRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Operator);
        Calls.Add(nameof(StartBulkRunAsync));
        BulkRunRequests.Add(request);
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        string[] ids = [.. (request.Agents ?? []).Concat(Agents.Agents.Where(agent => agent.Tags.Intersect(request.Tags ?? []).Any()).Select(agent => agent.Id)).Distinct()];
        FleetActionInfo action = Samples.FleetAction("bulk-" + (FleetActions.Count + 1), FleetActionKinds.BulkRun, FleetActionStates.Running, ids, command: request.Command);
        FleetActions.Insert(0, action);
        return Task.FromResult(action);
    }

    public Task<FleetActionInfo> StartFleetUpdateAsync(StartFleetUpdateRequest request, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Admin);
        Calls.Add(nameof(StartFleetUpdateAsync));
        UpdateRequests.Add(request);
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        FleetActionInfo action = Samples.FleetAction("update-" + (FleetActions.Count + 1), FleetActionKinds.Update, FleetActionStates.Running, [.. request.Agents ?? []], target: FleetVersions.LatestVersion ?? request.TargetVersion);
        FleetActions.Insert(0, action);
        return Task.FromResult(action);
    }

    public Task<FleetActionInfo> CancelFleetActionAsync(string id, CancellationToken cancellationToken = default)
    {
        Guard(HubRoleNames.Operator);
        Calls.Add(nameof(CancelFleetActionAsync) + ":" + id);
        FleetActionInfo action = FleetActions.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new ApiException(404, Samples.Problem(404, "unknown_action", "There is no such fleet action."));
        if (action.Kind == FleetActionKinds.Update && !HubRoleNames.AtLeast(Me?.Role, HubRoleNames.Admin))
        {
            Calls.Add("refused:admin_required");
            throw new ApiException(403, Samples.Problem(403, "admin_required", "Only an administrator may cancel an update of agents."));
        }

        CancelledActions.Add(id);
        FleetActionInfo cancelled = action with { State = FleetActionStates.Cancelled };
        FleetActions[FleetActions.IndexOf(action)] = cancelled;
        return Task.FromResult(cancelled);
    }

    private static LibraryDocumentSummary Summary(LibraryDocumentInfo document)
    {
        return new LibraryDocumentSummary(document.Kind, document.Id, document.Name, document.Revision, document.Hash, document.CreatedAt, document.CreatedAt);
    }

    /// <summary>Like the hub: content with system scripts needs an administrator to publish.</summary>
    private void RequireAdminForSystemScripts()
    {
        if (Preview is { RequiresAdmin: true } && !HubRoleNames.AtLeast(Me?.Role, HubRoleNames.Admin))
        {
            Calls.Add("refused:admin_required");
            throw new ApiException(403, Samples.Problem(403, "admin_required", "The content has system scripts: only an administrator may publish it."));
        }
    }

    private void ThrowListFailure()
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }
    }

    /// <summary>The refusal of the hub for a role that is too low: 403, whatever the UI showed.</summary>
    private void Guard(string required)
    {
        if (!HubRoleNames.AtLeast(Me?.Role, required))
        {
            Calls.Add("refused:" + required);
            throw new ApiException(403, Samples.Problem(403, "forbidden", "Your role may not do this."));
        }
    }
}
