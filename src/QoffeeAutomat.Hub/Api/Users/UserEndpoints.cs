using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Api.Users;

/// <summary>
/// The user management endpoints of the hub API (PLAN.md S20 "Hub security" item 1, T903): list, create, change role or disabled flag, set
/// a password and delete local accounts. Only an administrator may call them. The hub never ends up without an enabled administrator, and every
/// change ends the sessions of the account it touches.
/// </summary>
public static class UserEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>Maps the endpoints below <c>/api/v1/users</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Users";
        api.MapGet("/users", ListAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "listUsers", "Lists the local accounts.")
            .Returns<UserList>();
        api.MapPost("/users", CreateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "createUser", "Creates a local account.")
            .Takes<CreateUserRequest>()
            .Returns<UserInfo>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, HubApiResults.UnprocessableContent);
        api.MapGet("/users/{id}", GetAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "getUser", "Returns a local account.")
            .Returns<UserInfo>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPatch("/users/{id}", UpdateAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "updateUser", "Changes the role and/or the disabled flag of an account; its sessions end.")
            .Takes<UpdateUserRequest>()
            .Returns<UserInfo>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, HubApiResults.UnprocessableContent);
        api.MapPost("/users/{id}/password", SetPasswordAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "setUserPassword", "Sets the password of an account; its sessions end.")
            .Takes<SetPasswordRequest>()
            .ReturnsNothing()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, HubApiResults.UnprocessableContent);
        api.MapDelete("/users/{id}", DeleteAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe(tag, "deleteUser", "Deletes an account; its sessions end.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListAsync(UserService users, CancellationToken cancellationToken)
    {
        IReadOnlyList<HubUser> all = await users.ListAsync(cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new UserList([.. all.Select(ToInfo)]), HubApiJsonContext.Default.UserList);
    }

    private static async Task<IResult> GetAsync(string id, UserService users, CancellationToken cancellationToken)
    {
        HubUser? user = await users.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return user is null
            ? UnknownUser()
            : HubApiResults.Json(ToInfo(user), HubApiJsonContext.Default.UserInfo);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, UserService users, CancellationToken cancellationToken)
    {
        (CreateUserRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.CreateUserRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        context.Audit().Target = "user:" + request.Username;
        if (UserService.ValidateUsername(request.Username) is { } usernameProblem)
        {
            return HubApiResults.Invalid("/username", "invalid", usernameProblem);
        }

        if (!HubRoles.IsValid(request.Role))
        {
            return HubApiResults.Invalid("/role", "invalid", "The role must be admin, operator or viewer.");
        }

        if (users.ValidatePassword(request.Password, request.Username) is { } passwordProblem)
        {
            return HubApiResults.Invalid("/password", "weak_password", passwordProblem);
        }

        (CreateUserOutcome outcome, HubUser? created) = await users.CreateAsync(request.Username, request.Password, request.Role, cancellationToken).ConfigureAwait(false);
        if (outcome == CreateUserOutcome.UsernameTaken || created is null)
        {
            return HubApiResults.Problem(StatusCodes.Status409Conflict, "username_taken", "An account with this user name exists.");
        }

        context.Audit().Target = "user:" + created.Id;
        return HubApiResults.Json(ToInfo(created), HubApiJsonContext.Default.UserInfo, StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(string id, HttpContext context, UserService users, CancellationToken cancellationToken)
    {
        context.Audit().Target = "user:" + id;
        (UpdateUserRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.UpdateUserRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (request.Role is null && request.Disabled is null)
        {
            return HubApiResults.Invalid("/", "required", "Give the role, disabled or both.");
        }

        if (request.Role is not null && !HubRoles.IsValid(request.Role))
        {
            return HubApiResults.Invalid("/role", "invalid", "The role must be admin, operator or viewer.");
        }

        switch (await users.UpdateAsync(id, request.Role, request.Disabled, cancellationToken).ConfigureAwait(false))
        {
            case UserChangeResult.NotFound:
                return UnknownUser();
            case UserChangeResult.LastAdministrator:
                return LastAdministrator();
            default:
                HubUser? updated = await users.FindAsync(id, cancellationToken).ConfigureAwait(false);
                return updated is null
                    ? UnknownUser()
                    : HubApiResults.Json(ToInfo(updated), HubApiJsonContext.Default.UserInfo);
        }
    }

    private static async Task<IResult> SetPasswordAsync(string id, HttpContext context, UserService users, CancellationToken cancellationToken)
    {
        context.Audit().Target = "user:" + id;
        (SetPasswordRequest? request, IResult? failure) = await HubApiResults.ReadBodyAsync(
            context.Request, HubApiJsonContext.Default.SetPasswordRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        HubUser? target = await users.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return UnknownUser();
        }

        if (users.ValidatePassword(request.NewPassword, target.Username) is { } problem)
        {
            return HubApiResults.Invalid("/newPassword", "weak_password", problem);
        }

        // An administrator who resets their own password stays signed in; every other session of the account ends.
        string? keep = string.Equals(HubPrincipal.SubjectId(context.User), id, StringComparison.Ordinal)
            ? HubSessionCookie.Read(context.Request)
            : null;
        return await users.SetPasswordAsync(id, request.NewPassword, keep, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : UnknownUser();
    }

    private static async Task<IResult> DeleteAsync(string id, HttpContext context, UserService users, CancellationToken cancellationToken)
    {
        context.Audit().Target = "user:" + id;
        return await users.DeleteAsync(id, cancellationToken).ConfigureAwait(false) switch
        {
            UserChangeResult.NotFound => UnknownUser(),
            UserChangeResult.LastAdministrator => LastAdministrator(),
            _ => Results.NoContent(),
        };
    }

    private static UserInfo ToInfo(HubUser user)
    {
        return new UserInfo(
            user.Id,
            user.Username,
            user.Role,
            user.Disabled,
            user.CreatedAt,
            user.UpdatedAt,
            user.PasswordChangedAt,
            user.LastLoginAt);
    }

    private static IResult UnknownUser() => HubApiResults.NotFound("unknown_user", "There is no such account.");

    private static IResult LastAdministrator()
    {
        return HubApiResults.Problem(
            StatusCodes.Status409Conflict,
            "last_administrator",
            "The hub must keep at least one enabled administrator.");
    }
}
