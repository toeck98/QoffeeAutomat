using System.Globalization;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// The authentication endpoints of the management API (PLAN.md S08): <c>/auth/setup</c>, <c>/auth/login</c>,
/// <c>/auth/logout</c>, <c>/auth/me</c> and <c>/auth/password</c>. Setup and login are the only routes that
/// need no session; the rest require one.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>Maps the endpoints below <c>/api/v1/auth</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Auth";
        RouteGroupBuilder auth = api.MapGroup("/auth");
        auth.MapGet("/setup", GetSetupAsync).AllowAnonymous()
            .Describe(tag, "getSetupState", "Tells whether first-run setup is available (only until a password exists).")
            .Returns<SetupState>()
            .Fails(StatusCodes.Status404NotFound);
        auth.MapPost("/setup", SetupAsync).AllowAnonymous()
            .Describe(tag, "setUp", "Sets the first password with the setup token and signs in.")
            .Takes<SetupRequest>()
            .Returns<AuthMeResponse>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);
        auth.MapPost("/login", LoginAsync).AllowAnonymous()
            .Describe(tag, "logIn", "Signs in with the password; the session is a cookie.")
            .Takes<LoginRequest>()
            .Returns<AuthMeResponse>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);
        auth.MapPost("/logout", Logout)
            .Describe(tag, "logOut", "Ends the session.")
            .ReturnsNothing();
        auth.MapGet("/me", Me)
            .Describe(tag, "getMe", "Tells who is signed in.")
            .Returns<AuthMeResponse>();
        auth.MapPost("/password", ChangePasswordAsync)
            .Describe(tag, "changePassword", "Changes the password.")
            .Takes<ChangePasswordRequest>()
            .ReturnsNothing()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> GetSetupAsync(AccountService accounts, CancellationToken cancellationToken)
    {
        if (!await accounts.IsSetupAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return SetupUnavailable();
        }

        return ApiResults.Json(new SetupState(true), ApiJsonContext.Default.SetupState);
    }

    private static async Task<IResult> SetupAsync(
        HttpContext context,
        AccountService accounts,
        ILogger<AccountService> logger,
        CancellationToken cancellationToken)
    {
        // "Unavailable once a password exists" is checked before anything else is looked at.
        if (!await accounts.IsSetupAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return SetupUnavailable();
        }

        (SetupRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.SetupRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrEmpty(request.SetupToken) || string.IsNullOrEmpty(request.Password))
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request.", "Both setupToken and password are required.");
        }

        AccountResult result = await accounts.SetupAsync(Caller(context), request.SetupToken, request.Password, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            SecurityLog.PasswordSet(logger);
        }

        return Signed(context, result, logger);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        AccountService accounts,
        ILogger<AccountService> logger,
        CancellationToken cancellationToken)
    {
        (LoginRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.LoginRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request.", "Password is required.");
        }

        AccountResult result = await accounts.LoginAsync(Caller(context), request.Password, cancellationToken).ConfigureAwait(false);
        return Signed(context, result, logger);
    }

    private static IResult Logout(HttpContext context, SessionStore sessions)
    {
        sessions.Revoke(SessionCookie.Read(context.Request));
        SessionCookie.Delete(context);
        return Results.NoContent();
    }

    private static IResult Me()
    {
        return ApiResults.Json(new AuthMeResponse(true, AuthNames.AccountName), ApiJsonContext.Default.AuthMeResponse);
    }

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        AccountService accounts,
        ILogger<AccountService> logger,
        CancellationToken cancellationToken)
    {
        (ChangePasswordRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.ChangePasswordRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_request", "Invalid request.", "Both currentPassword and newPassword are required.");
        }

        AccountResult result = await accounts.ChangePasswordAsync(
            Caller(context),
            SessionCookie.Read(context.Request),
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case AccountOutcome.Done:
                SecurityLog.PasswordSet(logger);
                return Results.NoContent();
            case AccountOutcome.WeakPassword:
                return ApiResults.Problem(StatusCodes.Status400BadRequest, "weak_password", result.Message ?? "The new password is not acceptable.");
            case AccountOutcome.LockedOut:
                return LockedOut(context, result, logger);
            default:
                // 403, not 401: the session is fine, the current password is wrong.
                return ApiResults.Problem(StatusCodes.Status403Forbidden, "invalid_credentials", "The current password is wrong.");
        }
    }

    /// <summary>Turns the result of a setup or a sign-in into the response: a session cookie on success.</summary>
    private static IResult Signed(HttpContext context, AccountResult result, ILogger logger)
    {
        switch (result.Outcome)
        {
            case AccountOutcome.Done:
                SessionCookie.Write(context, result.SessionId!);
                context.Response.Headers.CacheControl = "no-store";
                return ApiResults.Json(new AuthMeResponse(true, AuthNames.AccountName), ApiJsonContext.Default.AuthMeResponse);
            case AccountOutcome.SetupUnavailable:
                return SetupUnavailable();
            case AccountOutcome.InvalidSetupToken:
                return ApiResults.Problem(StatusCodes.Status401Unauthorized, "invalid_setup_token", "The setup token is wrong.");
            case AccountOutcome.WeakPassword:
                return ApiResults.Problem(StatusCodes.Status400BadRequest, "weak_password", result.Message ?? "The password is not acceptable.");
            case AccountOutcome.LockedOut:
                return LockedOut(context, result, logger);
            default:
                return ApiResults.Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", "The password is wrong.");
        }
    }

    private static IResult LockedOut(HttpContext context, AccountResult result, ILogger logger)
    {
        int seconds = (int)Math.Ceiling((result.RetryAfter ?? TimeSpan.Zero).TotalSeconds);
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        SecurityLog.LoginLockedOut(logger, seconds);
        return ApiResults.Problem(StatusCodes.Status429TooManyRequests, "locked_out", "Too many failed attempts. Try again later.");
    }

    private static IResult SetupUnavailable()
    {
        return ApiResults.Problem(StatusCodes.Status404NotFound, "setup_unavailable", "First-run setup is not available: a password is already set.");
    }

    /// <summary>The key of the attempt limit: the remote address. Forwarding headers are not trusted.</summary>
    private static string Caller(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? "local";
    }
}
