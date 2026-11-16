using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Agent.Api;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>Names of the session authentication scheme and of the authorization policy of the management API.</summary>
public static class AuthNames
{
    /// <summary>The authentication scheme that reads the session cookie.</summary>
    public const string Scheme = "QoffeeAutomatSession";

    /// <summary>The policy that every route below <c>/api/v1</c> requires, except the documented unauthenticated ones (PLAN.md S08).</summary>
    public const string ManagementPolicy = "QoffeeAutomatManagement";

    /// <summary>The name of the one account.</summary>
    public const string AccountName = "administrator";
}

/// <summary>
/// The session cookie (PLAN.md S09 item 1): <c>HttpOnly</c>, <c>SameSite=Strict</c>, <c>Secure</c> when the
/// request came over HTTPS, valid for the whole site, and without an expiry date, so the browser drops it
/// when it closes. The server side decides when a session ends.
/// </summary>
public static class SessionCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "qoffeeautomat.session";

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
/// Authenticates a request by its session cookie. A request without a valid session is anonymous; the
/// authorization policy then answers 401 with a problem-details body.
/// </summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly SessionStore _sessions;

    /// <summary>Creates the handler; the authentication system calls this.</summary>
    /// <param name="options">The scheme options.</param>
    /// <param name="logger">The logger factory.</param>
    /// <param name="encoder">The URL encoder.</param>
    /// <param name="sessions">The sessions.</param>
    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SessionStore sessions)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? id = SessionCookie.Read(Request);
        if (!_sessions.TryValidate(id))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        Claim[] claims = [new Claim(ClaimTypes.Name, AuthNames.AccountName)];
        var identity = new ClaimsIdentity(claims, AuthNames.Scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthNames.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        return ApiResults.Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Sign in to use the management API.").ExecuteAsync(Context);
    }
}
