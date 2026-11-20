using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Api.Users;

/// <summary>
/// The authentication endpoints of the hub API (PLAN.md S09 item 1, S20 "Hub security" item 1): <c>/auth/setup</c>, <c>/auth/login</c>,
/// <c>/auth/logout</c>, <c>/auth/me</c> and <c>/auth/password</c>. Setup and login are the only routes that need no authentication; the rest
/// require a session or an API token (the password can only be changed in a session).
/// </summary>
public static class AuthEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    private const string PasswordScope = "password";

    /// <summary>Maps the endpoints below <c>/api/v1/auth</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Auth";
        RouteGroupBuilder auth = api.MapGroup("/auth");
        auth.MapGet("/setup", GetSetupAsync).AllowAnonymous()
            .Describe(tag, "getSetupState", "Tells whether first-run setup is available (only while the hub has no account).")
            .Returns<SetupState>()
            .Fails(StatusCodes.Status404NotFound);
        auth.MapPost("/setup", SetupAsync).AllowAnonymous()
            .Describe(tag, "setUp", "Creates the first administrator with the one-time setup token and signs in.")
            .Takes<HubSetupRequest>()
            .Returns<HubSessionInfo>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);
        auth.MapPost("/login", LoginAsync).AllowAnonymous()
            .Describe(tag, "logIn", "Signs in with a user name and a password; the session is a cookie.")
            .Takes<HubLoginRequest>()
            .Returns<HubSessionInfo>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);
        auth.MapPost("/logout", Logout)
            .Describe(tag, "logOut", "Ends the session of the cookie.")
            .ReturnsNothing();
        auth.MapGet("/me", Me)
            .Describe(tag, "getMe", "Tells who is signed in and with which role.")
            .Returns<HubSessionInfo>();
        auth.MapPost("/password", ChangeOwnPasswordAsync)
            .Describe(tag, "changeOwnPassword", "Changes the password of the signed-in user; their other sessions end.")
            .Takes<ChangeOwnPasswordRequest>()
            .ReturnsNothing()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> GetSetupAsync(SetupService setup, CancellationToken cancellationToken)
    {
        if (!await setup.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return SetupUnavailable();
        }

        return HubApiResults.Json(new SetupState(true), HubApiJsonContext.Default.SetupState);
    }

    private static async Task<IResult> SetupAsync(
        HttpContext context,
        SetupService setup,
        HubSessionStore sessions,
        ILogger<SetupService> logger,
        CancellationToken cancellationToken)
    {
        // "Unavailable once an account exists" is checked before anything else is looked at.
        if (!await setup.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return SetupUnavailable();
        }

        (HubSetupRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.HubSetupRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrEmpty(request.SetupToken) || string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return HubApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_request", "setupToken, username and password are required.");
        }

        NoteAttempt(context, request.Username);
        SetupResult result = await setup.SetupAsync(Caller(context), request.SetupToken, request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case SetupOutcome.Done:
                HubUser administrator = result.User!;
                SecurityLog.AdministratorCreated(logger, administrator.Username);
                return SignedIn(context, administrator, sessions.Create(new HubSession(administrator.Id, administrator.Username, administrator.Role)));
            case SetupOutcome.Unavailable:
                return SetupUnavailable();
            case SetupOutcome.InvalidToken:
                return HubApiResults.Problem(StatusCodes.Status401Unauthorized, "invalid_setup_token", "The setup token is wrong.");
            case SetupOutcome.InvalidUsername:
                return HubApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_username", result.Message ?? "The user name is not acceptable.");
            case SetupOutcome.WeakPassword:
                return HubApiResults.Problem(StatusCodes.Status400BadRequest, "weak_password", result.Message ?? "The password is not acceptable.");
            default:
                return LockedOut(context, result.RetryAfter, logger);
        }
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        LoginService login,
        ILogger<LoginService> logger,
        CancellationToken cancellationToken)
    {
        (HubLoginRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.HubLoginRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return HubApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_request", "username and password are required.");
        }

        NoteAttempt(context, request.Username);
        LoginResult result = await login.LoginAsync(Caller(context), request.Username, request.Password, cancellationToken).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case LoginOutcome.Done:
                return SignedIn(context, result.User!, result.SessionId!);
            case LoginOutcome.LockedOut:
                return LockedOut(context, result.RetryAfter, logger);
            default:
                return HubApiResults.Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", "The user name or the password is wrong.");
        }
    }

    private static IResult Logout(HttpContext context, HubSessionStore sessions)
    {
        sessions.Revoke(HubSessionCookie.Read(context.Request));
        HubSessionCookie.Delete(context);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext context)
    {
        var info = new HubSessionInfo(
            true,
            HubPrincipal.Name(context.User) ?? string.Empty,
            HubPrincipal.Role(context.User) ?? string.Empty,
            HubPrincipal.Method(context.User));
        return HubApiResults.Json(info, HubApiJsonContext.Default.HubSessionInfo);
    }

    private static async Task<IResult> ChangeOwnPasswordAsync(
        HttpContext context,
        UserService users,
        LoginRateLimiter limiter,
        ILogger<LoginService> logger,
        CancellationToken cancellationToken)
    {
        if (HubPrincipal.Method(context.User) != AuthMethods.Session || HubPrincipal.SubjectId(context.User) is not { } userId)
        {
            return HubApiResults.Problem(StatusCodes.Status403Forbidden, "session_required", "Only a signed-in user can change their password; an API token cannot.");
        }

        context.Audit().Target = "user:" + userId;
        (ChangeOwnPasswordRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.ChangeOwnPasswordRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return HubApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_request", "currentPassword and newPassword are required.");
        }

        // Guessing the current password through a stolen session is limited like a sign-in.
        string caller = Caller(context);
        if (limiter.GetLockout(PasswordScope, caller) is { } lockout)
        {
            return LockedOut(context, lockout, logger);
        }

        if (!await users.VerifyPasswordAsync(userId, request.CurrentPassword, cancellationToken).ConfigureAwait(false))
        {
            limiter.RecordFailure(PasswordScope, caller);
            return HubApiResults.Problem(StatusCodes.Status403Forbidden, "invalid_credentials", "The current password is wrong.");
        }

        limiter.Reset(PasswordScope, caller);
        if (users.ValidatePassword(request.NewPassword, HubPrincipal.Name(context.User)) is { } problem)
        {
            return HubApiResults.Problem(StatusCodes.Status400BadRequest, "weak_password", problem);
        }

        // The session that changed the password stays; every other session of the account ends.
        await users.SetPasswordAsync(userId, request.NewPassword, HubSessionCookie.Read(context.Request), cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>Starts the session: the cookie, and the audit entry names the user (the request itself is not authenticated yet).</summary>
    private static IResult SignedIn(HttpContext context, HubUser user, string sessionId)
    {
        AuditContext audit = context.Audit();
        audit.User = user.Username;
        audit.Role = user.Role;
        audit.Target = "user:" + user.Id;

        HubSessionCookie.Write(context, sessionId);
        context.Response.Headers.CacheControl = "no-store";
        return HubApiResults.Json(new HubSessionInfo(true, user.Username, user.Role, AuthMethods.Session), HubApiJsonContext.Default.HubSessionInfo);
    }

    private static IResult LockedOut(HttpContext context, TimeSpan? retryAfter, ILogger logger)
    {
        TimeSpan wait = retryAfter ?? TimeSpan.Zero;
        SecurityLog.LockedOut(logger, (int)Math.Ceiling(wait.TotalSeconds));
        return HubApiResults.LockedOut(context, wait);
    }

    private static IResult SetupUnavailable()
    {
        return HubApiResults.Problem(StatusCodes.Status404NotFound, "setup_unavailable", "First-run setup is not available: the hub already has an account.");
    }

    /// <summary>Tells the audit log which name a sign-in or setup attempt used; a name that does not look like a user name is not recorded.</summary>
    private static void NoteAttempt(HttpContext context, string username)
    {
        context.Audit().User = UserService.ValidateUsername(username) is null ? username : "invalid";
    }

    /// <summary>The key of the attempt limit: the client address as the hub sees it (the forwarded address behind a trusted proxy, nothing else).</summary>
    private static string Caller(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? "local";
    }
}
