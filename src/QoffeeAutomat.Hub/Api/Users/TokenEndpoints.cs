using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Api.Users;

/// <summary>
/// The API token endpoints of the hub API (PLAN.md S20 "Hub security" items 1 and 3, T903): list, create and revoke tokens. A token has a role
/// and an optional expiry; its secret is shown once, when it is created, and the hub stores only its SHA-256 hash. Only an administrator may call
/// them.
/// </summary>
public static class TokenEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>The longest name of a token.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The farthest expiry that a new token may have.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(3650);

    /// <summary>Maps the endpoints below <c>/api/v1/tokens</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Tokens";
        api.MapGet("/tokens", ListAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "listTokens", "Lists the API tokens, including revoked and expired ones. Secrets are never returned.")
            .Returns<TokenList>();
        api.MapPost("/tokens", CreateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "createToken", "Creates an API token with a role and an optional expiry. The secret is in the answer, and only there.")
            .Takes<CreateTokenRequest>()
            .Returns<CreatedToken>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, HubApiResults.UnprocessableContent);
        api.MapDelete("/tokens/{id}", RevokeAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "revokeToken", "Revokes an API token; it stays in the list as revoked.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(ApiTokenService tokens, TimeProvider time, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiTokenInfo> all = await tokens.ListAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        return HubApiResults.Json(new TokenList([.. all.Select(token => ToInfo(token, now))]), HubApiJsonContext.Default.TokenList);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, ApiTokenService tokens, TimeProvider time, CancellationToken cancellationToken)
    {
        (CreateTokenRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.CreateTokenRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            return HubApiResults.Invalid("/name", "invalid", "The name must have 1 to 64 characters and no control characters.");
        }

        if (!HubRoles.IsValid(request.Role))
        {
            return HubApiResults.Invalid("/role", "invalid", "The role must be admin, operator or viewer.");
        }

        DateTimeOffset now = time.GetUtcNow();
        if (request.ExpiresAt is { } expiresAt && (expiresAt <= now || expiresAt > now + MaxLifetime))
        {
            return HubApiResults.Invalid("/expiresAt", "invalid", "The expiry must lie in the future, within ten years.");
        }

        string createdBy = HubPrincipal.Name(context.User) ?? "unknown";
        IssuedApiToken issued = await tokens.CreateAsync(name, request.Role, request.ExpiresAt, createdBy, cancellationToken).ConfigureAwait(false);
        context.Audit().Target = "token:" + issued.Info.Id;

        // The secret is in this answer and nowhere else: not cached, not logged.
        context.Response.Headers.CacheControl = "no-store";
        return HubApiResults.Json(new CreatedToken(ToInfo(issued.Info, now), issued.Secret), HubApiJsonContext.Default.CreatedToken, StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(string id, HttpContext context, ApiTokenService tokens, CancellationToken cancellationToken)
    {
        context.Audit().Target = "token:" + id;
        ApiTokenInfo? revoked = await tokens.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        return revoked is null
            ? HubApiResults.NotFound("unknown_token", "There is no such API token.")
            : Results.NoContent();
    }

    private static TokenInfo ToInfo(ApiTokenInfo token, DateTimeOffset now)
    {
        return new TokenInfo(
            token.Id,
            token.Name,
            token.Role,
            token.Prefix,
            token.CreatedBy,
            token.CreatedAt,
            token.ExpiresAt,
            token.LastUsedAt,
            token.RevokedAt,
            token.IsActive(now));
    }
}
