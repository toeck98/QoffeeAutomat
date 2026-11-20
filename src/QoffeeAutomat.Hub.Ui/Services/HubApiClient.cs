using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
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
/// <see cref="IHubApi"/> over a typed <see cref="HttpClient"/> (PLAN.md S08, S09 item 1, S20). It talks to the hub that served the UI, so the
/// session cookie travels with every request; requests that change state carry the anti-forgery header; bodies are read and written with the
/// source-generated <see cref="HubUiJsonContext"/> (no reflection); and every error answer becomes an <see cref="ApiException"/> that carries
/// the problem details. Addresses are relative to the base address of the page, so the client works below a path prefix (Home Assistant ingress).
/// </summary>
public sealed class HubApiClient : IHubApi
{
    private const string ApiBase = "api/v1/";

    private readonly HttpClient _http;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The HTTP client; its base address is the base address of the page, including a path prefix.</param>
    public HubApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
    }

    /// <inheritdoc />
    public async Task<SetupState?> GetSetupStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync("auth/setup", HubUiJsonContext.Default.SetupState, cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<HubSessionInfo> SetUpAsync(HubSetupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(HttpMethod.Post, "auth/setup", request, HubUiJsonContext.Default.HubSetupRequest, HubUiJsonContext.Default.HubSessionInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HubSessionInfo> LogInAsync(HubLoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(HttpMethod.Post, "auth/login", request, HubUiJsonContext.Default.HubLoginRequest, HubUiJsonContext.Default.HubSessionInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        return SendNothingAsync(HttpMethod.Post, "auth/logout", cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HubSessionInfo?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync("auth/me", HubUiJsonContext.Default.HubSessionInfo, cancellationToken);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task ChangeOwnPasswordAsync(ChangeOwnPasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendBodyAsync(HttpMethod.Post, "auth/password", request, HubUiJsonContext.Default.ChangeOwnPasswordRequest, cancellationToken);
    }

    /// <inheritdoc />
    public Task<HubInfo> GetHubAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("hub", HubUiJsonContext.Default.HubInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentList> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("agents", HubUiJsonContext.Default.AgentList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<InventoryList> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("inventory", HubUiJsonContext.Default.InventoryList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<InventoryDetail> GetInventoryDetailAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("inventory/" + Uri.EscapeDataString(id), HubUiJsonContext.Default.InventoryDetail, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RolloutOverview> GetRolloutsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("rollouts", HubUiJsonContext.Default.RolloutOverview, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentConfigStatusInfo> PushConfigAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendAsync(HttpMethod.Post, "agents/" + Uri.EscapeDataString(id) + "/config-push", HubUiJsonContext.Default.AgentConfigStatusInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSummary> UpdateAgentTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(tags);

        return SendAsync(
            HttpMethod.Patch,
            "agents/" + Uri.EscapeDataString(id),
            new UpdateAgentRequest(tags),
            HubUiJsonContext.Default.UpdateAgentRequest,
            HubUiJsonContext.Default.AgentSummary,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSummary> ApproveAgentAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendAsync(HttpMethod.Post, "agents/" + Uri.EscapeDataString(id) + "/approve", HubUiJsonContext.Default.AgentSummary, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSummary> RejectAgentAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendAsync(HttpMethod.Post, "agents/" + Uri.EscapeDataString(id) + "/reject", HubUiJsonContext.Default.AgentSummary, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSummary> RotateCredentialAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendAsync(HttpMethod.Post, "agents/" + Uri.EscapeDataString(id) + "/rotate-credential", HubUiJsonContext.Default.AgentSummary, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAgentAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendNothingAsync(HttpMethod.Delete, "agents/" + Uri.EscapeDataString(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<EnrollmentTokenList> GetEnrollmentTokensAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("enrollment-tokens", HubUiJsonContext.Default.EnrollmentTokenList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CreatedEnrollmentToken> CreateEnrollmentTokenAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            HttpMethod.Post,
            "enrollment-tokens",
            request,
            HubUiJsonContext.Default.CreateEnrollmentTokenRequest,
            HubUiJsonContext.Default.CreatedEnrollmentToken,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RevokeEnrollmentTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendNothingAsync(HttpMethod.Delete, "enrollment-tokens/" + Uri.EscapeDataString(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetPairingInfo> StartFleetPairingAsync(CreateFleetPairingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            HttpMethod.Post,
            "fleet-pairings",
            request,
            HubUiJsonContext.Default.CreateFleetPairingRequest,
            HubUiJsonContext.Default.FleetPairingInfo,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task CancelFleetPairingAsync(CancellationToken cancellationToken = default)
    {
        return SendNothingAsync(HttpMethod.Delete, "fleet-pairings", cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetTokenList> GetFleetTokensAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("fleet-tokens", HubUiJsonContext.Default.FleetTokenList, cancellationToken);
    }

    /// <inheritdoc />
    public Task RevokeFleetTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendNothingAsync(HttpMethod.Delete, "fleet-tokens/" + Uri.EscapeDataString(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<UserList> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("users", HubUiJsonContext.Default.UserList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<UserInfo> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(HttpMethod.Post, "users", request, HubUiJsonContext.Default.CreateUserRequest, HubUiJsonContext.Default.UserInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<UserInfo> UpdateUserAsync(string id, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            HttpMethod.Patch,
            "users/" + Uri.EscapeDataString(id),
            request,
            HubUiJsonContext.Default.UpdateUserRequest,
            HubUiJsonContext.Default.UserInfo,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetUserPasswordAsync(string id, SetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(request);

        return SendBodyAsync(HttpMethod.Post, "users/" + Uri.EscapeDataString(id) + "/password", request, HubUiJsonContext.Default.SetPasswordRequest, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteUserAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendNothingAsync(HttpMethod.Delete, "users/" + Uri.EscapeDataString(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<TokenList> GetTokensAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("tokens", HubUiJsonContext.Default.TokenList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CreatedToken> CreateTokenAsync(CreateTokenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(HttpMethod.Post, "tokens", request, HubUiJsonContext.Default.CreateTokenRequest, HubUiJsonContext.Default.CreatedToken, cancellationToken);
    }

    /// <inheritdoc />
    public Task RevokeTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendNothingAsync(HttpMethod.Delete, "tokens/" + Uri.EscapeDataString(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AuditPage> GetAuditAsync(AuditQueryParameters query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return GetAsync("audit" + AuditQueryString(query), HubUiJsonContext.Default.AuditPage, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LibraryDocumentList> GetLibraryAsync(string? kind = null, CancellationToken cancellationToken = default)
    {
        string path = string.IsNullOrEmpty(kind) ? "library" : "library?kind=" + Uri.EscapeDataString(kind);
        return GetAsync(path, HubUiJsonContext.Default.LibraryDocumentList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LibraryDocumentInfo> GetLibraryDocumentAsync(string kind, string id, int? revision = null, CancellationToken cancellationToken = default)
    {
        string path = LibraryPath(kind, id) + (revision is { } number ? "?revision=" + number.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return GetAsync(path, HubUiJsonContext.Default.LibraryDocumentInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LibraryRevisionList> GetLibraryRevisionsAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        return GetAsync(LibraryPath(kind, id) + "/revisions", HubUiJsonContext.Default.LibraryRevisionList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LibraryDocumentInfo> SaveLibraryDocumentAsync(string kind, string id, JsonElement body, CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Put,
            LibraryPath(kind, id),
            new SaveLibraryDocumentRequest(body),
            HubUiJsonContext.Default.SaveLibraryDocumentRequest,
            HubUiJsonContext.Default.LibraryDocumentInfo,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteLibraryDocumentAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        return SendNothingAsync(HttpMethod.Delete, LibraryPath(kind, id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileList> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("profiles", HubUiJsonContext.Default.ProfileList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileInfo> GetProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        return GetAsync(ProfilePath(id), HubUiJsonContext.Default.ProfileInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileInfo> CreateProfileAsync(ProfileDraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(HttpMethod.Post, "profiles", request, HubUiJsonContext.Default.ProfileDraftRequest, HubUiJsonContext.Default.ProfileInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileInfo> UpdateProfileAsync(string id, ProfileDraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(HttpMethod.Put, ProfilePath(id), request, HubUiJsonContext.Default.ProfileDraftRequest, HubUiJsonContext.Default.ProfileInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteProfileAsync(string id, CancellationToken cancellationToken = default)
    {
        return SendNothingAsync(HttpMethod.Delete, ProfilePath(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileRevisionList> GetProfileRevisionsAsync(string id, CancellationToken cancellationToken = default)
    {
        return GetAsync(ProfilePath(id) + "/revisions", HubUiJsonContext.Default.ProfileRevisionList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileRevisionInfo> GetProfileRevisionAsync(string id, int revision, CancellationToken cancellationToken = default)
    {
        return GetAsync(ProfilePath(id) + "/revisions/" + revision.ToString(CultureInfo.InvariantCulture), HubUiJsonContext.Default.ProfileRevisionInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PublishPreview> PreviewProfileAsync(string id, int? revision = null, CancellationToken cancellationToken = default)
    {
        string path = ProfilePath(id) + "/preview" + (revision is { } number ? "?revision=" + number.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return GetAsync(path, HubUiJsonContext.Default.PublishPreview, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PublishResult> PublishProfileAsync(string id, string? note = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Post,
            ProfilePath(id) + "/publish",
            new PublishProfileRequest(note),
            HubUiJsonContext.Default.PublishProfileRequest,
            HubUiJsonContext.Default.PublishResult,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<PublishResult> RollbackProfileAsync(string id, int revision, string? note = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(
            HttpMethod.Post,
            ProfilePath(id) + "/rollback",
            new RollbackProfileRequest(revision, note),
            HubUiJsonContext.Default.RollbackProfileRequest,
            HubUiJsonContext.Default.PublishResult,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ProfileRolloutInfo> GetProfileRolloutAsync(string id, int? revision = null, CancellationToken cancellationToken = default)
    {
        string path = ProfilePath(id) + "/rollout" + (revision is { } number ? "?revision=" + number.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return GetAsync(path, HubUiJsonContext.Default.ProfileRolloutInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AssignmentList> GetAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("assignments", HubUiJsonContext.Default.AssignmentList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<CreatedAssignment> CreateAssignmentAsync(CreateAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            HttpMethod.Post,
            "assignments",
            request,
            HubUiJsonContext.Default.CreateAssignmentRequest,
            HubUiJsonContext.Default.CreatedAssignment,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAssignmentAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendNothingAsync(HttpMethod.Delete, "assignments/" + Uri.EscapeDataString(id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentDesiredInfo> GetAgentDesiredStateAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("agents/" + Uri.EscapeDataString(id) + "/desired-state", HubUiJsonContext.Default.AgentDesiredInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentMetricList> GetAgentMetricsAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("agents/" + Uri.EscapeDataString(id) + "/metrics", HubUiJsonContext.Default.AgentMetricList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MetricQueryResult> QueryMetricsAsync(MetricQueryParameters query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return GetAsync("metrics/query" + MetricQueryString(query), HubUiJsonContext.Default.MetricQueryResult, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetVersionOverview> GetFleetVersionsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("fleet-versions", HubUiJsonContext.Default.FleetVersionOverview, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetActionList> GetFleetActionsAsync(CancellationToken cancellationToken = default)
    {
        return GetAsync("fleet-actions", HubUiJsonContext.Default.FleetActionList, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetActionInfo> GetFleetActionAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return GetAsync("fleet-actions/" + Uri.EscapeDataString(id), HubUiJsonContext.Default.FleetActionInfo, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetActionInfo> StartBulkRunAsync(StartBulkRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            HttpMethod.Post,
            "fleet-actions/bulk-runs",
            request,
            HubUiJsonContext.Default.StartBulkRunRequest,
            HubUiJsonContext.Default.FleetActionInfo,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetActionInfo> StartFleetUpdateAsync(StartFleetUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync(
            HttpMethod.Post,
            "fleet-actions/updates",
            request,
            HubUiJsonContext.Default.StartFleetUpdateRequest,
            HubUiJsonContext.Default.FleetActionInfo,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FleetActionInfo> CancelFleetActionAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return SendAsync(HttpMethod.Post, "fleet-actions/" + Uri.EscapeDataString(id) + "/cancel", HubUiJsonContext.Default.FleetActionInfo, cancellationToken);
    }

    /// <summary>Builds the query string of a metric query: agents and metrics comma separated, times in ISO 8601 (UTC), empty members left out.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The query string with its leading question mark.</returns>
    public static string MetricQueryString(MetricQueryParameters query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>
        {
            "agent=" + Uri.EscapeDataString(string.Join(',', query.Agents)),
        };
        if (query.Metrics.Count > 0)
        {
            parts.Add("metric=" + Uri.EscapeDataString(string.Join(',', query.Metrics)));
        }

        if (query.From is { } from)
        {
            parts.Add("from=" + Uri.EscapeDataString(from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        if (query.To is { } to)
        {
            parts.Add("to=" + Uri.EscapeDataString(to.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        AddPart(parts, "resolution", query.Resolution);
        if (query.MaxPoints is { } maxPoints)
        {
            parts.Add("maxPoints=" + maxPoints.ToString(CultureInfo.InvariantCulture));
        }

        return "?" + string.Join('&', parts);
    }

    private static string LibraryPath(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);

        return "library/" + Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(id);
    }

    private static string ProfilePath(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        return "profiles/" + Uri.EscapeDataString(id);
    }

    /// <summary>Builds the query string of an audit request; empty filters are left out.</summary>
    /// <param name="query">The filters and the page.</param>
    /// <returns>The query string with its leading question mark, always present because the limit is always sent.</returns>
    public static string AuditQueryString(AuditQueryParameters query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>();
        AddPart(parts, "user", query.User);
        AddPart(parts, "action", query.Action);
        AddPart(parts, "result", query.Result);
        AddPart(parts, "agent", query.Agent);
        if (query.Before is { } before)
        {
            parts.Add("before=" + before.ToString(CultureInfo.InvariantCulture));
        }

        parts.Add("limit=" + query.Limit.ToString(CultureInfo.InvariantCulture));
        return "?" + string.Join('&', parts);
    }

    private static void AddPart(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(name + "=" + Uri.EscapeDataString(value.Trim()));
        }
    }

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        using HttpResponseMessage response = await SendCoreAsync(HttpMethod.Get, path, null, cancellationToken);
        return await ReadAsync(response, typeInfo, cancellationToken);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest body,
        JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResponse> responseInfo,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using HttpResponseMessage response = await SendCoreAsync(method, path, JsonContent.Create(body, requestInfo), cancellationToken);
        return await ReadAsync(response, responseInfo, cancellationToken);
    }

    private async Task<TResponse> SendAsync<TResponse>(HttpMethod method, string path, JsonTypeInfo<TResponse> responseInfo, CancellationToken cancellationToken)
        where TResponse : class
    {
        using HttpResponseMessage response = await SendCoreAsync(method, path, null, cancellationToken);
        return await ReadAsync(response, responseInfo, cancellationToken);
    }

    private async Task SendBodyAsync<TRequest>(HttpMethod method, string path, TRequest body, JsonTypeInfo<TRequest> requestInfo, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendCoreAsync(method, path, JsonContent.Create(body, requestInfo), cancellationToken);
    }

    private async Task SendNothingAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendCoreAsync(method, path, null, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiBase + path, UriKind.Relative)) { Content = content };

        // The session cookie is HttpOnly and SameSite=Strict: the browser attaches it to requests of this origin.
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        if (ChangesState(method))
        {
            request.Headers.Add(ApiHeaders.CsrfName, ApiHeaders.CsrfValue);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw NetworkError(exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The HTTP client gave up (its own time limit), which is not the caller cancelling.
            throw NetworkError(exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ToExceptionAsync(response, cancellationToken);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)
                ?? throw InvalidAnswer(response, null);
        }
        catch (JsonException exception)
        {
            throw InvalidAnswer(response, exception);
        }
        catch (HttpRequestException exception)
        {
            throw NetworkError(exception);
        }
    }

    private static async Task<ApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;
        ApiProblem? problem = null;
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                problem = JsonSerializer.Deserialize(body, HubUiJsonContext.Default.ApiProblem);
            }
        }
        catch (JsonException)
        {
            // Not problem details: the status alone describes the error.
        }
        catch (HttpRequestException)
        {
            // The body could not be read: same.
        }

        problem ??= new ApiProblem("about:blank", response.ReasonPhrase ?? "Error", status);
        int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
        return new ApiException(status, problem, retryAfter);
    }

    private static ApiException InvalidAnswer(HttpResponseMessage response, Exception? cause)
    {
        int status = (int)response.StatusCode;
        var problem = new ApiProblem("about:blank", "Invalid answer", status, "The answer of the hub is not understood.", "invalid_response");
        return new ApiException(status, problem, innerException: cause);
    }

    private static ApiException NetworkError(Exception cause)
    {
        var problem = new ApiProblem("about:blank", "Network error", 0, "The hub cannot be reached.", ApiException.NetworkErrorCode);
        return new ApiException(0, problem, innerException: cause);
    }

    private static bool ChangesState(HttpMethod method)
    {
        return method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;
    }
}
