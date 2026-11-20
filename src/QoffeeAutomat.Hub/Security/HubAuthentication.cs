using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Security;

/// <summary>Names of the authentication scheme and of the claims of the hub.</summary>
public static class HubAuthNames
{
    /// <summary>The authentication scheme: an API token in the <c>Authorization</c> header, otherwise the session cookie.</summary>
    public const string Scheme = "QoffeeAutomatHub";

    /// <summary>The claim with the way the caller signed in: <see cref="AuthMethods"/>.</summary>
    public const string AuthMethodClaim = "qoffeeautomat:auth_method";

    /// <summary>The claim with the id of the account (sessions) or of the token (API tokens).</summary>
    public const string SubjectIdClaim = "qoffeeautomat:subject_id";

    /// <summary>The prefix of the name of an API token principal, so that the audit log tells a token from a person.</summary>
    public const string TokenNamePrefix = "token:";
}

/// <summary>The ways a request can be authenticated.</summary>
public static class AuthMethods
{
    /// <summary>The session cookie of a signed-in user.</summary>
    public const string Session = "session";

    /// <summary>An API token in the <c>Authorization</c> header.</summary>
    public const string Token = "token";

    /// <summary>A fleet token on the fleet link of Home Assistant (T913). Only audit entries of the fleet link carry it; it never authenticates a request to <c>/api/v1</c>.</summary>
    public const string FleetToken = "fleet_token";

    /// <summary>A pairing code presented to <c>POST /hub/v1/pair</c> (T913).</summary>
    public const string PairingCode = "pairing_code";

    /// <summary>Home Assistant Supervisor ingress user headers on the ingress listener (T918).</summary>
    public const string Ingress = "ingress";

    /// <summary>Not authenticated.</summary>
    public const string None = "none";
}

/// <summary>
/// The session cookie (PLAN.md S09 item 1): <c>HttpOnly</c>, <c>SameSite=Strict</c>, <c>Secure</c> when the request came over HTTPS (also
/// when a trusted proxy says so), valid for the whole site, and without an expiry date, so the browser drops it when it closes. The server
/// side decides when a session ends.
/// </summary>
public static class HubSessionCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "qoffeeautomat.hub.session";

    /// <summary>Reads the session id from a request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The id, or null when the request has no session cookie.</returns>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies[Name];
    }

    /// <summary>Sets the session cookie on a response.</summary>
    /// <param name="context">The request context; its scheme decides the <c>Secure</c> flag.</param>
    /// <param name="sessionId">The session id.</param>
    public static void Write(HttpContext context, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Append(Name, sessionId, Options(context));
    }

    /// <summary>Removes the session cookie in the browser.</summary>
    /// <param name="context">The request context.</param>
    public static void Delete(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Delete(Name, Options(context));
    }

    private static CookieOptions Options(HttpContext context)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        };
    }
}

/// <summary>
/// Authenticates a request (PLAN.md S20 "Hub security" item 1): when it carries <c>Authorization: Bearer qht_...</c> only that counts,
/// and a token that is unknown, revoked or expired makes the request unauthenticated (the session cookie is not tried as a fallback);
/// otherwise the session cookie is checked. The principal carries the name, the role and the way of authentication; the authorization
/// policies of <see cref="HubPolicies"/> look at the role. A request that fails authorization gets a problem-details answer: 401 when
/// nobody is authenticated, 403 when the role is too low.
/// </summary>
public sealed class HubAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";
    private const string IngressUserNameHeader = "X-Remote-User-Name";
    private const string IngressUserIdHeader = "X-Remote-User-Id";

    private readonly HubSessionStore _sessions;
    private readonly ApiTokenService _tokens;
    private readonly HubSettings _settings;

    /// <summary>Creates the handler; the authentication system calls this.</summary>
    /// <param name="options">The scheme options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="tokens">The API tokens.</param>
    /// <param name="settings">The resolved hub settings.</param>
    public HubAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        HubSessionStore sessions,
        ApiTokenService tokens,
        HubSettings settings)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _tokens = tokens;
        _settings = settings;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (IngressGateMiddleware.IsIngressRequest(Context, _settings))
        {
            if (!IngressGateMiddleware.IsAllowedPeer(Context.Connection.RemoteIpAddress, _settings.IngressAllowedAddress))
            {
                return AuthenticateResult.Fail("Ingress peer is not the Home Assistant Supervisor.");
            }

            string? haUserName = ReadHeader(Request, IngressUserNameHeader);
            string? haUserId = ReadHeader(Request, IngressUserIdHeader);
            if (haUserName is not null || haUserId is not null)
            {
                string name = "ha:" + (haUserName ?? haUserId!);
                string subjectId = "ha:" + (haUserId ?? haUserName!);
                return Authenticated(name, _settings.IngressRole, AuthMethods.Ingress, subjectId);
            }
        }
        string authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string secret = authorization[BearerPrefix.Length..].Trim();
            ApiTokenInfo? token = await _tokens.AuthenticateAsync(secret, Context.RequestAborted).ConfigureAwait(false);
            if (token is null)
            {
                return AuthenticateResult.Fail("The API token is not valid.");
            }

            return Authenticated(
                HubAuthNames.TokenNamePrefix + token.Name,
                token.Role,
                AuthMethods.Token,
                token.Id);
        }

        HubSession? session = _sessions.TryValidate(HubSessionCookie.Read(Request));
        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        return Authenticated(session.Username, session.Role, AuthMethods.Session, session.UserId);
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        return HubApiResults.Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in or send an API token to use the hub API.").ExecuteAsync(Context);
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        return HubApiResults.Problem(StatusCodes.Status403Forbidden, "forbidden", "Your role does not allow this.").ExecuteAsync(Context);
    }

    private static AuthenticateResult Authenticated(string name, string role, string method, string subjectId)
    {
        Claim[] claims =
        [
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
            new Claim(HubAuthNames.AuthMethodClaim, method),
            new Claim(HubAuthNames.SubjectIdClaim, subjectId),
        ];
        var identity = new ClaimsIdentity(claims, HubAuthNames.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), HubAuthNames.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    private static string? ReadHeader(HttpRequest request, string name)
    {
        string value = request.Headers[name].ToString().Trim();
        return value.Length == 0 ? null : value;
    }
}

/// <summary>Reads the facts about the caller from the principal of a request.</summary>
public static class HubPrincipal
{
    /// <summary>The way the caller was authenticated.</summary>
    /// <param name="user">The principal.</param>
    /// <returns>One of <see cref="AuthMethods"/>.</returns>
    public static string Method(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity?.IsAuthenticated == true
            ? user.FindFirst(HubAuthNames.AuthMethodClaim)?.Value ?? AuthMethods.None
            : AuthMethods.None;
    }

    /// <summary>The name of the caller: the user name, or <c>token:&lt;name&gt;</c> for an API token.</summary>
    /// <param name="user">The principal.</param>
    /// <returns>The name, or null when nobody is authenticated.</returns>
    public static string? Name(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity?.IsAuthenticated == true ? user.FindFirst(ClaimTypes.Name)?.Value : null;
    }

    /// <summary>The role of the caller.</summary>
    /// <param name="user">The principal.</param>
    /// <returns>The role, or null when nobody is authenticated.</returns>
    public static string? Role(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity?.IsAuthenticated == true ? user.FindFirst(ClaimTypes.Role)?.Value : null;
    }

    /// <summary>The id of the account (session) or of the token (API token) of the caller.</summary>
    /// <param name="user">The principal.</param>
    /// <returns>The id, or null when nobody is authenticated.</returns>
    public static string? SubjectId(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.Identity?.IsAuthenticated == true ? user.FindFirst(HubAuthNames.SubjectIdClaim)?.Value : null;
    }
}
