using System.Globalization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Config;

/// <summary>
/// The configuration endpoints of the hub API (PLAN.md S20, T907): the library of documents, profiles with their drafts and published revisions,
/// assignments, the preview, publish and roll-back of a profile, and the desired state of an agent. Everyone who is signed in may read. Changing the
/// library, a draft or an assignment needs the role admin. Publishing and rolling back are open to the role operator, with one exception that the
/// service enforces: content with system scripts (commands that run code as <c>system</c>) can only be published by an administrator, and the
/// preview says so beforehand. Every change is audited with the profile or document and the agents that got a new generation.
/// </summary>
public static class ConfigEndpoints
{
    /// <summary>The largest request body of a library document that is read.</summary>
    public const int MaxLibraryBodyBytes = 1024 * 1024;

    /// <summary>The largest request body of the other endpoints that is read.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        MapLibrary(api);
        MapProfiles(api);
        MapAssignments(api);

        api.MapGet("/agents/{id}/desired-state", GetDesiredStateAsync)
            .Describe("Agents", "getAgentDesiredState", "Shows the desired state that the published profiles yield for an agent now: documents, policy, the profiles that contribute and the warnings about overrides. Also shows the generation the agent was last told about.")
            .Returns<AgentDesiredInfo>()
            .Fails(StatusCodes.Status404NotFound);
    }

    private static void MapLibrary(RouteGroupBuilder api)
    {
        const string tag = "Library";
        api.MapGet("/library", ListLibraryAsync)
            .Describe(tag, "listLibraryDocuments", "Lists the documents of the library (commands, sensors, scripts, modes, metric overrides and variables) with their current revision.")
            .Query("kind", "string", "Only documents of this kind.")
            .Returns<LibraryDocumentList>()
            .Fails(HubApiResults.UnprocessableContent);
        api.MapGet("/library/{kind}/{id}", GetLibraryDocumentAsync)
            .Describe(tag, "getLibraryDocument", "Returns the current revision of a library document with its body, or the revision named by the query.")
            .Query("revision", "integer", "A revision other than the current one.")
            .Returns<LibraryDocumentInfo>()
            .Fails(StatusCodes.Status404NotFound, HubApiResults.UnprocessableContent);
        api.MapGet("/library/{kind}/{id}/revisions", ListLibraryRevisionsAsync)
            .Describe(tag, "listLibraryRevisions", "Lists the revisions of a library document, newest first.")
            .Returns<LibraryRevisionList>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPut("/library/{kind}/{id}", SaveLibraryDocumentAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "saveLibraryDocument", "Validates a library document with the validators of the agent and saves it as the next revision. A document whose content equals the current revision adds no revision.")
            .Takes<SaveLibraryDocumentRequest>()
            .Returns<LibraryDocumentInfo>()
            .Returns<LibraryDocumentInfo>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status413PayloadTooLarge, HubApiResults.UnprocessableContent);
        api.MapDelete("/library/{kind}/{id}", DeleteLibraryDocumentAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "deleteLibraryDocument", "Deletes a library document that no profile draft uses. Its revisions stay for the published profile revisions that pin them.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    private static void MapProfiles(RouteGroupBuilder api)
    {
        const string tag = "Profiles";
        api.MapGet("/profiles", ListProfilesAsync)
            .Describe(tag, "listProfiles", "Lists the profiles with their drafts and the revision that is in effect.")
            .Returns<ProfileList>();
        api.MapPost("/profiles", CreateProfileAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "createProfile", "Creates a profile with a draft. Nothing is published, so no agent is affected.")
            .Takes<ProfileDraftRequest>()
            .Returns<ProfileInfo>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status413PayloadTooLarge, HubApiResults.UnprocessableContent);
        api.MapGet("/profiles/{id}", GetProfileAsync)
            .Describe(tag, "getProfile", "Returns a profile with its draft.")
            .Returns<ProfileInfo>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPut("/profiles/{id}", UpdateProfileAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "updateProfile", "Replaces the draft of a profile. Published revisions and agents are not affected until the next publish.")
            .Takes<ProfileDraftRequest>()
            .Returns<ProfileInfo>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status413PayloadTooLarge, HubApiResults.UnprocessableContent);
        api.MapDelete("/profiles/{id}", DeleteProfileAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "deleteProfile", "Deletes a profile with its revisions and assignments. The agents it reached get a new generation.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
        api.MapGet("/profiles/{id}/revisions", ListProfileRevisionsAsync)
            .Describe(tag, "listProfileRevisions", "Lists the published revisions of a profile, newest first.")
            .Returns<ProfileRevisionList>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapGet("/profiles/{id}/revisions/{revision}", GetProfileRevisionAsync)
            .Describe(tag, "getProfileRevision", "Returns one immutable published revision of a profile with its frozen content.")
            .Returns<ProfileRevisionInfo>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapGet("/profiles/{id}/preview", PreviewProfileAsync)
            .Describe(tag, "previewProfilePublish", "Shows what publishing the draft, or republishing the revision named by the query, would do: exactly the agents whose desired state would change with the differences, and whether the content needs the role admin. Changes nothing.")
            .Query("revision", "integer", "An earlier revision to republish instead of the draft.")
            .Returns<PublishPreview>()
            .Fails(StatusCodes.Status404NotFound, HubApiResults.UnprocessableContent);
        api.MapPost("/profiles/{id}/publish", PublishProfileAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(tag, "publishProfile", "Publishes the draft as the next immutable revision and gives every agent whose desired state changed a new generation. Content equal to what is in effect changes nothing. Content with system scripts needs the role admin.")
            .Takes<PublishProfileRequest>(optional: true)
            .Returns<PublishResult>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status413PayloadTooLarge, HubApiResults.UnprocessableContent);
        api.MapPost("/profiles/{id}/rollback", RollbackProfileAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(tag, "rollbackProfile", "Republishes an earlier revision, which is the roll-back. Agents whose desired state changed get a new generation. Content with system scripts needs the role admin.")
            .Takes<RollbackProfileRequest>()
            .Returns<PublishResult>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status413PayloadTooLarge, HubApiResults.UnprocessableContent);
    }

    private static void MapAssignments(RouteGroupBuilder api)
    {
        const string tag = "Assignments";
        api.MapGet("/assignments", ListAssignmentsAsync)
            .Describe(tag, "listAssignments", "Lists the assignments of profiles to the targets all, tag:<tag> and agent:<id>.")
            .Returns<AssignmentList>();
        api.MapPost("/assignments", CreateAssignmentAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "createAssignment", "Assigns a profile to a target. It takes effect at once: agents whose desired state changed get a new generation.")
            .Takes<CreateAssignmentRequest>()
            .Returns<CreatedAssignment>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status413PayloadTooLarge, HubApiResults.UnprocessableContent);
        api.MapDelete("/assignments/{id}", DeleteAssignmentAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "deleteAssignment", "Removes an assignment. Agents whose desired state changed get a new generation.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
    }

    // Helpers -----------------------------------------------------------------------------------------------------------------------

    private static string UserName(HttpContext context) => HubPrincipal.Name(context.User) ?? "unknown";

    private static string UserRole(HttpContext context) => HubPrincipal.Role(context.User) ?? HubRoles.Viewer;

    private static void Audit(HttpContext context, string target)
    {
        context.Audit().Target = target;
    }

    private static void AuditAgents(HttpContext context, IEnumerable<AgentChange> changes)
    {
        ISet<string> agents = context.Audit().Agents;
        foreach (AgentChange change in changes)
        {
            agents.Add(change.AgentId);
        }
    }

    private static IResult UnknownKind() => HubApiResults.NotFound("unknown_kind", "The kind is not one of command, sensor, script, mode, metric_overrides and variables.");

    private static IResult Failure<T>(ConfigResult<T> result)
    {
        switch (result.Status)
        {
            case ConfigStatus.NotFound:
                return HubApiResults.NotFound(result.Code ?? "not_found", result.Message ?? "The object does not exist.");
            case ConfigStatus.Invalid:
                return HubApiResults.Problem(
                    HubApiResults.UnprocessableContent,
                    "validation_failed",
                    "The request has a validation error.",
                    [.. result.Errors.Select(static error => new ApiValidationError(error.Path, error.Code, error.Message))]);
            case ConfigStatus.Conflict:
                return HubApiResults.Problem(StatusCodes.Status409Conflict, result.Code ?? "conflict", result.Message ?? "The request conflicts with the state of the hub.");
            case ConfigStatus.Forbidden:
                return HubApiResults.Problem(StatusCodes.Status403Forbidden, result.Code ?? HubErrorCodes.Forbidden, result.Message ?? "The role is not sufficient.");
            default:
                return HubApiResults.Problem(StatusCodes.Status500InternalServerError, "internal_error", "The operation failed.");
        }
    }

    /// <summary>Reads the optional query <c>revision</c>: null when it is absent, a problem when it is not a positive whole number.</summary>
    private static (int? Revision, IResult? Problem) ReadRevision(HttpContext context)
    {
        string text = context.Request.Query["revision"].ToString();
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int revision) && revision > 0)
        {
            return (revision, null);
        }

        return (null, HubApiResults.Invalid("/revision", "invalid", "The revision must be a positive whole number."));
    }

    private static bool HasBody(HttpRequest request)
    {
        return request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0;
    }

    // Library -----------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListLibraryAsync(HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        string kind = context.Request.Query["kind"].ToString();
        if (kind.Length > 0 && !DocumentKinds.All.Contains(kind))
        {
            return HubApiResults.Invalid("/kind", "invalid", "The kind is not one of command, sensor, script, mode, metric_overrides and variables.");
        }

        IReadOnlyList<LibraryDocumentSummary> documents = await service.ListLibraryAsync(kind.Length > 0 ? kind : null, cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new LibraryDocumentList(documents), HubApiJsonContext.Default.LibraryDocumentList);
    }

    private static async Task<IResult> GetLibraryDocumentAsync(string kind, string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        (int? revision, IResult? problem) = ReadRevision(context);
        if (problem is not null)
        {
            return problem;
        }

        LibraryDocumentInfo? document = await service.GetLibraryDocumentAsync(kind, id, revision, cancellationToken).ConfigureAwait(false);
        return document is null
            ? HubApiResults.NotFound("unknown_document", "There is no such library document.")
            : HubApiResults.Json(document, HubApiJsonContext.Default.LibraryDocumentInfo);
    }

    private static async Task<IResult> ListLibraryRevisionsAsync(string kind, string id, ConfigService service, CancellationToken cancellationToken)
    {
        LibraryRevisionList? revisions = await service.ListLibraryRevisionsAsync(kind, id, cancellationToken).ConfigureAwait(false);
        return revisions is null
            ? HubApiResults.NotFound("unknown_document", "There is no such library document.")
            : HubApiResults.Json(revisions, HubApiJsonContext.Default.LibraryRevisionList);
    }

    private static async Task<IResult> SaveLibraryDocumentAsync(string kind, string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "library:" + kind + "/" + id);
        if (!DocumentKinds.All.Contains(kind))
        {
            return UnknownKind();
        }

        (SaveLibraryDocumentRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.SaveLibraryDocumentRequest, MaxLibraryBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (request.Body is not { } body)
        {
            return HubApiResults.Invalid("/body", "required", "The body is required: the document, for a script the script text as a JSON string.");
        }

        ConfigResult<LibraryDocumentInfo> result = await service.SaveLibraryDocumentAsync(kind, id, body, UserName(context), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? HubApiResults.Json(
                result.Value!,
                HubApiJsonContext.Default.LibraryDocumentInfo,
                result.Status == ConfigStatus.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK)
            : Failure(result);
    }

    private static async Task<IResult> DeleteLibraryDocumentAsync(string kind, string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "library:" + kind + "/" + id);
        ConfigResult<bool> result = await service.DeleteLibraryDocumentAsync(kind, id, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Failure(result);
    }

    // Profiles ----------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListProfilesAsync(ConfigService service, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileInfo> profiles = await service.ListProfilesAsync(cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new ProfileList(profiles), HubApiJsonContext.Default.ProfileList);
    }

    private static async Task<IResult> GetProfileAsync(string id, ConfigService service, CancellationToken cancellationToken)
    {
        ProfileInfo? profile = await service.GetProfileAsync(id, cancellationToken).ConfigureAwait(false);
        return profile is null
            ? HubApiResults.NotFound("unknown_profile", "There is no such profile.")
            : HubApiResults.Json(profile, HubApiJsonContext.Default.ProfileInfo);
    }

    private static async Task<IResult> CreateProfileAsync(HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        (ProfileDraftRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.ProfileDraftRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        Audit(context, "profile:" + request.Id);
        ConfigResult<ProfileInfo> result = await service.CreateProfileAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? HubApiResults.Json(result.Value!, HubApiJsonContext.Default.ProfileInfo, StatusCodes.Status201Created)
            : Failure(result);
    }

    private static async Task<IResult> UpdateProfileAsync(string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "profile:" + id);
        (ProfileDraftRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.ProfileDraftRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        ConfigResult<ProfileInfo> result = await service.UpdateProfileAsync(id, request, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? HubApiResults.Json(result.Value!, HubApiJsonContext.Default.ProfileInfo) : Failure(result);
    }

    private static async Task<IResult> DeleteProfileAsync(string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "profile:" + id);
        ConfigResult<IReadOnlyList<AgentChange>> result = await service.DeleteProfileAsync(id, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure(result);
        }

        AuditAgents(context, result.Value!);
        return Results.NoContent();
    }

    private static async Task<IResult> ListProfileRevisionsAsync(string id, ConfigService service, CancellationToken cancellationToken)
    {
        ProfileRevisionList? revisions = await service.ListProfileRevisionsAsync(id, cancellationToken).ConfigureAwait(false);
        return revisions is null
            ? HubApiResults.NotFound("unknown_profile", "There is no such profile.")
            : HubApiResults.Json(revisions, HubApiJsonContext.Default.ProfileRevisionList);
    }

    private static async Task<IResult> GetProfileRevisionAsync(string id, int revision, ConfigService service, CancellationToken cancellationToken)
    {
        ProfileRevisionInfo? info = await service.GetProfileRevisionAsync(id, revision, cancellationToken).ConfigureAwait(false);
        return info is null
            ? HubApiResults.NotFound("unknown_revision", "The profile or the revision does not exist.")
            : HubApiResults.Json(info, HubApiJsonContext.Default.ProfileRevisionInfo);
    }

    private static async Task<IResult> PreviewProfileAsync(string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        (int? revision, IResult? problem) = ReadRevision(context);
        if (problem is not null)
        {
            return problem;
        }

        ConfigResult<PublishPreview> result = await service.PreviewAsync(id, revision, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? HubApiResults.Json(result.Value!, HubApiJsonContext.Default.PublishPreview) : Failure(result);
    }

    private static async Task<IResult> PublishProfileAsync(string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "profile:" + id);
        string? note = null;
        if (HasBody(context.Request))
        {
            (PublishProfileRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
                context.Request, HubApiJsonContext.Default.PublishProfileRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return failure!;
            }

            note = request.Note;
        }

        return await PublishAsync(id, null, note, context, service, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> RollbackProfileAsync(string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "profile:" + id);
        (RollbackProfileRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.RollbackProfileRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (request.Revision is not { } revision)
        {
            return HubApiResults.Invalid("/revision", "required", "The revision to republish is required.");
        }

        return await PublishAsync(id, revision, request.Note, context, service, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> PublishAsync(
        string id,
        int? revision,
        string? note,
        HttpContext context,
        ConfigService service,
        CancellationToken cancellationToken)
    {
        ConfigResult<PublishResult> result = await service.PublishAsync(id, revision, note, UserName(context), UserRole(context), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure(result);
        }

        AuditAgents(context, result.Value!.Agents);
        return HubApiResults.Json(result.Value!, HubApiJsonContext.Default.PublishResult);
    }

    // Assignments -------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListAssignmentsAsync(ConfigService service, CancellationToken cancellationToken)
    {
        IReadOnlyList<AssignmentInfo> assignments = await service.ListAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new AssignmentList(assignments), HubApiJsonContext.Default.AssignmentList);
    }

    private static async Task<IResult> CreateAssignmentAsync(HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        (CreateAssignmentRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.CreateAssignmentRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        Audit(context, "assignment:" + request.ProfileId + "@" + request.Target);
        ConfigResult<CreatedAssignment> result = await service.CreateAssignmentAsync(request.ProfileId, request.Target, UserName(context), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure(result);
        }

        AuditAgents(context, result.Value!.Agents);
        return HubApiResults.Json(result.Value!, HubApiJsonContext.Default.CreatedAssignment, StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteAssignmentAsync(string id, HttpContext context, ConfigService service, CancellationToken cancellationToken)
    {
        Audit(context, "assignment:" + id);
        ConfigResult<IReadOnlyList<AgentChange>> result = await service.DeleteAssignmentAsync(id, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Failure(result);
        }

        AuditAgents(context, result.Value!);
        return Results.NoContent();
    }

    // Desired state -----------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> GetDesiredStateAsync(string id, ConfigService service, CancellationToken cancellationToken)
    {
        AgentDesiredInfo? desired = await service.GetDesiredStateAsync(id, cancellationToken).ConfigureAwait(false);
        return desired is null
            ? HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent.")
            : HubApiResults.Json(desired, HubApiJsonContext.Default.AgentDesiredInfo);
    }
}
