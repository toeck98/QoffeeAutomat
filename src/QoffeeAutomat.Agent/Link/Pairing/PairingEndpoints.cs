using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Agent.Security;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Link.Pairing;

/// <summary>
/// The pairing endpoint of QoffeeLink, <c>POST /link/v1/pair</c> (PLAN.md S07), and the management
/// endpoints that start pairing and manage the paired clients (PLAN.md S08). The pairing endpoint needs
/// no bearer token, only a valid one-time code, and it answers on the HTTPS listener only. The
/// management endpoints sit below <c>/api/v1</c> and are protected by the session policy.
/// </summary>
public static class PairingEndpoints
{
    /// <summary>The largest pairing request body that is read.</summary>
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary>Maps <c>POST /link/v1/pair</c>.</summary>
    /// <param name="routes">The route builder.</param>
    /// <returns>The builder of the endpoint.</returns>
    public static RouteHandlerBuilder MapLink(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes.MapPost(ProtocolInfo.PairPath, PairAsync);
    }

    /// <summary>Maps the pairing and client endpoints below the management API group.</summary>
    /// <param name="api">The <c>/api/v1</c> group, which already requires a session.</param>
    public static void MapManagement(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Link";
        api.MapPost("/link/pairing", StartPairing)
            .Describe(tag, "startPairing", "Starts pairing: creates a one-time code that is valid for five minutes and returns it with the certificate fingerprint.")
            .Returns<PairingInfo>()
            .Fails(StatusCodes.Status403Forbidden, StatusCodes.Status503ServiceUnavailable);
        api.MapGet("/link/pairing", GetPairing)
            .Describe(tag, "getPairing", "Reads the active pairing code; answers 204 when there is none.")
            .Returns<PairingInfo>()
            .ReturnsNothing();
        api.MapDelete("/link/pairing", CancelPairing)
            .Describe(tag, "cancelPairing", "Cancels the active pairing code.")
            .ReturnsNothing();
        api.MapGet("/link/clients", ListClientsAsync)
            .Describe(tag, "listLinkClients", "Lists the paired QoffeeLink clients.")
            .Returns<LinkClientList>();
        api.MapDelete("/link/clients/{id}", RevokeClientAsync)
            .Describe(tag, "revokeLinkClient", "Revokes a paired client and closes its open connections.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> PairAsync(
        HttpContext context,
        PairingService pairing,
        IConfigStore config,
        IPlatformInfo platform,
        IHubPolicy policy,
        ILogger<PairingService> logger,
        CancellationToken cancellationToken)
    {
        if (!context.Request.IsHttps)
        {
            return ApiResults.Problem(StatusCodes.Status403Forbidden, "https_required", "Pairing is only available over HTTPS.");
        }

        if (policy.DirectLinkDenied)
        {
            return DirectLinkDenied();
        }

        (PairRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, LinkJsonContext.Default.PairRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        string? invalid = PairingService.ValidateClient(request.Client);
        if (invalid is not null)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, invalid);
        }

        PairResult result = await pairing.PairAsync(request.Code, request.Client, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            SecurityLog.PairingRefused(logger, result.Attempt);
            return ApiResults.Problem(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "The pairing code is invalid or has expired.");
        }

        SecurityLog.ClientPaired(logger, result.Client!.Kind, result.Client.Id);

        string agentId = await platform.GetOrCreateAgentIdAsync(cancellationToken).ConfigureAwait(false);
        string agentName = config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.Name ?? Environment.MachineName;
        var response = new PairResponse(result.Token!, new PairAgentInfo(agentId, agentName));

        // The response holds the only copy of the token in plain text.
        context.Response.Headers.CacheControl = "no-store";
        return ApiResults.Json(response, LinkJsonContext.Default.PairResponse);
    }

    /// <summary>The answer to a pairing attempt, or a request for a pairing code, while the hub denies direct links (PLAN.md S20, D-029).</summary>
    private static IResult DirectLinkDenied()
    {
        return ApiResults.Problem(
            StatusCodes.Status403Forbidden,
            HubConfigProblemCodes.DirectLinkDenied,
            "The hub that manages this PC does not allow direct pairing. Connect Home Assistant to the hub instead.");
    }

    private static IResult StartPairing(PairingService pairing, IHubPolicy policy)
    {
        if (policy.DirectLinkDenied)
        {
            return DirectLinkDenied();
        }

        string? fingerprint = pairing.Fingerprint;
        if (fingerprint is null)
        {
            return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "no_certificate", "The agent has no TLS certificate, so clients cannot pair.");
        }

        PairingCode code = pairing.StartPairing();
        return ApiResults.Json(new PairingInfo(code.Code, code.ExpiresAt, fingerprint), ApiJsonContext.Default.PairingInfo);
    }

    private static IResult GetPairing(PairingService pairing)
    {
        PairingCode? code = pairing.ActivePairing();
        string? fingerprint = pairing.Fingerprint;
        if (code is null || fingerprint is null)
        {
            return Results.NoContent();
        }

        return ApiResults.Json(new PairingInfo(code.Code, code.ExpiresAt, fingerprint), ApiJsonContext.Default.PairingInfo);
    }

    private static IResult CancelPairing(PairingService pairing)
    {
        pairing.CancelPairing();
        return Results.NoContent();
    }

    private static async Task<IResult> ListClientsAsync(PairingService pairing, CancellationToken cancellationToken)
    {
        IReadOnlyList<LinkClient> clients = await pairing.ListClientsAsync(cancellationToken).ConfigureAwait(false);
        var list = new LinkClientList(
            [.. clients.Select(client => new LinkClientInfo(
                client.Id,
                client.Name,
                client.Kind,
                client.CreatedAt,
                client.LastSeenAt,
                pairing.Connections.Count(client.Id)))]);
        return ApiResults.Json(list, ApiJsonContext.Default.LinkClientList);
    }

    private static async Task<IResult> RevokeClientAsync(
        string id,
        PairingService pairing,
        ILogger<PairingService> logger,
        CancellationToken cancellationToken)
    {
        if (!await pairing.RevokeAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return ApiResults.Problem(StatusCodes.Status404NotFound, "unknown_client", "There is no paired client with this id.");
        }

        SecurityLog.ClientRevoked(logger, id);
        return Results.NoContent();
    }
}
