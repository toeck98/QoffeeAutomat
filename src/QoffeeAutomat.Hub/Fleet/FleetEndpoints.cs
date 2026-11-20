using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>
/// The fleet endpoints of the hub API (PLAN.md S20 "Fleet link", T913): an administrator creates the pairing code that Home Assistant enters (<c>POST /api/v1/fleet-pairings</c>),
/// with a scope of all agents or of a set of tags, lists the fleet tokens that pairing produced and revokes them. Only an administrator may call them: a fleet token lets
/// Home Assistant run commands on every PC in its scope. Revoking a token closes its open connections. The pairing code is in the answer to its creation and nowhere else; it is
/// not written to the audit log.
/// </summary>
public static class FleetEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>The longest label of a token.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Maps the endpoints below <c>/api/v1/fleet-pairings</c> and <c>/api/v1/fleet-tokens</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Fleet";
        api.MapPost("/fleet-pairings", CreatePairingAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "createFleetPairing", "Creates the pairing code for Home Assistant: six digits, valid for five minutes, single-use. The token that the pairing produces sees and controls all agents or only the agents with one of the given tags. A new code replaces the previous one.")
            .Takes<CreateFleetPairingRequest>()
            .Returns<FleetPairingInfo>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, HubApiResults.UnprocessableContent);
        api.MapDelete("/fleet-pairings", CancelPairing).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "cancelFleetPairing", "Cancels the active pairing code, if there is one.")
            .ReturnsNothing();
        api.MapGet("/fleet-tokens", ListTokensAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "listFleetTokens", "Lists the fleet tokens, including revoked ones, with their scope, their last use and the number of connections they have open. Secrets are never returned.")
            .Returns<FleetTokenList>();
        api.MapDelete("/fleet-tokens/{id}", RevokeTokenAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "revokeFleetToken", "Revokes a fleet token and closes the connections that it has open. The client has to pair again.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> CreatePairingAsync(
        HttpContext context,
        FleetPairingService pairing,
        HubCertificateProvider certificate,
        CancellationToken cancellationToken)
    {
        (CreateFleetPairingRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.CreateFleetPairingRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return HubApiResults.Invalid("/name", "invalid", "The name must have at most 64 characters and no control characters.");
        }

        string scopeType = request.Scope ?? FleetScopeTypes.All;
        if (!FleetScopeTypes.IsValid(scopeType))
        {
            return HubApiResults.Invalid("/scope", "invalid", "The scope must be all or tags.");
        }

        if (!AgentTags.TryNormalize(request.Tags, out IReadOnlyList<string> tags, out string? tagError))
        {
            return HubApiResults.Invalid("/tags", "invalid", tagError ?? "The tags are not valid.");
        }

        FleetScope scope;
        if (scopeType == FleetScopeTypes.All)
        {
            if (tags.Count > 0)
            {
                return HubApiResults.Invalid("/tags", "invalid", "The scope all takes no tags. Use the scope tags to restrict a token to agents with certain tags.");
            }

            scope = FleetScope.Everything;
        }
        else
        {
            if (tags.Count == 0)
            {
                return HubApiResults.Invalid("/tags", "required", "The scope tags needs at least one tag.");
            }

            scope = FleetScope.OfTags(tags);
        }

        string createdBy = HubPrincipal.Name(context.User) ?? "unknown";
        FleetPairingCode code = pairing.Start(scope, name, createdBy);
        context.Audit().Target = "fleet-pairing";

        // The code is in this answer and nowhere else: not cached, not logged, not audited.
        context.Response.Headers.CacheControl = "no-store";
        return HubApiResults.Json(
            new FleetPairingInfo(code.Code, code.ExpiresAt, code.Name, ToSummary(scope), certificate.Fingerprint),
            HubApiJsonContext.Default.FleetPairingInfo,
            StatusCodes.Status201Created);
    }

    private static IResult CancelPairing(HttpContext context, FleetPairingService pairing)
    {
        context.Audit().Target = "fleet-pairing";
        _ = pairing.Cancel();
        return Results.NoContent();
    }

    private static async Task<IResult> ListTokensAsync(FleetTokenService tokens, FleetService fleet, CancellationToken cancellationToken)
    {
        IReadOnlyList<FleetTokenInfo> all = await tokens.ListAsync(cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(
            new FleetTokenList([.. all.Select(token => ToSummary(token, fleet.CountConnections(token.Id)))]),
            HubApiJsonContext.Default.FleetTokenList);
    }

    private static async Task<IResult> RevokeTokenAsync(
        string id,
        HttpContext context,
        FleetTokenService tokens,
        FleetService fleet,
        CancellationToken cancellationToken)
    {
        context.Audit().Target = "fleet-token:" + id;
        FleetTokenInfo? revoked = await tokens.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        if (revoked is null)
        {
            return HubApiResults.NotFound("unknown_token", "There is no such fleet token.");
        }

        // The token no longer opens a link; the links that it opened before are closed now (4401).
        fleet.OnTokenRevoked(id);
        return Results.NoContent();
    }

    private static FleetScopeSummary ToSummary(FleetScope scope) => new(scope.Type, scope.Tags);

    private static FleetTokenSummary ToSummary(FleetTokenInfo token, int connections)
    {
        return new FleetTokenSummary(
            token.Id,
            token.Name,
            token.Prefix,
            ToSummary(token.Scope),
            token.ClientKind,
            token.ClientName,
            token.CreatedBy,
            token.CreatedAt,
            token.LastUsedAt,
            token.RevokedAt,
            token.Active,
            connections);
    }
}
