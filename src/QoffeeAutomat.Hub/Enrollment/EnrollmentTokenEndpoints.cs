using System.Net;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Enrollment;

/// <summary>
/// The enrollment token endpoints of the hub API (PLAN.md S20 "Enrollment and trust" step 1, T904): list, create and revoke. Only an administrator may
/// call them. The enrollment string, which holds the secret of the token, is in the answer to the creation and nowhere else.
/// </summary>
public static class EnrollmentTokenEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>The longest label of a token.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The most uses that a token may allow.</summary>
    public const int MaxUsesLimit = 10_000;

    /// <summary>The farthest expiry that a new token may have.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(3650);

    /// <summary>Maps the endpoints below <c>/api/v1/enrollment-tokens</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Enrollment";
        api.MapGet("/enrollment-tokens", ListAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "listEnrollmentTokens", "Lists the enrollment tokens, including revoked, expired and used-up ones. Secrets are never returned.")
            .Returns<EnrollmentTokenList>();
        api.MapPost("/enrollment-tokens", CreateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "createEnrollmentToken", "Creates an enrollment token with an optional expiry, a maximum number of uses, tags and an approval flag. The enrollment string with the secret is in the answer, and only there.")
            .Takes<CreateEnrollmentTokenRequest>()
            .Returns<CreatedEnrollmentToken>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, HubApiResults.UnprocessableContent);
        api.MapDelete("/enrollment-tokens/{id}", RevokeAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "revokeEnrollmentToken", "Revokes an enrollment token: no further agent can enroll with it. Agents that already enrolled keep working.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(EnrollmentTokenService tokens, TimeProvider time, CancellationToken cancellationToken)
    {
        IReadOnlyList<EnrollmentTokenInfo> all = await tokens.ListAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        return HubApiResults.Json(new EnrollmentTokenList([.. all.Select(token => ToSummary(token, now))]), HubApiJsonContext.Default.EnrollmentTokenList);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        EnrollmentTokenService tokens,
        HubCertificateProvider certificate,
        HubSigningKeyStore signingKey,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        (CreateEnrollmentTokenRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.CreateEnrollmentTokenRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl))
        {
            return HubApiResults.Invalid("/name", "invalid", "The name must have 1 to 64 characters and no control characters.");
        }

        if (!AgentTags.TryNormalize(request.Tags, out IReadOnlyList<string> tags, out string? tagError))
        {
            return HubApiResults.Invalid("/tags", "invalid", tagError ?? "The tags are not valid.");
        }

        int maxUses = request.MaxUses ?? 1;
        if (maxUses is < 1 or > MaxUsesLimit)
        {
            return HubApiResults.Invalid("/maxUses", "invalid", "The maximum number of uses must be between 1 and 10000.");
        }

        DateTimeOffset now = time.GetUtcNow();
        if (request.ExpiresAt is { } expiresAt && (expiresAt <= now || expiresAt > now + MaxLifetime))
        {
            return HubApiResults.Invalid("/expiresAt", "invalid", "The expiry must lie in the future, within ten years.");
        }

        (Uri? hubUrl, IResult? urlFailure) = ResolveHubUrl(context.Request, request.HubUrl);
        if (hubUrl is null)
        {
            return urlFailure!;
        }

        string createdBy = HubPrincipal.Name(context.User) ?? "unknown";
        IssuedEnrollmentToken issued = await tokens.CreateAsync(
            name, tags, request.RequiresApproval ?? false, maxUses, request.ExpiresAt, createdBy, cancellationToken).ConfigureAwait(false);
        context.Audit().Target = "enrollment-token:" + issued.Info.Id;

        string enrollmentString = EnrollmentString.Encode(new EnrollmentPayload(hubUrl, issued.Secret, signingKey.PublicKey, certificate.Fingerprint));

        // The enrollment string holds the secret and is in this answer and nowhere else: not cached, not logged.
        context.Response.Headers.CacheControl = "no-store";
        return HubApiResults.Json(
            new CreatedEnrollmentToken(ToSummary(issued.Info, now), enrollmentString),
            HubApiJsonContext.Default.CreatedEnrollmentToken,
            StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeAsync(string id, HttpContext context, EnrollmentTokenService tokens, CancellationToken cancellationToken)
    {
        context.Audit().Target = "enrollment-token:" + id;
        EnrollmentTokenInfo? revoked = await tokens.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        return revoked is null
            ? HubApiResults.NotFound("unknown_token", "There is no such enrollment token.")
            : Results.NoContent();
    }

    /// <summary>The address that goes into the enrollment string: the one the administrator gave, otherwise the address of this request.</summary>
    private static (Uri? Url, IResult? Failure) ResolveHubUrl(HttpRequest request, string? given)
    {
        string text = string.IsNullOrWhiteSpace(given)
            ? request.Scheme + "://" + request.Host.ToUriComponent() + request.PathBase.ToUriComponent()
            : given.Trim();

        if (text.Length > 256
            || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return (null, HubApiResults.Invalid("/hubUrl", "invalid", "The hub address must be an http or https address without credentials, query or fragment."));
        }

        // The credential travels to this address: plain HTTP is acceptable only on the same machine.
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            string detail = string.IsNullOrWhiteSpace(given)
                ? "This request did not arrive over HTTPS. Give the public https address of the hub as hubUrl."
                : "The hub address must use https unless it is on this machine.";
            return (null, HubApiResults.Invalid("/hubUrl", "invalid", detail));
        }

        return (uri, null);
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address));
    }

    private static EnrollmentTokenSummary ToSummary(EnrollmentTokenInfo token, DateTimeOffset now)
    {
        return new EnrollmentTokenSummary(
            token.Id,
            token.Name,
            token.Prefix,
            token.Tags,
            token.RequiresApproval,
            token.MaxUses,
            token.Uses,
            token.CreatedBy,
            token.CreatedAt,
            token.ExpiresAt,
            token.LastUsedAt,
            token.RevokedAt,
            token.IsUsable(now));
    }
}
