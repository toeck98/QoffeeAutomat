using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>
/// The hub endpoints of the management API (PLAN.md S08, S20, T905): <c>GET /api/v1/hub</c> reports the hub link, <c>POST /api/v1/hub/join</c> and
/// <c>POST /api/v1/hub/leave</c> do what the verbs <c>hub join</c> and <c>hub leave</c> do, and <c>PATCH /api/v1/hub</c> switches the management tunnel
/// off or on locally. Like the rest of the API they need the session of the local administrator and the anti-forgery header. Joining gives the hub full
/// control over this PC, so the UI says so before it calls the endpoint.
/// </summary>
public static class HubEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    private const string Tag = "Hub";

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/hub", GetHubAsync)
            .Describe(Tag, "getHub", "Reports the hub link: not joined, pending, connected, disconnected or revoked, the hub and the local switch of the management tunnel.")
            .Returns<HubLinkStatus>();
        api.MapGet("/hub/config", GetHubConfigAsync)
            .Describe(Tag, "getHubConfig", "Reports what the hub configured on this agent: the applied generation, the policy, the documents of the hub layer, the local documents that are kept but not used, and the names of the hub variables.")
            .Returns<HubConfigState>();
        api.MapPatch("/hub", PatchHubAsync)
            .Describe(Tag, "updateHub", "Switches the management tunnel on or off locally. The hub learns of it when the link is renewed, which happens at once.")
            .Takes<HubSettingsRequest>()
            .Returns<HubLinkStatus>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status500InternalServerError);
        api.MapPost("/hub/join", JoinAsync)
            .Describe(Tag, "joinHub", "Joins a hub with the enrollment string that the hub shows. This gives the hub full control over this PC. The hub is asked at once; a token that needs approval leaves the agent pending.")
            .Takes<HubJoinRequest>()
            .Returns<HubLinkStatus>()
            .Fails(
                StatusCodes.Status400BadRequest,
                StatusCodes.Status409Conflict,
                ApiResults.UnprocessableContent,
                StatusCodes.Status500InternalServerError,
                StatusCodes.Status502BadGateway);
        api.MapPost("/hub/leave", LeaveAsync)
            .Describe(Tag, "leaveHub", "Leaves the hub: deletes the credential and stops the hub link. The local configuration becomes active again.")
            .Takes<HubLeaveRequest>(optional: true)
            .Returns<HubLinkStatus>()
            .Fails(StatusCodes.Status400BadRequest, ApiResults.UnprocessableContent, StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> GetHubAsync(HubLinkService service, CancellationToken cancellationToken)
    {
        return ApiResults.Json(await service.GetStatusAsync(cancellationToken).ConfigureAwait(false), ApiJsonContext.Default.HubLinkStatus);
    }

    private static async Task<IResult> GetHubConfigAsync(IConfigStore config, CancellationToken cancellationToken)
    {
        return ApiResults.Json(await HubConfigOverview.DescribeAsync(config, cancellationToken).ConfigureAwait(false), ApiJsonContext.Default.HubConfigState);
    }

    private static async Task<IResult> PatchHubAsync(HttpContext context, HubLinkManager manager, HubLinkService service, CancellationToken cancellationToken)
    {
        (HubSettingsRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.HubSettingsRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        try
        {
            await manager.SetManagementTunnelAsync(request.ManagementTunnel, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }

        return ApiResults.Json(await service.GetStatusAsync(cancellationToken).ConfigureAwait(false), ApiJsonContext.Default.HubLinkStatus);
    }

    private static async Task<IResult> JoinAsync(HttpContext context, HubLinkManager manager, HubLinkService service, CancellationToken cancellationToken)
    {
        (HubJoinRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.HubJoinRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.Enrollment))
        {
            return ApiResults.ValidationFailed([new ValidationError("/enrollment", ValidationCodes.Required, "enrollment is required.")]);
        }

        HubJoinResult result;
        try
        {
            result = await manager.JoinAsync(request.Enrollment, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }

        // The message never contains the token or the credential.
        string message = result.Error ?? "The agent could not join the hub.";
        return result.Failure switch
        {
            HubJoinFailure.None => ApiResults.Json(await service.GetStatusAsync(cancellationToken).ConfigureAwait(false), ApiJsonContext.Default.HubLinkStatus),
            HubJoinFailure.Invalid => ApiResults.ValidationFailed([new ValidationError("/enrollment", ValidationCodes.Invalid, message)]),
            HubJoinFailure.AlreadyJoined => ApiResults.Problem(StatusCodes.Status409Conflict, "already_joined", message),
            HubJoinFailure.Refused => ApiResults.Problem(ApiResults.UnprocessableContent, "enrollment_refused", message),
            _ => ApiResults.Problem(StatusCodes.Status502BadGateway, "hub_unreachable", message),
        };
    }

    private static async Task<IResult> LeaveAsync(HttpContext context, HubLinkManager manager, HubLinkService service, CancellationToken cancellationToken)
    {
        (HubLeaveRequest? request, IResult? failure) = await ApiResults.ReadOptionalBodyAsync(
            context.Request, ApiJsonContext.Default.HubLeaveRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        string? documents = request?.Documents;
        if (documents is not (null or HubLinkManager.DocumentsConvert or HubLinkManager.DocumentsRemove))
        {
            return ApiResults.ValidationFailed(
                [new ValidationError("/documents", ValidationCodes.Invalid, "documents must be 'convert' or 'remove'.")]);
        }

        try
        {
            _ = await manager.LeaveAsync(documents, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }

        return ApiResults.Json(await service.GetStatusAsync(cancellationToken).ConfigureAwait(false), ApiJsonContext.Default.HubLinkStatus);
    }

    private static IResult StoreUnavailable(SecretStoreException exception)
    {
        // The message of the exception never contains a secret value.
        return ApiResults.Problem(StatusCodes.Status500InternalServerError, "secret_store_unavailable", exception.Message);
    }
}
